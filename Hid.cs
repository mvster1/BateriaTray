using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static BateriaTray.N;

namespace BateriaTray;

// Acesso HID compartilhado pelos protocolos. Tudo roda na thread da janela (sem locks).
internal static unsafe class Hid
{
    public const int BufSize = 512;
    public static nint Evt;
    public static byte* Tx, Rx;                     // buffers fixos (I/O overlapped exige endereço estável)
    public static byte* D;                          // última resposta, já sem o report id 0 (estilo hidapi)
    public static int DLen;
    public static readonly List<nint> Handles = new();

    public struct Iface
    {
        public string Path, Product;
        public ushort Vid, Pid, UsagePage, Usage, InLen, OutLen, FeatLen;
        public int Mi;                               // número da interface USB (MI_xx), -1 se não composto
        // Bateria no formato padrão HID (Generic Device Controls / Battery System), se houver
        public int BatType;                          // -1 = não tem, 0 = input report, 2 = feature report
        public byte BatReportId;
        public ushort BatPage, BatUsage, BatLink;
        public int BatMin, BatMax;
    }

    static readonly int[] BatTypes = { 2, 0 }; // feature primeiro, depois input

    public static void Init()
    {
        Evt = CreateEventW(0, 1, 0, null);
        Tx = (byte*)NativeMemory.AllocZeroed(BufSize);
        Rx = (byte*)NativeMemory.AllocZeroed(BufSize);
    }

    public static void CloseAll()
    {
        foreach (var h in Handles) CloseHandle(h);
        Handles.Clear();
    }

    // Lista todas as interfaces HID presentes (atributos, capacidades e bateria padrão, se existir).
    public static List<Iface> Enumerate()
    {
        var list = new List<Iface>();
        Guid g; HidD_GetHidGuid(&g);
        nint set = SetupDiGetClassDevsW(&g, null, 0, 0x02 | 0x10 /*PRESENT|DEVICEINTERFACE*/);
        if (set == -1) return list;
        SP_DEVICE_INTERFACE_DATA did = default;
        did.cbSize = (uint)sizeof(SP_DEVICE_INTERFACE_DATA);
        byte* detail = stackalloc byte[2048];
        char* name = stackalloc char[128];
        HIDP_VALUE_CAPS* vc = stackalloc HIDP_VALUE_CAPS[64];
        for (uint i = 0; SetupDiEnumDeviceInterfaces(set, 0, &g, i, &did) != 0; i++)
        {
            *(uint*)detail = 8; // sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) em x64
            if (SetupDiGetDeviceInterfaceDetailW(set, &did, detail, 2048, null, 0) == 0) continue;
            char* path = (char*)(detail + 4);
            var span = new ReadOnlySpan<char>(path, 1000);
            span = span[..span.IndexOf('\0')];

            nint q = CreateFileW(path, 0, 3, 0, 3, 0, 0); // só consulta
            if (q == -1) continue;
            HIDD_ATTRIBUTES a = default; a.Size = (uint)sizeof(HIDD_ATTRIBUTES);
            if (HidD_GetAttributes(q, &a) == 0) { CloseHandle(q); continue; }
            var it = new Iface { Path = span.ToString(), Vid = a.VendorID, Pid = a.ProductID, Mi = ParseMi(span), BatType = -1 };
            nint pp = 0;
            if (HidD_GetPreparsedData(q, &pp) != 0)
            {
                HIDP_CAPS caps = default;
                HidP_GetCaps(pp, &caps);
                it.UsagePage = caps.UsagePage; it.Usage = caps.Usage;
                it.InLen = caps.InputReportByteLength; it.OutLen = caps.OutputReportByteLength; it.FeatLen = caps.FeatureReportByteLength;
                // Procura um valor de bateria padrão: 0x06/0x20 (Battery Strength) ou 0x85/0x65|0x66 (State of Charge)
                foreach (int type in BatTypes)
                {
                    if (it.BatType >= 0) break;
                    ushort n = 64;
                    if (HidP_GetValueCaps(type, vc, &n, pp) != 0x00110000 /*HIDP_STATUS_SUCCESS*/) continue;
                    for (int k = 0; k < n; k++)
                    {
                        var c = vc[k];
                        if (c.IsRange != 0) continue;
                        bool bat = (c.UsagePage == 0x06 && c.Usage == 0x20) || (c.UsagePage == 0x85 && (c.Usage == 0x65 || c.Usage == 0x66));
                        if (!bat) continue;
                        it.BatType = type; it.BatReportId = c.ReportID; it.BatPage = c.UsagePage; it.BatUsage = c.Usage;
                        it.BatLink = c.LinkCollection; it.BatMin = c.LogicalMin; it.BatMax = c.LogicalMax;
                        break;
                    }
                }
                HidD_FreePreparsedData(pp);
            }
            name[0] = '\0';
            it.Product = HidD_GetProductString(q, name, 254) != 0 ? new string(name).Trim() : "";
            CloseHandle(q);
            list.Add(it);
        }
        SetupDiDestroyDeviceInfoList(set);
        return list;
    }

    static int ParseMi(ReadOnlySpan<char> path)
    {
        int i = path.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || i + 6 > path.Length) return -1;
        return Convert.ToInt32(path.Slice(i + 4, 2).ToString(), 16);
    }

    // Abre para leitura/escrita assíncrona (reports de entrada/saída).
    public static nint OpenRW(string path)
    {
        nint h;
        fixed (char* p = path) h = CreateFileW(p, 0xC0000000, 3, 0, 3, 0x40000000 /*OVERLAPPED*/, 0);
        if (h != -1) Handles.Add(h);
        return h;
    }

    // Abre sem acesso R/W: suficiente para feature reports, inclusive em mouse/teclado do sistema.
    public static nint OpenFeature(string path)
    {
        nint h;
        fixed (char* p = path) h = CreateFileW(p, 0, 3, 0, 3, 0, 0);
        if (h != -1) Handles.Add(h);
        return h;
    }

    public static void Release(nint h)
    {
        if (h == -1) return;
        Handles.Remove(h);
        CloseHandle(h);
    }

    // ---------------------------------------------------------------------------------------------
    // Escrita/leitura de reports (handle aberto com OpenRW)
    // ---------------------------------------------------------------------------------------------

    public static bool Write(nint h, uint len)
    {
        OVERLAPPED ov = default; ov.hEvent = Evt; ResetEvent(Evt);
        uint n = 0;
        if (WriteFile(h, Tx, len, &n, &ov) == 0)
        {
            if (Marshal.GetLastPInvokeError() != ERROR_IO_PENDING) return false;
            if (WaitForSingleObject(Evt, 1000) != 0) { CancelIo(h); GetOverlappedResult(h, &ov, &n, 1); return false; }
        }
        return GetOverlappedResult(h, &ov, &n, 0) != 0;
    }

    // Lê um report. 1 = leu (n bytes em Rx), 0 = tempo esgotado, -1 = erro (dispositivo sumiu).
    public static int Read(nint h, int timeoutMs, out uint n)
    {
        n = 0;
        OVERLAPPED ov = default; ov.hEvent = Evt; ResetEvent(Evt);
        uint got = 0;
        if (ReadFile(h, Rx, BufSize, &got, &ov) == 0)
        {
            if (Marshal.GetLastPInvokeError() != ERROR_IO_PENDING) return -1;
            if (WaitForSingleObject(Evt, (uint)Math.Max(0, timeoutMs)) != 0) { CancelIo(h); GetOverlappedResult(h, &ov, &got, 1); return 0; }
        }
        if (GetOverlappedResult(h, &ov, &got, 0) == 0) return -1;
        n = got;
        return 1;
    }

    // Envia Tx[0..txLen) e espera uma resposta que comece com 'match' (retorna 1) ou com 'alt' (retorna 2).
    // 0 = tempo esgotado. ioError = dispositivo sumiu. (Bytes crus: Rx[0] é o report id.)
    public static int Transact(nint h, uint txLen, int timeoutMs, ReadOnlySpan<byte> match, ReadOnlySpan<byte> alt, out bool ioError)
    {
        ioError = false;
        HidD_FlushQueue(h);
        if (!Write(h, txLen)) { ioError = true; return 0; }
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            long rem = deadline - Environment.TickCount64;
            if (rem <= 0) return 0;
            int r = Read(h, (int)rem, out uint n);
            if (r < 0) { ioError = true; return 0; }
            if (r == 0) return 0;
            if (StartsWith(Rx, n, match)) return 1;
            if (!alt.IsEmpty && StartsWith(Rx, n, alt)) return 2;
        }
    }

    // Consulta no estilo hidapi: req[0] é o report id (0 = sem id). A resposta fica em D/DLen já sem o
    // byte 0 quando o dispositivo não usa report id. 'pattern' compara com D (-1 = qualquer byte).
    // Retorna 1 = ok, 0 = sem resposta, -1 = dispositivo sumiu.
    public static int Query(nint h, int outLen, ReadOnlySpan<byte> req, ReadOnlySpan<short> pattern, int timeoutMs)
    {
        if (outLen <= 0 || outLen > BufSize) outLen = Math.Max(req.Length, 2);
        NativeMemory.Clear(Tx, (nuint)outLen);
        req.CopyTo(new Span<byte>(Tx, outLen));
        HidD_FlushQueue(h);
        if (!Write(h, (uint)outLen)) return -1;
        bool stripZero = req[0] == 0;
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            long rem = deadline - Environment.TickCount64;
            if (rem <= 0) return 0;
            int r = Read(h, (int)rem, out uint n);
            if (r <= 0) return r;
            int off = stripZero && n > 0 && Rx[0] == 0 ? 1 : 0;
            D = Rx + off; DLen = (int)n - off;
            if (Matches(pattern)) return 1;
        }
    }

    static bool Matches(ReadOnlySpan<short> p)
    {
        if (DLen < p.Length) return false;
        for (int i = 0; i < p.Length; i++) if (p[i] >= 0 && D[i] != p[i]) return false;
        return true;
    }

    static bool StartsWith(byte* b, uint n, ReadOnlySpan<byte> p)
    {
        if (n < p.Length) return false;
        for (int i = 0; i < p.Length; i++) if (b[i] != p[i]) return false;
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Feature reports (handle aberto com OpenFeature). Buffer = Tx/Rx, tamanho = FeatureReportByteLength.
    // ---------------------------------------------------------------------------------------------

    public static bool SetFeature(nint h, int len) => HidD_SetFeature(h, Tx, (uint)len) != 0;

    public static bool GetFeature(nint h, byte reportId, int len)
    {
        NativeMemory.Clear(Rx, (nuint)len);
        Rx[0] = reportId;
        return HidD_GetFeature(h, Rx, (uint)len) != 0;
    }

    public static void ClearTx(int len) => NativeMemory.Clear(Tx, (nuint)Math.Min(len, BufSize));

    // Esvazia a fila de reports de entrada já recebidos (sem esperar). Chama 'onFrame' para cada um.
    public static bool Drain(nint h, Action<uint> onFrame)
    {
        for (int i = 0; i < 64; i++)
        {
            int r = Read(h, 0, out uint n);
            if (r < 0) return false;
            if (r == 0) return true;
            onFrame(n);
        }
        return true;
    }
}
