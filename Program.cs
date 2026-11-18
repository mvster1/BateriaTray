using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using static BateriaTray.N;

namespace BateriaTray;

internal static unsafe class App
{
    const uint WM_TRAY = 0x8001, WM_DESTROY = 0x0002, WM_TIMER = 0x0113, WM_DEVICECHANGE = 0x0219,
               WM_POWERBROADCAST = 0x0218, WM_SETTINGCHANGE = 0x001A, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    const int TICK_MS = 10_000, POLL_OK_MS = 60_000, POLL_RETRY_MS = 20_000, RESCAN_MS = 300_000;

    static readonly List<Dev> Devs = new();
    static nint _hwnd;
    static uint _wmTaskbarCreated;
    static bool _lightTheme, _showGlyph = true;
    static int _ticks, _promoteTries;
    static long _lastScan;

    static int Main(string[] args)
    {
        Hid.Init();
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
                    else if (ev == WM_LBUTTONUP) RefreshNow();
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

    // ===================== varredura e leitura =====================

    static void RefreshNow()
    {
        foreach (var d in Devs) d.NextPoll = 0;
        Tick();
    }

    static void Tick()
    {
        long now = Environment.TickCount64;
        // Revarre de tempos em tempos: acha dispositivos Logitech/Razer que estavam dormindo.
        if (now - _lastScan >= RESCAN_MS) { Rescan(); return; }
        foreach (var d in Devs)
        {
            if (!d.Present || now < d.NextPoll) continue;
            var r = Poll(d);
            if (r == PollResult.Lost) { d.H = -1; d.Asleep = true; SetTimer(_hwnd, 2, 3000, 0); }
            else if (r == PollResult.Asleep) d.Asleep = true;
            d.NextPoll = now + (r == PollResult.Ok ? POLL_OK_MS : POLL_RETRY_MS);
        }
        UpdateIcons();
        if (++_ticks % 360 == 0) TrimMemory(); // a cada ~1h
    }

    static PollResult Poll(Dev d)
    {
        // Protocolos com interfaces candidatas abrem o handle sob demanda; os demais precisam dele aberto.
        if (d.Proto != Proto.Bluetooth && d.Cands == null && d.H == -1) return PollResult.Asleep;
        try
        {
            return d.Proto switch
            {
                Proto.Compx => Compx.Poll(d),
                Proto.Blk => Blk.Poll(d),
                Proto.Logitech => Logitech.Poll(d),
                Proto.Razer => Razer.Poll(d),
                Proto.Bluetooth => Bluetooth.Poll(d),
                _ => Brands.Poll(d),
            };
        }
        catch { return PollResult.Asleep; }
    }

    static List<Dev> Scan(out List<Hid.Iface> ifs)
    {
        Hid.CloseAll();
        var found = new List<Dev>();
        ifs = Hid.Enumerate();
        try { Compx.Scan(ifs, found); } catch { }
        try { Blk.Scan(ifs, found); } catch { }
        try { Logitech.Scan(ifs, found); } catch { }
        try { Razer.Scan(ifs, found); } catch { }
        try { Bluetooth.Scan(found); } catch { }
        Brands.Scan(ifs, found); // demais marcas + HID genérico (por último)
        return found;
    }

    static void Rescan()
    {
        _lastScan = Environment.TickCount64;
        var found = Scan(out var ifs);

        // Junta com a lista atual mantendo o estado (última %, horário) de quem já era conhecido.
        foreach (var f in found)
        {
            var e = Devs.Find(x => x.Key == f.Key);
            if (e == null) { f.Present = true; Devs.Add(f); continue; }
            // Mantém a interface que já funcionava, se ela ainda existir
            int keep = e.Locked && e.Cands != null && f.Cands != null ? f.Cands.FindIndex(c => c.Path == e.Path) : -1;
            e.H = f.H; e.H2 = f.H2; e.Path = f.Path; e.DevIdx = f.DevIdx; e.FeatIdx = f.FeatIdx; e.Feat = f.Feat;
            e.TxId = f.TxId; e.InstanceId = f.InstanceId; e.Kind = f.Kind; e.Present = true;
            e.Vid = f.Vid; e.Pid = f.Pid; e.Bat = f.Bat; e.OutLen = f.OutLen; e.FeatLen = f.FeatLen;
            e.Cands = f.Cands; e.NeedConfirm = f.NeedConfirm;
            if (e.Proto != Proto.Xenta) e.Name = f.Name; // Attack Shark: o nome vem dos quadros recebidos
            if (keep >= 0) e.CandIdx = keep; else { e.CandIdx = 0; e.Locked = false; }
            if (f.Proto != Proto.CorsairBragi) e.Sub = f.Sub;
        }
        for (int i = Devs.Count - 1; i >= 0; i--)
        {
            var d = Devs[i];
            if (found.Exists(f => f.Key == d.Key)) continue;
            // Logitech/Razer dormindo não respondem à varredura: mantém enquanto o receptor estiver conectado.
            if ((d.Proto == Proto.Logitech || d.Proto == Proto.Razer) && d.Path != null && ifs.Exists(x => x.Path == d.Path))
            { d.H = -1; d.H2 = -1; d.Asleep = true; continue; }
            RemoveIcon(d);
            Devs.RemoveAt(i);
        }
        foreach (var d in Devs) d.NextPoll = 0;
        Tick();
    }

    static void TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    // ===================== bandeja =====================

    static void UpdateIcons()
    {
        foreach (var d in Devs) UpdateIcon(d);
    }

    static string AsleepWord(Dev d) =>
        d.Proto == Proto.Bluetooth ? "desconectado" : d.Kind == Kind.Headset ? "desligado" : "dormindo";

    static void UpdateIcon(Dev d)
    {
        if (!d.Present || d.Hidden || (d.NeedConfirm && !d.Confirmed)) { RemoveIcon(d); return; }

        string text = d.Pct < 0 ? "–" : d.Pct.ToString();
        // Cor pelo nível: verde = cheio, amarelo = metade, vermelho = acabando.
        // (tons mais escuros no tema claro para manter contraste)
        uint color = d.Pct < 0 ? 0x8C8C8Cu
                   : d.Pct >= 60 ? (_lightTheme ? 0x1E9E50u : 0x3DDC84u)
                   : d.Pct > 20 ? (_lightTheme ? 0xC08400u : 0xFFC83Du)
                   : (_lightTheme ? 0xD93636u : 0xFF4D4Du);

        string state;
        if (d.Pct < 0)
            state = d.Asleep && d.Kind == Kind.Headset ? "Desligado"
                  : "Aguardando leitura" + (d.Kind == Kind.Mouse ? " (mexa o mouse)" : d.Asleep ? " (use o dispositivo)" : "");
        else if (d.Asleep)
            state = $"{d.Pct}% · {AsleepWord(d)} (lido às {d.ReadAt})";
        else
            state = d.Proto == Proto.Bluetooth ? $"{d.Pct}% · Bluetooth"
                  : $"{d.Pct}% · {(d.Charging ? "carregando" : d.Full ? "carga completa" : "descarregando")}";
        string tip = d.Name + "\n" + state;

        // Símbolo do dispositivo (pequeno, à esquerda do número).
        // Cinza = dormindo/sem leitura, azul = carregando, normal = em uso.
        int glyph = !_showGlyph ? Render.GlyphNone : d.Kind switch
        {
            Kind.Mouse => Render.GlyphMouse,
            Kind.Headset => Render.GlyphHeadset,
            Kind.Keyboard => Render.GlyphKeyboard,
            _ => Render.GlyphOther,
        };
        uint gcolor = d.Pct < 0 || d.Asleep ? 0x8C8C8Cu
                    : d.Charging ? (_lightTheme ? 0x0078D4u : 0x4FC3F7u)
                    : _lightTheme ? 0x1A1A1Au : 0xFFFFFFu;
        string sig = text + "|" + color + "|" + glyph + "|" + gcolor + "|" + tip;
        if (d.Shown && sig == d.LastSig) return;

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
        if (d.Shown)
        {
            NOTIFYICONDATAW nd = default;
            nd.cbSize = (uint)sizeof(NOTIFYICONDATAW);
            nd.hWnd = _hwnd; nd.uID = d.Id;
            Shell_NotifyIconW(NIM_DELETE, &nd);
            d.Shown = false;
        }
        if (d.Icon != 0) { DestroyIcon(d.Icon); d.Icon = 0; }
        d.LastSig = null;
    }

    static void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        bool auto = AutoStart.IsEnabled();
        fixed (char* s1 = "Atualizar agora")
        fixed (char* s5 = "Procurar dispositivos")
        fixed (char* s2 = "Iniciar com o Windows")
        fixed (char* s4 = "Mostrar ícone do dispositivo")
        fixed (char* s3 = "Sair")
        {
            AppendMenuW(menu, 0, 1, s1);
            AppendMenuW(menu, 0, 4, s5);
            AppendMenuW(menu, 0x800 /*MF_SEPARATOR*/, 0, null);
            AppendMenuW(menu, _showGlyph ? 0x8u : 0u, 3, s4);
            AppendMenuW(menu, auto ? 0x8u /*MF_CHECKED*/ : 0u, 2, s2);
            AppendMenuW(menu, 0x800, 0, null);
            AppendMenuW(menu, 0, 9, s3);
        }
        POINT p; GetCursorPos(&p);
        SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenu(menu, 0x100 /*TPM_RETURNCMD*/ | 0x20 /*TPM_BOTTOMALIGN*/ | 0x8 /*RIGHTALIGN*/, p.x, p.y, 0, _hwnd, 0);
        DestroyMenu(menu);
        switch (cmd)
        {
            case 1: RefreshNow(); break;
            case 4: Rescan(); break;
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

    // ===================== modo de teste =====================
    // BateriaTray.exe --test <pasta> : varre, lê cada dispositivo uma vez e grava teste.txt (+ prévia dos ícones)
    static int RunTest(string dir)
    {
        var sb = new System.Text.StringBuilder();
        var found = Scan(out var ifs);
        sb.Append("Interfaces HID consideradas: ").Append(ifs.Count).AppendLine();
        foreach (var i in ifs)
            sb.Append("  ").Append(i.Vid.ToString("X4")).Append(':').Append(i.Pid.ToString("X4"))
              .Append(" up=").Append(i.UsagePage.ToString("X4")).Append(" in=").Append(i.InLen).Append(" out=").Append(i.OutLen)
              .Append(" feat=").Append(i.FeatLen).Append(' ').Append(i.Product).AppendLine();
        sb.Append("Dispositivos com bateria: ").Append(found.Count).AppendLine();
        foreach (var d in found)
        {
            var r = Poll(d);
            sb.Append('[').Append(d.Proto).Append('/').Append(d.Kind).Append("] ").Append(d.Name)
              .Append(" -> ").Append(r).Append(" pct=").Append(d.Pct).Append(" mV=").Append(d.MilliVolts)
              .Append(" carregando=").Append(d.Charging).Append(" cheio=").Append(d.Full).AppendLine();
        }
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "teste.txt"), sb.ToString());
        var items = new (string, uint, int)[] { ("85", 0x3DDC84u, Render.GlyphMouse), ("45", 0xFFC83Du, Render.GlyphHeadset),
            ("72", 0x3DDC84u, Render.GlyphKeyboard), ("12", 0xFF4D4Du, Render.GlyphOther), ("100", 0x3DDC84u, Render.GlyphHeadset),
            ("–", 0x8C8C8Cu, Render.GlyphMouse) };
        Render.DumpStrip(Path.Combine(dir, "strip.bmp"), items);
        Hid.CloseAll();
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
