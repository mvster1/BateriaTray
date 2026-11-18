using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static BateriaTray.N;

namespace BateriaTray;

// Acesso HID compartilhado pelos protocolos. Tudo roda na thread da janela (sem locks).
internal static unsafe class Hid
{
    public static nint Evt;
    public static byte* Tx, Rx;                     // buffers fixos (I/O overlapped exige endereço estável)
    public static readonly List<nint> Handles = new();

    public struct Iface
    {
        public string Path, Product;
        public ushort Vid, Pid, UsagePage, Usage, InLen, OutLen, FeatLen;
    }

    public static void Init()
    {
        Evt = CreateEventW(0, 1, 0, null);
        Tx = (byte*)NativeMemory.AllocZeroed(128);
        Rx = (byte*)NativeMemory.AllocZeroed(128);
    }

    public static void CloseAll()
    {
        foreach (var h in Handles) CloseHandle(h);
        Handles.Clear();
    }

    // Lista as interfaces HID presentes cujo caminho contém algum dos VIDs (ex.: "vid_046d").
    public static List<Iface> Enumerate(string[] vids)
    {
        var list = new List<Iface>();
        Guid g; HidD_GetHidGuid(&g);
        nint set = SetupDiGetClassDevsW(&g, null, 0, 0x02 | 0x10 /*PRESENT|DEVICEINTERFACE*/);
        if (set == -1) return list;
        SP_DEVICE_INTERFACE_DATA did = default;
        did.cbSize = (uint)sizeof(SP_DEVICE_INTERFACE_DATA);
        byte* detail = stackalloc byte[2048];
        char* name = stackalloc char[128];
        for (uint i = 0; SetupDiEnumDeviceInterfaces(set, 0, &g, i, &did) != 0; i++)
        {
            *(uint*)detail = 8; // sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) em x64
            if (SetupDiGetDeviceInterfaceDetailW(set, &did, detail, 2048, null, 0) == 0) continue;
            char* path = (char*)(detail + 4);
            var span = new ReadOnlySpan<char>(path, 1000);
            span = span[..span.IndexOf('\0')];
            bool want = false;
            foreach (var v in vids) if (span.Contains(v, StringComparison.OrdinalIgnoreCase)) { want = true; break; }
            if (!want) continue;

            nint q = CreateFileW(path, 0, 3, 0, 3, 0, 0); // só consulta
            if (q == -1) continue;
            HIDD_ATTRIBUTES a = default; a.Size = (uint)sizeof(HIDD_ATTRIBUTES);
            HIDP_CAPS caps = default;
            nint pp = 0;
            if (HidD_GetAttributes(q, &a) != 0)
            {
                if (HidD_GetPreparsedData(q, &pp) != 0) { HidP_GetCaps(pp, &caps); HidD_FreePreparsedData(pp); }
                name[0] = '\0';
                string product = HidD_GetProductString(q, name, 254) != 0 ? new string(name).Trim() : "";
                list.Add(new Iface
                {
                    Path = span.ToString(), Product = product, Vid = a.VendorID, Pid = a.ProductID,
                    UsagePage = caps.UsagePage, Usage = caps.Usage, InLen = caps.InputReportByteLength,
                    OutLen = caps.OutputReportByteLength, FeatLen = caps.FeatureReportByteLength
                });
            }
            CloseHandle(q);
        }
        SetupDiDestroyDeviceInfoList(set);
        return list;
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

    // Envia Tx[0..txLen) e espera uma resposta que comece com 'match' (retorna 1) ou com 'alt' (retorna 2).
    // 0 = tempo esgotado. ioError = dispositivo sumiu.
    public static int Transact(nint h, uint txLen, int timeoutMs, ReadOnlySpan<byte> match, ReadOnlySpan<byte> alt, out bool ioError)
    {
        ioError = false;
        HidD_FlushQueue(h);
        OVERLAPPED ov = default; ov.hEvent = Evt; ResetEvent(Evt);
        uint n = 0;
        if (WriteFile(h, Tx, txLen, &n, &ov) == 0)
        {
            if (Marshal.GetLastPInvokeError() != ERROR_IO_PENDING) { ioError = true; return 0; }
            if (WaitForSingleObject(Evt, 1000) != 0) { CancelIo(h); GetOverlappedResult(h, &ov, &n, 1); ioError = true; return 0; }
        }
        if (GetOverlappedResult(h, &ov, &n, 0) == 0) { ioError = true; return 0; }

        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            long rem = deadline - Environment.TickCount64;
            if (rem <= 0) return 0;
            ov = default; ov.hEvent = Evt; ResetEvent(Evt);
            if (ReadFile(h, Rx, 128, &n, &ov) == 0)
            {
                if (Marshal.GetLastPInvokeError() != ERROR_IO_PENDING) { ioError = true; return 0; }
                if (WaitForSingleObject(Evt, (uint)rem) != 0) { CancelIo(h); GetOverlappedResult(h, &ov, &n, 1); return 0; }
            }
            if (GetOverlappedResult(h, &ov, &n, 0) == 0) { ioError = true; return 0; }
            if (StartsWith(n, match)) return 1;
            if (!alt.IsEmpty && StartsWith(n, alt)) return 2;
        }
    }

    static bool StartsWith(uint n, ReadOnlySpan<byte> p)
    {
        if (n < p.Length) return false;
        for (int i = 0; i < p.Length; i++) if (Rx[i] != p[i]) return false;
        return true;
    }
}
