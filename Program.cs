using System;
using System.IO;
using System.Runtime.InteropServices;
using static BateriaTray.N;

namespace BateriaTray;

internal sealed class Dev
{
    public readonly uint Id;
    public readonly bool IsMouse;
    public string Name;
    public nint H = -1;          // handle HID aberto (overlapped)
    public bool Present;         // receptor/dongle conectado
    public int Pct = -1;         // última % conhecida
    public int MilliVolts;
    public bool Charging, Full, Asleep;
    public string ReadAt = "";   // hora da última leitura válida
    public long NextPoll;
    public nint Icon;
    public bool Shown, LowWarned;
    public string LastSig;       // evita redesenhar o ícone sem mudança
    public Dev(uint id, bool mouse, string name) { Id = id; IsMouse = mouse; Name = name; }
}

internal static unsafe class App
{
    const uint WM_TRAY = 0x8001, WM_DESTROY = 0x0002, WM_TIMER = 0x0113, WM_DEVICECHANGE = 0x0219,
               WM_POWERBROADCAST = 0x0218, WM_SETTINGCHANGE = 0x001A, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    const int TICK_MS = 10_000, POLL_OK_MS = 60_000, POLL_RETRY_MS = 20_000;

    // Mouse Kysona (chip Compx, VID 0x3554): report 0x08, 17 bytes, checksum = 0x55 - soma.
    const ushort MOUSE_VID = 0x3554, MOUSE_UP = 0xFF02;
    static readonly ushort[] MousePids = { 0xF57C, 0xF57D, 0xF57E, 0xF512, 0xF5D5 };
    // Tabela tensão(mV) -> % do driver oficial (BatteryParam): 0%,5%,...,100%
    static readonly ushort[] MouseVolt = { 3050, 3420, 3480, 3540, 3600, 3660, 3720, 3760, 3800, 3840, 3880,
                                           3920, 3940, 3960, 3980, 4000, 4020, 4040, 4060, 4080, 4110 };
    // Headset MCHOSE V9 PRO (protocolo BLK): report 0x55, 64 bytes, cmd 0x65.
    const ushort HS_VID = 0x291D, HS_PID = 0x385D, HS_UP = 0xFF90;

    static readonly Dev Mouse = new(1, true, "Mouse");
    static readonly Dev Headset = new(2, false, "Headset");
    static readonly Dev[] Devs = { Headset, Mouse };

    static nint _hwnd, _evt;
    static uint _wmTaskbarCreated;
    static byte* _tx, _rx;
    static bool _lightTheme;
    static int _ticks, _promoteTries;
    static bool _showGlyph = true;

    static int Main(string[] args)
    {
        _evt = CreateEventW(0, 1, 0, null);
        _tx = (byte*)NativeMemory.AllocZeroed(128);
        _rx = (byte*)NativeMemory.AllocZeroed(128);

        if (args.Length >= 1 && args[0] == "--test")
            return RunTest(args.Length > 1 ? args[1] : ".");

        nint mutex;
        fixed (char* mn = "Local\\BateriaTray_8c1f2e") mutex = CreateMutexW(0, 1, mn);
        if (Marshal.GetLastPInvokeError() == ERROR_ALREADY_EXISTS) return 0;

        nint inst = GetModuleHandleW(null);
        fixed (char* cls = "BateriaTrayWnd")
        fixed (char* tbc = "TaskbarCreated")
        {
            WNDCLASSEXW wc = default;
            wc.cbSize = (uint)sizeof(WNDCLASSEXW);
            wc.lpfnWndProc = &WndProc;
            wc.hInstance = inst;
            wc.lpszClassName = cls;
            RegisterClassExW(&wc);
            // Janela top-level oculta (recebe WM_DEVICECHANGE / TaskbarCreated). Nunca é exibida.
            _hwnd = CreateWindowExW(0x80 /*WS_EX_TOOLWINDOW*/, cls, cls, 0x80000000 /*WS_POPUP*/, 0, 0, 0, 0, 0, 0, inst, 0);
            _wmTaskbarCreated = RegisterWindowMessageW(tbc);
        }

        ReadTheme();
        _showGlyph = Settings.GetShowGlyph();
        Rescan();
        SetTimer(_hwnd, 1, TICK_MS, 0);
        TrimMemory();

        MSG msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0) DispatchMessageW(&msg);
        GC.KeepAlive(mutex);
        return 0;
    }

    [UnmanagedCallersOnly]
    static nint WndProc(nint h, uint m, nuint w, nint l)
    {
        try
        {
            switch (m)
            {
                case WM_TRAY:
                    uint ev = (uint)(l & 0xFFFF);
                    if (ev == WM_RBUTTONUP) ShowMenu();
                    else if (ev == WM_LBUTTONUP) { foreach (var d in Devs) d.NextPoll = 0; Tick(); }
                    return 0;
                case WM_TIMER:
                    if (w == 2) { KillTimer(h, 2); Rescan(); }
                    else if (w == 3)
                    {
                        // O Windows 11 cria a entrada do ícone de forma assíncrona; tenta algumas vezes.
                        TrayPromote.Run();
                        if (++_promoteTries > 5) KillTimer(h, 3);
                    }
                    else Tick();
                    return 0;
                case WM_DEVICECHANGE:
                    if (w == 7 /*DBT_DEVNODES_CHANGED*/) SetTimer(h, 2, 1500, 0);
                    return 1;
                case WM_POWERBROADCAST:
                    if (w == 0x12 /*PBT_APMRESUMEAUTOMATIC*/) SetTimer(h, 2, 4000, 0);
                    return 1;
                case WM_SETTINGCHANGE:
                    ReadTheme();
                    foreach (var d in Devs) d.LastSig = null;
                    UpdateIcons();
                    break;
                case WM_DESTROY:
                    foreach (var d in Devs) RemoveIcon(d);
                    PostQuitMessage(0);
                    return 0;
                default:
                    if (m == _wmTaskbarCreated && m != 0)
                    {
                        foreach (var d in Devs) { d.Shown = false; d.LastSig = null; }
                        UpdateIcons();
                        return 0;
                    }
                    break;
            }
        }
        catch { /* nunca derrubar o processo por causa de um callback */ }
        return DefWindowProcW(h, m, w, l);
    }

    // ===================== agendamento =====================

    static void Tick()
    {
        long now = Environment.TickCount64;
        foreach (var d in Devs)
        {
            if (!d.Present || now < d.NextPoll) continue;
            bool ok = d.IsMouse ? PollMouse(d) : PollHeadset(d);
            d.NextPoll = now + (ok ? POLL_OK_MS : POLL_RETRY_MS);
        }
        UpdateIcons();
        if (++_ticks % 360 == 0) TrimMemory(); // a cada ~1h
    }

    static void Rescan()
    {
        foreach (var d in Devs) { if (d.H != -1) CloseHandle(d.H); d.H = -1; d.Present = false; }
        ScanHid();
        foreach (var d in Devs) d.NextPoll = 0;
        Tick();
    }

    static void TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    // ===================== HID =====================

    static void ScanHid()
    {
        Guid g; HidD_GetHidGuid(&g);
        nint set = SetupDiGetClassDevsW(&g, null, 0, 0x02 | 0x10 /*PRESENT|DEVICEINTERFACE*/);
        if (set == -1) return;
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
            if (!span.Contains("vid_3554", StringComparison.OrdinalIgnoreCase) &&
                !span.Contains("vid_291d", StringComparison.OrdinalIgnoreCase)) continue;

            nint q = CreateFileW(path, 0, 3, 0, 3, 0, 0); // só consulta (sem acesso R/W)
            if (q == -1) continue;
            HIDD_ATTRIBUTES a = default; a.Size = (uint)sizeof(HIDD_ATTRIBUTES);
            HIDP_CAPS caps = default;
            nint pp = 0;
            bool okAttr = HidD_GetAttributes(q, &a) != 0;
            if (okAttr && HidD_GetPreparsedData(q, &pp) != 0) { HidP_GetCaps(pp, &caps); HidD_FreePreparsedData(pp); }
            Dev target = null;
            if (okAttr && a.VendorID == MOUSE_VID && Array.IndexOf(MousePids, a.ProductID) >= 0 &&
                caps.UsagePage == MOUSE_UP && caps.OutputReportByteLength == 17) target = Mouse;
            else if (okAttr && a.VendorID == HS_VID && a.ProductID == HS_PID &&
                caps.UsagePage == HS_UP && caps.OutputReportByteLength == 64) target = Headset;
            if (target != null && target.H == -1)
            {
                name[0] = '\0';
                if (HidD_GetProductString(q, name, 254) != 0 && name[0] != '\0')
                    target.Name = new string(name).Trim();
            }
            CloseHandle(q);
            if (target == null || target.H != -1) continue;

            nint hnd = CreateFileW(path, 0xC0000000 /*GENERIC_READ|WRITE*/, 3, 0, 3, 0x40000000 /*OVERLAPPED*/, 0);
            if (hnd == -1) continue;
            target.H = hnd;
            target.Present = true;
        }
        SetupDiDestroyDeviceInfoList(set);
    }

    // Escreve um report e espera a resposta cujo rx[idx]==val (rx[0] é o report id). Retorna false em timeout.
    static bool Transact(nint h, uint txLen, byte reportId, int idx, byte val, int timeoutMs, out bool ioError)
    {
        ioError = false;
        HidD_FlushQueue(h);
        OVERLAPPED ov = default; ov.hEvent = _evt; ResetEvent(_evt);
        uint n = 0;
        if (WriteFile(h, _tx, txLen, &n, &ov) == 0)
        {
            if (Marshal.GetLastPInvokeError() != ERROR_IO_PENDING) { ioError = true; return false; }
            if (WaitForSingleObject(_evt, 1000) != 0) { CancelIo(h); GetOverlappedResult(h, &ov, &n, 1); ioError = true; return false; }
        }
        if (GetOverlappedResult(h, &ov, &n, 0) == 0) { ioError = true; return false; }

        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            long rem = deadline - Environment.TickCount64;
            if (rem <= 0) return false;
            ov = default; ov.hEvent = _evt; ResetEvent(_evt);
            if (ReadFile(h, _rx, 128, &n, &ov) == 0)
            {
                if (Marshal.GetLastPInvokeError() != ERROR_IO_PENDING) { ioError = true; return false; }
                if (WaitForSingleObject(_evt, (uint)rem) != 0) { CancelIo(h); GetOverlappedResult(h, &ov, &n, 1); return false; }
            }
            if (GetOverlappedResult(h, &ov, &n, 0) == 0) { ioError = true; return false; }
            if (n > idx && _rx[0] == reportId && _rx[idx] == val) return true;
        }
    }

    static void Lost(Dev d)
    {
        if (d.H != -1) CloseHandle(d.H);
        d.H = -1; d.Present = false;
        SetTimer(_hwnd, 2, 3000, 0); // tenta reabrir
    }

    static bool PollMouse(Dev d)
    {
        NativeMemory.Clear(_tx, 17);
        _tx[0] = 0x08; _tx[1] = 0x04; // comando: ler bateria
        byte sum = 0; for (int i = 0; i < 16; i++) sum += _tx[i];
        _tx[16] = (byte)(0x55 - sum);

        bool io = false, ok = false;
        for (int attempt = 0; attempt < 2 && !ok && !io; attempt++)
            ok = Transact(d.H, 17, 0x08, 1, 0x04, 250, out io);
        if (io) { Lost(d); return false; }
        if (!ok) { d.Asleep = true; return false; } // mouse dormindo: o receptor não repassa a consulta

        int raw = _rx[6];
        int mv = (_rx[8] << 8) | _rx[9];
        d.MilliVolts = mv;
        d.Pct = (mv >= 2500 && mv <= 4500) ? VoltToPct(mv) : Math.Min(raw, 100);
        d.Charging = _rx[7] == 1;
        d.Full = _rx[7] == 2;
        d.Asleep = false;
        d.ReadAt = Now();
        return true;
    }

    static int VoltToPct(int mv)
    {
        var t = MouseVolt;
        if (mv <= t[0]) return 0;
        if (mv >= t[^1]) return 100;
        for (int i = 1; i < t.Length; i++)
            if (mv < t[i]) return (i - 1) * 5 + (mv - t[i - 1]) * 5 / (t[i] - t[i - 1]);
        return 100;
    }

    static bool PollHeadset(Dev d)
    {
        NativeMemory.Clear(_tx, 64);
        _tx[0] = 0x55; _tx[1] = 0x65; _tx[2] = 0x01; // report 0x55, cmd 0x65 (bateria)
        bool io = false, ok = false;
        for (int attempt = 0; attempt < 2 && !ok && !io; attempt++)
            ok = Transact(d.H, 64, 0x55, 1, 0x65, 400, out io);
        if (io) { Lost(d); return false; }
        if (!ok) { d.Asleep = true; return false; }

        int level = _rx[2], status = _rx[3];
        if (status == 26) { d.Asleep = true; return true; } // headset desligado/dormindo
        d.Pct = Math.Min(level, 100);
        d.Charging = status == 3;
        d.Full = status == 4;
        d.Asleep = false;
        d.ReadAt = Now();
        return true;
    }

    // ===================== bandeja =====================

    static void UpdateIcons()
    {
        foreach (var d in Devs) UpdateIcon(d);
    }

    static void UpdateIcon(Dev d)
    {
        if (!d.Present) { RemoveIcon(d); return; }

        string text = d.Pct < 0 ? "–" : d.Pct.ToString();
        // Cor pelo nível: verde = cheio, amarelo = metade, vermelho = acabando.
        // (tons mais escuros no tema claro para manter contraste)
        uint color = d.Pct < 0 ? 0x8C8C8Cu
                   : d.Pct >= 60 ? (_lightTheme ? 0x1E9E50u : 0x3DDC84u)
                   : d.Pct > 20 ? (_lightTheme ? 0xC08400u : 0xFFC83Du)
                   : (_lightTheme ? 0xD93636u : 0xFF4D4Du);

        string state;
        if (d.Pct < 0)
            state = d.IsMouse ? "Aguardando leitura (mexa o mouse)" : (d.Asleep ? "Desligado" : "Aguardando leitura");
        else if (d.Asleep)
            state = $"{d.Pct}% · {(d.IsMouse ? "dormindo" : "desligado")} (lido às {d.ReadAt})";
        else
            state = $"{d.Pct}% · {(d.Charging ? "carregando" : d.Full ? "carga completa" : "descarregando")}";
        string tip = d.Name + "\n" + state;

        // Símbolo do dispositivo (pequeno, à esquerda do número), neutro; cinza se dormindo.
        int glyph = !_showGlyph ? Render.GlyphNone : d.IsMouse ? Render.GlyphMouse : Render.GlyphHeadset;
        // Símbolo: cinza = dormindo/sem leitura, azul = carregando, normal = em uso.
        uint gcolor = d.Pct < 0 || d.Asleep ? 0x8C8C8Cu
                    : d.Charging ? (_lightTheme ? 0x0078D4u : 0x4FC3F7u)
                    : _lightTheme ? 0x1A1A1Au : 0xFFFFFFu;
        string sig = text + "|" + color + "|" + glyph + "|" + gcolor + "|" + tip;
        if (!d.Shown || sig != d.LastSig)
        {
            nint icon = Render.MakeIcon(text, color, glyph, gcolor);
            NOTIFYICONDATAW nd = Nd(d.Id, icon, tip);

            // Aviso de bateria fraca (uma vez por ciclo de descarga)
            if (d.Pct >= 0 && !d.Asleep)
            {
                if (d.Charging || d.Full || d.Pct > 20) d.LowWarned = false;
                else if (d.Pct <= 15 && !d.LowWarned)
                {
                    d.LowWarned = true;
                    nd.uFlags |= NIF_INFO;
                    nd.dwInfoFlags = 2; // NIIF_WARNING
                    Copy("Bateria fraca", nd.szInfoTitle, 64);
                    Copy($"{d.Name}: {d.Pct}%", nd.szInfo, 256);
                }
            }
            d.Shown = Put(&nd, d.Shown);
            if (d.Icon != 0) DestroyIcon(d.Icon);
            d.Icon = icon;
            d.LastSig = sig;
        }
    }

    static NOTIFYICONDATAW Nd(uint id, nint icon, string tip)
    {
        NOTIFYICONDATAW nd = default;
        nd.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        nd.hWnd = _hwnd; nd.uID = id;
        nd.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        nd.uCallbackMessage = WM_TRAY;
        nd.hIcon = icon;
        Copy(tip, nd.szTip, 128);
        return nd;
    }

    static bool Put(NOTIFYICONDATAW* nd, bool shown)
    {
        if (shown) { Shell_NotifyIconW(NIM_MODIFY, nd); return true; }
        bool ok = Shell_NotifyIconW(NIM_ADD, nd) != 0;
        if (ok) { _promoteTries = 1; SetTimer(_hwnd, 3, 3000, 0); } // marca ícones novos como visíveis
        return ok;
    }

    static void RemoveIcon(Dev d)
    {
        RemoveOne(d.Id, ref d.Shown, ref d.Icon, ref d.LastSig);
    }

    static void RemoveOne(uint id, ref bool shown, ref nint icon, ref string sig)
    {
        if (shown)
        {
            NOTIFYICONDATAW nd = default;
            nd.cbSize = (uint)sizeof(NOTIFYICONDATAW);
            nd.hWnd = _hwnd; nd.uID = id;
            Shell_NotifyIconW(NIM_DELETE, &nd);
            shown = false;
        }
        if (icon != 0) { DestroyIcon(icon); icon = 0; }
        sig = null;
    }

    static void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        bool auto = AutoStart.IsEnabled();
        fixed (char* s1 = "Atualizar agora")
        fixed (char* s2 = "Iniciar com o Windows")
        fixed (char* s4 = "Mostrar ícone do dispositivo")
        fixed (char* s3 = "Sair")
        {
            AppendMenuW(menu, 0, 1, s1);
            AppendMenuW(menu, _showGlyph ? 0x8u : 0u, 3, s4);
            AppendMenuW(menu, auto ? 0x8u /*MF_CHECKED*/ : 0u, 2, s2);
            AppendMenuW(menu, 0x800 /*MF_SEPARATOR*/, 0, null);
            AppendMenuW(menu, 0, 9, s3);
        }
        POINT p; GetCursorPos(&p);
        SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenu(menu, 0x100 /*TPM_RETURNCMD*/ | 0x20 /*TPM_BOTTOMALIGN*/ | 0x8 /*RIGHTALIGN*/, p.x, p.y, 0, _hwnd, 0);
        DestroyMenu(menu);
        switch (cmd)
        {
            case 1: foreach (var d in Devs) d.NextPoll = 0; Tick(); break;
            case 2: AutoStart.Set(!auto); break;
            case 3:
                _showGlyph = !_showGlyph;
                Settings.SetShowGlyph(_showGlyph);
                foreach (var d in Devs) d.LastSig = null;
                UpdateIcons();
                break;
            case 9: DestroyWindow(_hwnd); break;
        }
    }

    static void ReadTheme()
    {
        uint v = 0, size = 4;
        fixed (char* k = "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize")
        fixed (char* n = "SystemUsesLightTheme")
            if (RegGetValueW(HKEY_CURRENT_USER, k, n, 0x10 /*RRF_RT_REG_DWORD*/, null, &v, &size) != 0) v = 0;
        _lightTheme = v == 1;
    }

    static void Copy(string s, char* dst, int cap)
    {
        int n = Math.Min(s.Length, cap - 1);
        for (int i = 0; i < n; i++) dst[i] = s[i];
        dst[n] = '\0';
    }

    static string Now()
    {
        SYSTEMTIME t; GetLocalTime(&t);
        return t.Hour.ToString("00") + ":" + t.Minute.ToString("00");
    }

    // ===================== modo de teste =====================
    // BateriaTray.exe --test <pasta> : faz uma leitura, grava resultado em teste.txt e os ícones em .bmp
    static int RunTest(string dir)
    {
        var sb = new System.Text.StringBuilder();
        ScanHid();
        foreach (var d in Devs)
        {
            sb.Append(d.IsMouse ? "[mouse] " : "[headset] ").Append(d.Name).Append(" presente=").Append(d.Present).AppendLine();
            if (!d.Present) continue;
            bool ok = d.IsMouse ? PollMouse(d) : PollHeadset(d);
            sb.Append("  ok=").Append(ok).Append(" pct=").Append(d.Pct).Append(" mV=").Append(d.MilliVolts)
              .Append(" carregando=").Append(d.Charging).Append(" cheio=").Append(d.Full).Append(" dormindo=").Append(d.Asleep).AppendLine();
            if (ok)
            {
                sb.Append("  rx=");
                int len = d.IsMouse ? 17 : 8;
                for (int i = 0; i < len; i++) sb.Append(_rx[i].ToString("x2")).Append(' ');
                sb.AppendLine();
            }
        }
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "teste.txt"), sb.ToString());
        var items = new (string, uint, int)[] { ("100", 0x3DDC84u, Render.GlyphHeadset), ("85", 0x3DDC84u, Render.GlyphMouse),
            ("60", 0x3DDC84u, Render.GlyphHeadset), ("45", 0xFFC83Du, Render.GlyphMouse), ("21", 0xFFC83Du, Render.GlyphHeadset),
            ("20", 0xFF4D4Du, Render.GlyphMouse), ("8", 0xFF4D4Du, Render.GlyphHeadset), ("–", 0x8C8C8Cu, Render.GlyphMouse) };
        Render.DumpStrip(Path.Combine(dir, "strip.bmp"), items);
        return 0;
    }
}

// Windows 11 esconde ícones novos na área "^". Na primeira vez, marca os nossos como visíveis
// (HKCU\Control Panel\NotifyIconSettings\*\IsPromoted = 1). Se o usuário já escolheu algo, respeita.
internal static unsafe class TrayPromote
{
    public static bool Run()
    {
        nint root;
        fixed (char* k = "Control Panel\\NotifyIconSettings")
            if (RegOpenKeyExW(HKEY_CURRENT_USER, k, 0, 0x20019 /*KEY_READ*/, &root) != 0) return false;
        bool found = false;
        char* name = stackalloc char[256];
        char* path = stackalloc char[1024];
        for (uint i = 0; ; i++)
        {
            uint len = 256;
            if (RegEnumKeyExW(root, i, name, &len, 0, null, null, null) != 0) break;
            nint sub;
            if (RegOpenKeyExW(root, name, 0, 0x20019 | 0x2 /*READ|SET_VALUE*/, &sub) != 0) continue;
            uint type, size = 2046;
            bool ours = false;
            fixed (char* v = "ExecutablePath")
                if (RegQueryValueExW(sub, v, 0, &type, (byte*)path, &size) == 0 && type == 1)
                {
                    path[Math.Min(size / 2, 1023)] = '\0';
                    var s = new ReadOnlySpan<char>(path, (int)(size / 2)).TrimEnd('\0');
                    ours = s.EndsWith("\\BateriaTray.exe", StringComparison.OrdinalIgnoreCase);
                }
            if (ours)
            {
                found = true;
                fixed (char* p = "IsPromoted")
                {
                    uint t2, sz2 = 4, val;
                    if (RegQueryValueExW(sub, p, 0, &t2, (byte*)&val, &sz2) == 2 /*não existe*/)
                    {
                        val = 1;
                        RegSetValueExW(sub, p, 0, 4 /*REG_DWORD*/, (byte*)&val, 4);
                    }
                }
            }
            RegCloseKey(sub);
        }
        RegCloseKey(root);
        return found;
    }
}

internal static unsafe class Settings
{
    const string Key = @"Software\BateriaTray";

    public static bool GetShowGlyph()
    {
        uint v = 1, size = 4;
        fixed (char* k = Key) fixed (char* n = "ShowGlyph")
            if (RegGetValueW(HKEY_CURRENT_USER, k, n, 0x10, null, &v, &size) != 0) v = 1;
        return v != 0;
    }

    public static void SetShowGlyph(bool on)
    {
        nint hk; uint disp, v = on ? 1u : 0u;
        fixed (char* k = Key)
            if (RegCreateKeyExW(HKEY_CURRENT_USER, k, 0, null, 0, 0x20006 /*KEY_WRITE*/, 0, &hk, &disp) != 0) return;
        fixed (char* n = "ShowGlyph") RegSetValueExW(hk, n, 0, 4 /*REG_DWORD*/, (byte*)&v, 4);
        RegCloseKey(hk);
    }
}

internal static unsafe class AutoStart
{
    const string Key = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    const string Name = "BateriaTray";

    public static bool IsEnabled()
    {
        nint hk;
        fixed (char* k = Key) if (RegOpenKeyExW(HKEY_CURRENT_USER, k, 0, 0x20019 /*KEY_READ*/, &hk) != 0) return false;
        uint type, size = 0; int r;
        fixed (char* n = Name) r = RegQueryValueExW(hk, n, 0, &type, null, &size);
        RegCloseKey(hk);
        return r == 0;
    }

    public static void Set(bool on)
    {
        nint hk;
        fixed (char* k = Key) if (RegOpenKeyExW(HKEY_CURRENT_USER, k, 0, 0x20006 /*KEY_WRITE*/, &hk) != 0) return;
        fixed (char* n = Name)
        {
            if (on)
            {
                string v = "\"" + Environment.ProcessPath + "\"";
                fixed (char* pv = v) RegSetValueExW(hk, n, 0, 1 /*REG_SZ*/, (byte*)pv, (uint)((v.Length + 1) * 2));
            }
            else RegDeleteValueW(hk, n);
        }
        RegCloseKey(hk);
    }
}
