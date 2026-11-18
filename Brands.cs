using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static BateriaTray.N;

namespace BateriaTray;

// Protocolos de outras marcas. Todos só leem (nenhum comando de configuração).
// Bytes dos protocolos conferidos com projetos abertos: HeadsetControl, Wireless-Device-Battery-Monitor,
// rivalcfg, ckb-next/OpenLinkHub, glorious-ctl, PulsarBattery, vole e outros (ver README).
// A varredura só compara IDs (não pergunta nada ao aparelho): a leitura acontece no ciclo normal.
internal static unsafe class Brands
{
    // ============================================================================================
    // Utilitários
    // ============================================================================================

    static Kind KindOf(List<Hid.Iface> ifs, ushort vid, ushort pid, string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("mouse")) return Kind.Mouse;
        if (n.Contains("keyboard") || n.Contains("teclado")) return Kind.Keyboard;
        if (n.Contains("headset") || n.Contains("headphone") || n.Contains("audio") || n.Contains("buds")) return Kind.Headset;
        int kb = 0, ms = 0, tel = 0;
        foreach (var i in ifs)
        {
            if (i.Vid != vid || i.Pid != pid) continue;
            if (i.UsagePage == 0x01 && i.Usage == 0x06) kb++;
            else if (i.UsagePage == 0x01 && i.Usage == 0x02) ms++;
            else if (i.UsagePage == 0x0B) tel++;
        }
        if (tel > 0) return Kind.Headset;
        if (kb >= 2) return Kind.Keyboard;  // teclados expõem coleções boot + NKRO
        if (ms > 0) return Kind.Mouse;
        if (kb > 0) return Kind.Keyboard;
        return Kind.Other;
    }

    static Dev New(List<Dev> found, Proto p, Kind k, string name, string key, List<Hid.Iface> cands, int sub, bool confirm)
    {
        if (cands.Count == 0 || Proto_.Exists(found, key)) return null;
        var d = new Dev(p, k, name, key) { Cands = cands, Sub = sub, NeedConfirm = confirm, Vid = cands[0].Vid, Pid = cands[0].Pid };
        found.Add(d);
        return d;
    }

    static List<Hid.Iface> Find(List<Hid.Iface> ifs, ushort vid, ushort pid, Func<Hid.Iface, bool> ok)
    {
        var l = new List<Hid.Iface>();
        foreach (var i in ifs) if (i.Vid == vid && i.Pid == pid && ok(i)) l.Add(i);
        return l;
    }

    // Handle da interface candidata atual (abre sob demanda).
    static nint Cur(Dev d, bool feature)
    {
        if (d.H != -1) return d.H;
        var c = d.Cands[d.CandIdx];
        d.H = feature ? Hid.OpenFeature(c.Path) : Hid.OpenRW(c.Path);
        d.OutLen = c.OutLen; d.FeatLen = c.FeatLen; d.Path = c.Path;
        return d.H;
    }

    // Sem resposta: na próxima leitura tenta a próxima interface candidata (até achar a certa).
    static PollResult NoReply(Dev d)
    {
        if (!d.Locked && d.Cands != null && d.Cands.Count > 1)
        {
            Hid.Release(d.H); d.H = -1;
            d.CandIdx = (d.CandIdx + 1) % d.Cands.Count;
        }
        return PollResult.Asleep;
    }

    static PollResult Ok(Dev d, int pct, bool chg, bool full)
    {
        d.Locked = true;
        d.MarkRead(pct, chg, full);
        return PollResult.Ok;
    }

    static int Map(int v, int lo, int hi) => hi <= lo ? 0 : Math.Clamp((v - lo) * 100 / (hi - lo), 0, 100);

    static bool In(ushort v, ushort[] list) => Array.IndexOf(list, v) >= 0;

    static int Q(nint h, int outLen, ReadOnlySpan<byte> req, ReadOnlySpan<short> pat, int timeout) =>
        Hid.Query(h, outLen, req, pat, timeout);

    public static void Scan(List<Hid.Iface> ifs, List<Dev> found)
    {
        try { ScanHyperX(ifs, found); } catch { }
        try { ScanSteelSeries(ifs, found); } catch { }
        try { ScanCorsair(ifs, found); } catch { }
        try { ScanGlorious(ifs, found); } catch { }
        try { ScanLamzu(ifs, found); } catch { }
        try { ScanPulsar(ifs, found); } catch { }
        try { ScanEndgame(ifs, found); } catch { }
        try { ScanAreson(ifs, found); } catch { }
        try { ScanXenta(ifs, found); } catch { }
        try { ScanGeneric(ifs, found); } catch { } // por último: só o que ninguém reivindicou
    }

    public static PollResult Poll(Dev d) => d.Proto switch
    {
        Proto.HyperX => HyperX(d),
        Proto.SteelSeries => SteelSeries(d),
        Proto.CorsairBragi => Bragi(d),
        Proto.CorsairVoid => Void(d),
        Proto.Glorious or Proto.Lamzu => Glorious(d),
        Proto.Pulsar => Pulsar(d),
        Proto.Endgame => Endgame(d),
        Proto.Areson => Areson(d),
        Proto.Xenta => Xenta(d),
        _ => Generic(d),
    };

    // ============================================================================================
    // HyperX (HP 03F0 / Kingston 0951)
    // ============================================================================================

    const int HxCloud3 = 1, HxCloud3S = 2, HxCloud2 = 3, HxCloud2K = 4, HxAlpha = 5, HxAlpha2 = 6, HxFlight = 7, HxHaste = 8;

    static readonly (ushort vid, ushort[] pids, int sub, string name, Kind kind)[] HxModels =
    {
        (0x03F0, new ushort[] { 0x05B7, 0x0C9D }, HxCloud3, "HyperX Cloud III Wireless", Kind.Headset),
        (0x03F0, new ushort[] { 0x069F, 0x0995 }, HxCloud3, "HyperX Cloud II Core Wireless", Kind.Headset),
        (0x03F0, new ushort[] { 0x06BE, 0x02CC }, HxCloud3S, "HyperX Cloud III S Wireless", Kind.Headset),
        (0x03F0, new ushort[] { 0x0696, 0x018B, 0x0D93 }, HxCloud2, "HyperX Cloud II Wireless", Kind.Headset),
        (0x0951, new ushort[] { 0x1718 }, HxCloud2K, "HyperX Cloud II Wireless", Kind.Headset),
        (0x03F0, new ushort[] { 0x098D }, HxAlpha, "HyperX Cloud Alpha Wireless", Kind.Headset),
        (0x03F0, new ushort[] { 0x08BE }, HxAlpha2, "HyperX Cloud Alpha 2 Wireless", Kind.Headset),
        (0x0951, new ushort[] { 0x16C4, 0x1723, 0x1749 }, HxFlight, "HyperX Cloud Flight", Kind.Headset),
        (0x03F0, new ushort[] { 0x0E90 }, HxFlight, "HyperX Cloud Flight", Kind.Headset),
        (0x03F0, new ushort[] { 0x0AB5 }, HxHaste, "HyperX Pulsefire Haste 2 Core Wireless", Kind.Mouse),
        (0x03F0, new ushort[] { 0x0D97, 0x0F98 }, HxHaste, "HyperX Pulsefire Haste 2 Wireless", Kind.Mouse),
    };

    static bool HxAccept(int sub, Hid.Iface i) => sub switch
    {
        HxCloud3 => i.OutLen >= 62 && i.InLen >= 62 && (i.UsagePage >= 0xFF00 || (i.OutLen == 62 && i.InLen == 62)),
        HxCloud3S => i.OutLen == 64 && i.InLen == 64,
        HxCloud2 => i.UsagePage == 0xFF90 && i.Usage == 0x0303,
        HxCloud2K => i.OutLen == 62,
        HxAlpha2 => i.UsagePage == 0xFF13 && i.Usage == 0xFF00,
        HxHaste => i.UsagePage >= 0xFF00 && i.OutLen >= 2 && i.InLen >= 8,
        _ => i.UsagePage >= 0xFF00 && i.OutLen >= 3, // Alpha, Flight
    };

    static void ScanHyperX(List<Hid.Iface> ifs, List<Dev> found)
    {
        foreach (var m in HxModels)
            foreach (var pid in m.pids)
                New(found, Proto.HyperX, m.kind, m.name, "hyperx:" + m.name, Find(ifs, m.vid, pid, i => HxAccept(m.sub, i)), m.sub, true);
    }

    static PollResult HyperX(Dev d)
    {
        nint h = Cur(d, false);
        if (h == -1) return NoReply(d);
        int ol = d.OutLen, r;
        switch (d.Sub)
        {
            case HxCloud3:
            {
                r = Q(h, ol, stackalloc byte[] { 0x66, 0x82 }, stackalloc short[] { 0x66, 0x82, -1 }, 800);
                if (r < 0) return PollResult.Lost; if (r == 0) return NoReply(d);
                if (Hid.D[2] == 0) return PollResult.Asleep; // headset desligado
                bool chg = Q(h, ol, stackalloc byte[] { 0x66, 0x8A }, stackalloc short[] { 0x66, 0x8A, -1 }, 800) == 1 && Hid.D[2] == 1;
                if (Q(h, ol, stackalloc byte[] { 0x66, 0x89 }, stackalloc short[] { 0x66, 0x89, -1, -1, -1 }, 800) == 1 && Hid.D[4] <= 100)
                {
                    d.MilliVolts = (Hid.D[2] << 8) | Hid.D[3];
                    return Ok(d, Hid.D[4], chg, false);
                }
                return PollResult.Asleep;
            }
            case HxCloud3S:
            {
                r = Q(h, ol, stackalloc byte[] { 0x0C, 0x02, 0x03, 0x01, 0x00, 0x06 }, stackalloc short[] { 0x0C, -1, -1, -1, -1, 0x06, -1 }, 1000);
                if (r < 0) return PollResult.Lost; if (r == 0 || Hid.D[6] > 100) return NoReply(d);
                int pct = Hid.D[6];
                bool chg = Q(h, ol, stackalloc byte[] { 0x0C, 0x02, 0x03, 0x01, 0x00, 0x48 }, stackalloc short[] { 0x0C, -1, -1, -1, -1, 0x48, -1 }, 1000) == 1
                           && (Hid.D[6] == 1 || Hid.D[6] == 2);
                return Ok(d, pct, chg, false);
            }
            case HxCloud2:
            {
                r = Q(h, ol, stackalloc byte[] { 0x06, 0xFF, 0xBB, 0x02 }, stackalloc short[] { 0x06, 0xFF, 0xBB, 0x02, -1, -1, -1, -1 }, 1000);
                if (r < 0) return PollResult.Lost; if (r == 0 || Hid.D[7] > 100) return NoReply(d);
                int pct = Hid.D[7]; d.MilliVolts = (Hid.D[5] << 8) | Hid.D[6];
                bool chg = Q(h, ol, stackalloc byte[] { 0x06, 0xFF, 0xBB, 0x03 }, stackalloc short[] { 0x06, 0xFF, 0xBB, 0x03, -1 }, 1000) == 1 && Hid.D[4] == 1;
                return Ok(d, pct, chg, false);
            }
            case HxCloud2K:
            {
                ReadOnlySpan<byte> b = stackalloc byte[] { 0x06, 0x00, 0x02, 0x00, 0x9A, 0x00, 0x00, 0x68, 0x4A, 0x8E, 0x0A, 0x00, 0x00, 0x00, 0xBB, 0x02, 0x00 };
                r = Q(h, ol, b, stackalloc short[] { 0x0B, -1, 0xBB, 0x02, -1, -1, -1, -1 }, 1000);
                if (r < 0) return PollResult.Lost; if (r == 0 || Hid.D[7] > 100) return NoReply(d);
                int pct = Hid.D[7];
                Span<byte> c = stackalloc byte[17]; b.CopyTo(c); c[15] = 0x03;
                bool chg = Q(h, ol, c, stackalloc short[] { 0x0B, -1, 0xBB, 0x03, -1 }, 1000) == 1 && Hid.D[4] == 1;
                return Ok(d, pct, chg, false);
            }
            case HxAlpha:
            {
                r = Q(h, ol, stackalloc byte[] { 0x21, 0xBB, 0x03 }, stackalloc short[] { 0x21, -1, 0x03, -1 }, 1000);
                if (r < 0) return PollResult.Lost; if (r == 0) return NoReply(d);
                if (Hid.D[3] == 0x01) return PollResult.Asleep; // headset desconectado
                bool chg = Q(h, ol, stackalloc byte[] { 0x21, 0xBB, 0x0C }, stackalloc short[] { 0x21, -1, 0x0C, -1 }, 1000) == 1 && Hid.D[3] == 0x01;
                if (Q(h, ol, stackalloc byte[] { 0x21, 0xBB, 0x0B }, stackalloc short[] { 0x21, -1, 0x0B, -1 }, 1000) == 1 && Hid.D[3] <= 100)
                    return Ok(d, Hid.D[3], chg, false);
                return PollResult.Asleep;
            }
            case HxAlpha2:
            case HxHaste:
            {
                // Protocolo "universal" do NGENUITY: 52 01 -> 53 01 [2]=conectado; 50 02 -> 51 02 [2]=% [3]=carga
                r = Q(h, ol, stackalloc byte[] { 0x52, 0x01 }, stackalloc short[] { 0x53, 0x01, -1 }, 1000);
                if (r < 0) return PollResult.Lost;
                bool linked = r == 0 || Hid.D[2] != 0; // sem resposta ao 52 01 não é erro: a bateria decide
                long now = Environment.TickCount64;
                if (!linked) { d.LinkUpAt = 0; return r == 0 ? NoReply(d) : PollResult.Asleep; }
                int rb = 0;
                ReadOnlySpan<byte> bq = stackalloc byte[] { 0x50, 0x02 };
                ReadOnlySpan<short> bp = stackalloc short[] { 0x51, 0x02, -1, -1, -1, -1, -1, -1 };
                for (int a = 0; a < 3 && rb != 1; a++) // o receptor pode responder FF enquanto acorda o mouse
                    rb = Q(h, ol, bq, bp, 600);
                if (rb < 0) return PollResult.Lost;
                if (rb == 0 || Hid.D[2] > 100) return r == 0 ? NoReply(d) : PollResult.Asleep;
                int pct = Hid.D[2]; bool chg = Hid.D[3] == 1 || Hid.D[3] == 2;
                d.MilliVolts = Hid.D[6] | (Hid.D[7] << 8);
                if (d.Sub == HxHaste)
                {
                    // Nos primeiros segundos após ligar, o Haste informa 40% em vez do valor real.
                    if (d.LinkUpAt == 0) d.LinkUpAt = d.Confirmed ? now : now - 6000;
                    if (now - d.LinkUpAt < 6000) { d.Locked = true; return PollResult.Asleep; }
                }
                return Ok(d, pct, chg, false);
            }
            case HxFlight:
            {
                r = Q(h, ol, stackalloc byte[] { 0x21, 0xFF, 0x05 }, stackalloc short[] { -1, -1, -1, -1, -1 }, 1000);
                if (r < 0) return PollResult.Lost; if (r == 0) return NoReply(d);
                int mv = (Hid.D[3] << 8) | Hid.D[4];
                if (mv == 0) return PollResult.Asleep;
                d.MilliVolts = mv;
                if (mv > 0x100B) // acima disso está carregando (a tensão não indica o nível)
                {
                    d.Locked = true; d.Charging = true; d.Asleep = false; d.Confirmed = true;
                    return PollResult.Ok;
                }
                double v = mv;
                double pct = mv <= 3648 ? 0.00125 * mv : mv > 3975 ? 100
                    : 0.00000002547505 * v * v * v * v - 0.0003900299 * v * v * v + 2.238321 * v * v - 5706.256 * v + 5452299.0;
                return Ok(d, (int)Math.Round(pct), false, false);
            }
        }
        return PollResult.Asleep;
    }

    // ============================================================================================
    // SteelSeries (1038): headsets Arctis / Nova e mouses Aerox / Rival
    // ============================================================================================

    const int SsArctis1 = 1, SsArctis7 = 2, SsArctis9 = 3, SsNovaPro = 4, SsNova7Plus = 5, SsNova5 = 6,
              SsNova7Discrete = 7, SsNova7 = 8, SsAerox = 9, SsRival = 10;

    static readonly (ushort[] pids, int sub, string name, Kind kind)[] SsModels =
    {
        (new ushort[] { 0x12B3, 0x12B6, 0x12D7 }, SsArctis1, "SteelSeries Arctis 1 Wireless", Kind.Headset),
        (new ushort[] { 0x1260, 0x12AD, 0x1252 }, SsArctis7, "SteelSeries Arctis 7", Kind.Headset),
        (new ushort[] { 0x12C2 }, SsArctis9, "SteelSeries Arctis 9", Kind.Headset),
        (new ushort[] { 0x12E0, 0x225D }, SsNovaPro, "SteelSeries Arctis Nova Pro Wireless", Kind.Headset),
        (new ushort[] { 0x220E, 0x2212, 0x2216, 0x2236 }, SsNova7Plus, "SteelSeries Arctis 7+", Kind.Headset),
        (new ushort[] { 0x2232, 0x2253 }, SsNova5, "SteelSeries Arctis Nova 5", Kind.Headset),
        (new ushort[] { 0x2269, 0x226D }, SsNova5, "SteelSeries Arctis Nova 3P Wireless", Kind.Headset),
        (new ushort[] { 0x2202, 0x2206, 0x220A, 0x223A, 0x227A, 0x22A4 }, SsNova7Discrete, "SteelSeries Arctis Nova 7", Kind.Headset),
        (new ushort[] { 0x22A1, 0x227E, 0x2258, 0x229E, 0x22AD, 0x22A9, 0x22A5, 0x22A7 }, SsNova7, "SteelSeries Arctis Nova 7", Kind.Headset),
        (new ushort[] { 0x1838, 0x183A }, SsAerox, "SteelSeries Aerox 3 Wireless", Kind.Mouse),
        (new ushort[] { 0x1852, 0x1854 }, SsAerox, "SteelSeries Aerox 5 Wireless", Kind.Mouse),
        (new ushort[] { 0x1858, 0x185A }, SsAerox, "SteelSeries Aerox 9 Wireless", Kind.Mouse),
        (new ushort[] { 0x1840, 0x1842 }, SsAerox, "SteelSeries Prime Wireless", Kind.Mouse),
        (new ushort[] { 0x1830 }, SsRival, "SteelSeries Rival 3 Wireless", Kind.Mouse),
        (new ushort[] { 0x1872 }, SsRival, "SteelSeries Rival 3 Wireless Gen 2", Kind.Mouse),
        (new ushort[] { 0x172B, 0x1726 }, SsRival, "SteelSeries Rival 650 Wireless", Kind.Mouse),
    };
    static readonly ushort[] AeroxWired = { 0x183A, 0x1854, 0x185A, 0x1842 };

    static bool SsAccept(int sub, ushort pid, Hid.Iface i) => sub switch
    {
        SsArctis1 or SsArctis7 => i.UsagePage == 0xFF43 && i.Usage == 0x0202,
        SsArctis9 => i.Mi <= 0 && i.OutLen > 0,
        SsNovaPro => i.Mi == 4 && i.OutLen > 0,
        SsAerox => i.Mi == 3 && i.OutLen > 0,
        SsRival => (pid == 0x172B || pid == 0x1726 ? i.Mi <= 0 : i.Mi == 3) && i.OutLen > 0,
        _ => i.UsagePage == 0xFFC0 && i.Usage == 0x0001, // família Nova
    };

    static void ScanSteelSeries(List<Hid.Iface> ifs, List<Dev> found)
    {
        foreach (var m in SsModels)
            foreach (var pid in m.pids)
                New(found, Proto.SteelSeries, m.kind, m.name, "steelseries:" + m.name, Find(ifs, 0x1038, pid, i => SsAccept(m.sub, pid, i)), m.sub, true);
    }

    static PollResult SteelSeries(Dev d)
    {
        nint h = Cur(d, false);
        if (h == -1) return NoReply(d);
        int ol = d.OutLen, r;
        byte* D;
        switch (d.Sub)
        {
            case SsArctis1:
                r = Q(h, ol, stackalloc byte[] { 0x06, 0x12 }, stackalloc short[] { -1, -1, -1, -1 }, 1000);
                if (r < 0) return PollResult.Lost; if (r == 0) return NoReply(d);
                D = Hid.D;
                return D[2] == 0x01 ? PollResult.Asleep : Ok(d, D[3], false, false);
            case SsArctis7:
                r = Q(h, ol, stackalloc byte[] { 0x06, 0x18 }, stackalloc short[] { -1, -1, -1 }, 1000);
                if (r < 0) return PollResult.Lost; if (r == 0) return NoReply(d);
                return Hid.D[2] == 0 ? PollResult.Asleep : Ok(d, Hid.D[2], false, false);
            case SsArctis9:
                r = Q(h, ol, stackalloc byte[] { 0x00, 0x20 }, stackalloc short[] { -1, -1, -1, -1, -1 }, 1000);
                if (r < 0) return PollResult.Lost; if (r == 0) return NoReply(d);
                return Ok(d, Map(Hid.D[3], 0x64, 0x9A), Hid.D[4] == 0x01, false);
            case SsNovaPro:
                r = Q(h, ol, stackalloc byte[] { 0x06, 0xB0 }, stackalloc short[] { -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 }, 1000);
                if (r < 0) return PollResult.Lost; if (r == 0) return NoReply(d);
                D = Hid.D;
                return D[15] == 0x01 ? PollResult.Asleep : Ok(d, Map(D[6], 0, 8), D[15] == 0x02, false);
            case SsAerox:
            {
                byte cmd = In(d.Pid, AeroxWired) ? (byte)0x92 : (byte)0xD2;
                r = Q(h, ol, stackalloc byte[] { 0x00, cmd }, stackalloc short[] { -1, -1 }, 300);
                if (r < 0) return PollResult.Lost; if (r == 0) return NoReply(d);
                int raw = Hid.D[1] & 0x7F;
                if (raw == 0 || raw > 21) return PollResult.Asleep; // mouse desligado / fora de alcance
                return Ok(d, (raw - 1) * 5, (Hid.D[1] & 0x80) != 0, false);
            }
            case SsRival:
                r = Q(h, ol, stackalloc byte[] { 0x00, 0xAA, 0x01 }, stackalloc short[] { -1, -1, -1 }, 300);
                if (r < 0) return PollResult.Lost; if (r == 0) return NoReply(d);
                return Hid.D[0] > 100 ? PollResult.Asleep : Ok(d, Hid.D[0], Hid.D[2] != 0, false);
            default: // família Nova: 00 B0 -> bloco de status
                r = Q(h, ol, stackalloc byte[] { 0x00, 0xB0 }, stackalloc short[] { -1, -1, -1, -1, -1, -1, -1 }, 1000);
                if (r < 0) return PollResult.Lost; if (r == 0) return NoReply(d);
                D = Hid.D;
                switch (d.Sub)
                {
                    case SsNova7Plus: return D[1] == 0x01 ? PollResult.Asleep : Ok(d, Map(D[2], 0, 4), D[3] == 0x01, false);
                    case SsNova5: return D[1] == 0x02 ? PollResult.Asleep : Ok(d, D[3], D[4] == 0x01, false);
                    default:
                        if (D[3] == 0x00) return PollResult.Asleep;
                        return Ok(d, d.Sub == SsNova7Discrete ? Map(D[2], 0, 4) : D[2], D[3] == 0x01 || D[3] == 0x02, false);
                }
        }
    }

    // ============================================================================================
    // Corsair (1B1C): "Bragi" (Slipstream, HS80, Virtuoso, Void V2) e família Void antiga
    // ============================================================================================

    static readonly ushort[] VoidPids = { 0x1B27, 0x0A14, 0x0A16, 0x0A17, 0x0A1D, 0x0A1A, 0x1B2A, 0x1B23, 0x1B29,
                                          0x0A55, 0x0A51, 0x0A52, 0x0A38, 0x0A4F, 0x0A2B, 0x0A75, 0x0A56 };

    static void ScanCorsair(List<Hid.Iface> ifs, List<Dev> found)
    {
        var pids = new HashSet<ushort>();
        foreach (var i in ifs) if (i.Vid == 0x1B1C) pids.Add(i.Pid);
        foreach (var pid in pids)
        {
            string prod = ifs.Find(i => i.Vid == 0x1B1C && i.Pid == pid && i.Product.Length > 0).Product ?? "Corsair";
            if (!prod.StartsWith("Corsair", StringComparison.OrdinalIgnoreCase)) prod = "Corsair " + prod;
            if (In(pid, VoidPids))
            {
                var c = Find(ifs, 0x1B1C, pid, i => i.UsagePage == 0xFFC5 && i.Usage == 0x0001);
                c.AddRange(Find(ifs, 0x1B1C, pid, i => i.UsagePage >= 0xFF00 && i.UsagePage != 0xFFC5 && i.OutLen >= 2));
                New(found, Proto.CorsairVoid, Kind.Headset, prod, "corsair:" + pid.ToString("X4"), c, 0, true);
            }
            else
            {
                var c = Find(ifs, 0x1B1C, pid, i => i.UsagePage == 0xFF42 && i.Usage == 0x0001 && i.OutLen >= 4 && i.InLen >= 8);
                New(found, Proto.CorsairBragi, KindOf(ifs, 0x1B1C, pid, prod), prod, "corsair:" + pid.ToString("X4"), c, 0x09, true);
            }
        }
    }

    static PollResult Void(Dev d)
    {
        nint h = Cur(d, false);
        if (h == -1) return NoReply(d);
        int r = Q(h, d.OutLen, stackalloc byte[] { 0xC9, 0x64 }, stackalloc short[] { 0x64, -1, -1, -1, -1 }, 1000);
        if (r < 0) return PollResult.Lost; if (r == 0) return NoReply(d);
        byte st = Hid.D[4];
        if (st == 0) return PollResult.Asleep; // headset desconectado do receptor
        return Ok(d, Hid.D[2] & 0x7F, st == 4 || st == 5, false);
    }

    // Lê uma propriedade Bragi: 02 TT 02 PP -> 01 FF 02 SS VV VV VV VV. Retorna o valor ou -1.
    static long BragiGet(nint h, int ol, byte target, byte prop, out int io)
    {
        short src = target == 0x09 ? (short)0x01 : (short)0x00;
        io = Q(h, ol, stackalloc byte[] { 0x02, target, 0x02, prop }, stackalloc short[] { 0x01, src, 0x02, -1, -1, -1, -1, -1 }, 800);
        if (io != 1 || Hid.D[3] != 0) return -1;
        return (long)Hid.D[4] | ((long)Hid.D[5] << 8) | ((long)Hid.D[6] << 16) | ((long)Hid.D[7] << 24);
    }

    static PollResult Bragi(Dev d)
    {
        nint h = Cur(d, false);
        if (h == -1) return NoReply(d);
        // Sub = alvo: 09 = aparelho atrás do receptor, 08 = o próprio aparelho (cabo). Testa os dois.
        int tries = d.Locked ? 1 : 2;
        for (int k = 0; k < tries; k++)
        {
            byte t = k == 0 ? (byte)d.Sub : (byte)(d.Sub == 0x09 ? 0x08 : 0x09);
            long lvl = BragiGet(h, d.OutLen, t, 0x0F, out int io);
            if (io < 0) return PollResult.Lost;
            if (lvl < 0 || lvl > 1000) continue;
            d.Sub = t;
            long st = BragiGet(h, d.OutLen, t, 0x10, out _);
            return Ok(d, (int)((lvl + 5) / 10), st == 1, st == 3);
        }
        return NoReply(d);
    }

    // ============================================================================================
    // Glorious / Hitscan / G-Wolves / WLMouse e LAMZU: feature report 0 de 64 bytes,
    // pedido [3]=02 [4]=02 [6]=83 -> resposta [1]=status A1, [7]=carregando, [8]=%
    // ============================================================================================

    static readonly ushort[] GloriousPids = { 0x2022, 0x2011, 0x2023, 0x2012, 0x2024, 0x2013, 0x2025, 0x2014, 0x2033, 0x2034, 0x2036, 0x201A };
    static readonly ushort[] GloriousCable = { 0x2011, 0x2012, 0x2013, 0x2014, 0x201A };
    static readonly ushort[] HitscanCable = { 0x0136, 0x0194 };

    static string GloriousName(ushort pid) => pid switch
    {
        0x2022 or 0x2011 => "Glorious Model O Wireless",
        0x2023 or 0x2012 => "Glorious Model D Wireless",
        0x2024 or 0x2013 => "Glorious Model O- Wireless",
        0x2025 or 0x2014 => "Glorious Model D- Wireless",
        0x2033 => "Glorious Model O 2 Wireless",
        _ => "Glorious Model D 2 PRO",
    };

    static void ScanGlorious(List<Hid.Iface> ifs, List<Dev> found)
    {
        var seen = new HashSet<uint>();
        foreach (var i in ifs)
        {
            if (i.FeatLen != 65 || !seen.Add(((uint)i.Vid << 16) | i.Pid)) continue;
            string name; int delay; bool cable;
            // 258A (Sinowealth) é usado por muitas marcas (inclusive teclados): só IDs conhecidos da Glorious.
            if (i.Vid == 0x258A && In(i.Pid, GloriousPids)) { name = GloriousName(i.Pid); delay = 50; cable = In(i.Pid, GloriousCable); }
            else if (i.Vid == 0x3770) { name = i.Pid == 0x0194 ? "Hitscan Hyperlight N" : "Hitscan Hyperlight X"; delay = 200; cable = In(i.Pid, HitscanCable); }
            else if (i.Vid == 0x33E4) { name = i.Product.Length > 0 ? i.Product : "G-Wolves"; delay = 100; cable = false; }
            else if (i.Vid == 0x36A7 && i.Pid == 0xA874) { name = "WLMouse YING"; delay = 100; cable = false; }
            else continue;
            var c = Find(ifs, i.Vid, i.Pid, x => x.FeatLen == 65 && (i.Vid != 0x36A7 || (x.UsagePage == 0xFFFF && x.Usage == 0)));
            New(found, Proto.Glorious, Kind.Mouse, name, "glorious:" + i.Vid.ToString("X4") + i.Pid.ToString("X4"), c, delay | (cable ? 0x10000 : 0), true);
        }
    }

    static void ScanLamzu(List<Hid.Iface> ifs, List<Dev> found)
    {
        var seen = new HashSet<uint>();
        foreach (var i in ifs)
        {
            if ((i.Vid != 0x373E && i.Vid != 0x37B0) || i.FeatLen != 65 || !seen.Add(((uint)i.Vid << 16) | i.Pid)) continue;
            string name = i.Product.Length > 0 ? i.Product : "LAMZU";
            if (!name.StartsWith("LAMZU", StringComparison.OrdinalIgnoreCase)) name = "LAMZU " + name;
            New(found, Proto.Lamzu, Kind.Mouse, name, "lamzu:" + i.Vid.ToString("X4") + i.Pid.ToString("X4"),
                Find(ifs, i.Vid, i.Pid, x => x.FeatLen == 65), 100, true);
        }
    }

    static PollResult Glorious(Dev d)
    {
        nint h = Cur(d, true);
        if (h == -1) return NoReply(d);
        int delay = d.Sub & 0xFFFF; bool cable = (d.Sub & 0x10000) != 0;
        byte* r = Hid.Rx;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Hid.ClearTx(65);
            Hid.Tx[3] = 0x02; Hid.Tx[4] = 0x02; Hid.Tx[6] = 0x83;
            if (!Hid.SetFeature(h, 65)) return NoReply(d);
            Sleep((uint)delay);
            if (!Hid.GetFeature(h, 0, 65)) return NoReply(d);
            int st, chg, pct;
            if (r[6] == 0x83 && (r[1] & 0xF0) == 0xA0) { st = r[1]; chg = r[7]; pct = r[8]; }
            else if (d.Proto == Proto.Lamzu && r[0] == 0xA1 && r[3] == 0x02 && r[5] == 0x83) { st = 0xA1; chg = r[6]; pct = r[7]; }
            else return NoReply(d);
            if (st == 0xA0 && attempt == 0) { Sleep(400); continue; } // acordando: pergunta de novo
            if (st != 0xA1 || pct > 100 || (pct == 0 && !cable && d.Proto == Proto.Lamzu)) return PollResult.Asleep;
            return Ok(d, pct == 0 ? 1 : pct, cable || chg == 1, false);
        }
        return PollResult.Asleep;
    }

    // ============================================================================================
    // Pulsar (3710, chip Sonix): feature report 0, pacote 64 bytes [00][08][81][01] + soma de 16 bits
    // ============================================================================================

    static void ScanPulsar(List<Hid.Iface> ifs, List<Dev> found)
    {
        foreach (ushort pid in stackalloc ushort[] { 0x3402, 0x3406, 0x5402, 0x5403 })
        {
            var c = Find(ifs, 0x3710, pid, x => x.FeatLen >= 65);
            c.Sort((a, b) => b.Mi.CompareTo(a.Mi)); // a interface 2 rejeita SET_FEATURE: tenta as maiores antes
            string name = pid == 0x3402 ? "Pulsar X2 V3" : pid == 0x3406 ? "Pulsar X2 V3 eS" : "Pulsar (receptor)";
            New(found, Proto.Pulsar, Kind.Mouse, name, "pulsar:" + pid.ToString("X4"), c, pid == 0x3402 || pid == 0x3406 ? 1 : 0, true);
        }
    }

    static PollResult Pulsar(Dev d)
    {
        nint h = Cur(d, true);
        if (h == -1) return NoReply(d);
        int len = Math.Min(d.FeatLen, Hid.BufSize);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Hid.ClearTx(len);
            byte* p = Hid.Tx + 1; // Tx[0] = report id 0
            p[1] = 0x08; p[2] = 0x81; p[3] = 0x01;
            int sum = 0; for (int k = 0; k < 62; k++) sum += p[k];
            p[62] = (byte)sum; p[63] = (byte)(sum >> 8);
            if (!Hid.SetFeature(h, len)) return NoReply(d);
            for (int k = 0; k < 6; k++)
            {
                Sleep(50);
                if (!Hid.GetFeature(h, 0, len)) return NoReply(d);
                byte* w = Hid.Rx + 1;
                if (w[1] != 0x08 || w[2] != 0x81 || w[3] != 0x01) continue;
                if (w[0] == 0x01)
                {
                    if (w[6] > 100 || (w[6] == 0 && d.Sub == 0)) return PollResult.Asleep;
                    return Ok(d, w[6], d.Sub == 1, false);
                }
            }
        }
        return NoReply(d);
    }

    // ============================================================================================
    // Endgame Gear 4K (3367:1970): feature report A1, comando B4 = bateria (resposta [16] = %)
    // ============================================================================================

    static void ScanEndgame(List<Hid.Iface> ifs, List<Dev> found) =>
        New(found, Proto.Endgame, Kind.Mouse, "Endgame Gear 4k", "endgame:1970",
            Find(ifs, 0x3367, 0x1970, x => x.UsagePage == 0xFF01 && x.Usage == 0x0002 && x.FeatLen >= 64), 0, true);

    static int EgCmd(nint h, int len, byte cmd, byte target)
    {
        Hid.ClearTx(len);
        Hid.Tx[0] = 0xA1; Hid.Tx[1] = cmd; Hid.Tx[2] = target;
        if (!Hid.SetFeature(h, len)) return -1;
        for (int a = 0; a < 3; a++)
        {
            Sleep((uint)(150 + a * 100));
            if (!Hid.GetFeature(h, 0xA1, len) || Hid.Rx[0] != 0xA1) return -1;
            if (Hid.Rx[1] != 0x03) return Hid.Rx[1]; // 03 = ocupado
        }
        return 0x03;
    }

    static PollResult Endgame(Dev d)
    {
        nint h = Cur(d, true);
        if (h == -1) return NoReply(d);
        int len = Math.Min(d.FeatLen, Hid.BufSize);
        int st = EgCmd(h, len, 0xB4, 0x00);
        if (st != 0x01 && st != 0x08 && st != 0x11)
        {
            EgCmd(h, len, 0x0F, 0x01); EgCmd(h, len, 0x0F, 0x0F); // "acorda" o canal após uma falha
            st = EgCmd(h, len, 0xB4, 0x00);
        }
        if (st == 0x01 && Hid.Rx[16] <= 100) return Ok(d, Hid.Rx[16], false, false);
        return st == 0x08 || st == 0x11 ? PollResult.Asleep : NoReply(d);
    }

    // ============================================================================================
    // Redragon em receptor Areson (25A7:FA07/FA08): pedido GetPower na coleção FF02:0002 (feature),
    // resposta na coleção FF01:0000 (input): [09][04][00]..[6]=nível 0..10 [7]=carregando
    // ============================================================================================

    static void ScanAreson(List<Hid.Iface> ifs, List<Dev> found)
    {
        foreach (ushort pid in stackalloc ushort[] { 0xFA07, 0xFA08 })
        {
            var cmd = Find(ifs, 0x25A7, pid, x => x.Mi == 1 && x.UsagePage == 0xFF02 && x.Usage == 0x0002 && x.FeatLen >= 17);
            var rep = Find(ifs, 0x25A7, pid, x => x.Mi == 1 && x.UsagePage == 0xFF01 && x.Usage == 0x0000 && x.InLen >= 17);
            if (cmd.Count == 0 || rep.Count == 0) continue;
            string name = cmd[0].Product.Length > 0 ? cmd[0].Product : "Redragon";
            var d = New(found, Proto.Areson, Kind.Mouse, name, "areson:" + pid.ToString("X4"), cmd, 0, false);
            if (d == null) continue;
            d.H = Hid.OpenFeature(cmd[0].Path); d.FeatLen = cmd[0].FeatLen; d.Path = cmd[0].Path;
            d.H2 = Hid.OpenRW(rep[0].Path);
        }
    }

    static PollResult Areson(Dev d)
    {
        if (d.H == -1 || d.H2 == -1) return PollResult.Lost;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Hid.ClearTx(d.FeatLen);
            Hid.Tx[0] = 0x08; Hid.Tx[1] = 0x04;
            byte s = 0; for (int k = 0; k < 16; k++) s += Hid.Tx[k];
            Hid.Tx[16] = (byte)(0x55 - s);
            HidD_FlushQueue(d.H2); // um report antigo na fila seria confundido com a resposta
            if (!Hid.SetFeature(d.H, d.FeatLen)) return PollResult.Lost;
            long end = Environment.TickCount64 + 1000;
            while (true)
            {
                long rem = end - Environment.TickCount64;
                if (rem <= 0) break;
                int r = Hid.Read(d.H2, (int)rem, out uint n);
                if (r < 0) return PollResult.Lost;
                if (r == 0) break;
                byte* x = Hid.Rx;
                if (n < 17 || x[0] != 0x09 || x[1] != 0x04 || x[2] != 0x00 || x[6] > 10) continue;
                byte t = 0; for (int k = 0; k < 17; k++) t += x[k];
                if (t != 0x55) continue;
                return Ok(d, x[6] * 10, x[7] != 0, false);
            }
        }
        return PollResult.Asleep;
    }

    // ============================================================================================
    // Attack Shark / Delux (receptor Xenta/Beken 1D57): o receptor transmite sozinho, a cada ~2 s,
    // [03][modelo][40|41][estado][nível]. Nada é enviado: só lemos o que ficou na fila.
    // ============================================================================================

    static readonly ushort[] XentaPids = { 0xFA60, 0xFA65, 0xFA55, 0x2155 };

    static string XentaName(byte model) => model switch
    {
        0x3D or 0x30 or 0x50 => "Delux M900 Pro",
        0x55 => "Attack Shark X11",
        0x4E => "Attack Shark X3 Max",
        0xE5 => "Attack Shark V3 Pro",
        0x24 => "Attack Shark V6",
        0x10 => "Attack Shark X6 / R1",
        _ => null,
    };

    static void ScanXenta(List<Hid.Iface> ifs, List<Dev> found)
    {
        foreach (var pid in XentaPids)
        {
            var c = Find(ifs, 0x1D57, pid, x => x.UsagePage == 0x000A && x.InLen >= 5);
            var d = New(found, Proto.Xenta, Kind.Mouse, "Attack Shark", "xenta:" + pid.ToString("X4"), c, pid == 0xFA55 || pid == 0x2155 ? 1 : 0, true);
            if (d != null) { d.H = Hid.OpenRW(c[0].Path); d.Path = c[0].Path; }
        }
    }

    static PollResult Xenta(Dev d)
    {
        if (d.H == -1) return PollResult.Lost;
        bool ok = Hid.Drain(d.H, n =>
        {
            byte* x = Hid.Rx;
            if (n < 5 || x[0] != 0x03 || (x[2] != 0x40 && x[2] != 0x41)) return;
            d.PrevFrame = d.LastFrame;
            d.LastFrame = (ulong)x[1] | ((ulong)x[2] << 8) | ((ulong)x[3] << 16) | ((ulong)x[4] << 24) | (1UL << 40);
            d.FrameAt = Environment.TickCount64;
        });
        if (!ok) return PollResult.Lost;
        if (d.LastFrame == 0 || Environment.TickCount64 - d.FrameAt > 15000) return PollResult.Asleep;
        byte model = (byte)d.LastFrame, st = (byte)(d.LastFrame >> 16), level = (byte)(d.LastFrame >> 24);
        string n2 = XentaName(model);
        if (n2 != null) d.Name = n2;
        bool cable = d.Sub == 1;
        if (cable || st == 0x02 || st == 0x03)
        {
            if (st == 0x02 && !cable) return Ok(d, 100, false, true);
            if (st == 0x03 && level >= 1 && level <= 100 && model is 0x30 or 0x50) return Ok(d, level, true, false);
            if (d.Pct >= 0) return Ok(d, d.Pct, true, false);   // na tomada o nível fica congelado
            d.Charging = true; d.Confirmed = true; d.Asleep = false;
            return PollResult.Ok;
        }
        // Toda reconexão começa com um falso 100%: só vale quando dois quadros seguidos concordam.
        if (d.PrevFrame != d.LastFrame) return PollResult.Asleep;
        if (model == 0x10) return level >= 1 && level <= 10 ? Ok(d, level * 10, false, false) : PollResult.Asleep;
        return st == 0x01 && level >= 1 && level <= 100 ? Ok(d, level, false, false) : PollResult.Asleep;
    }

    // ============================================================================================
    // HID genérico: qualquer aparelho que informe bateria no formato padrão do USB HID
    // ============================================================================================

    static void ScanGeneric(List<Hid.Iface> ifs, List<Dev> found)
    {
        var claimed = new HashSet<uint>();
        foreach (var d in found) claimed.Add(((uint)d.Vid << 16) | d.Pid);
        foreach (var i in ifs)
        {
            if (i.BatType < 0 || i.Vid == 0) continue;
            if (i.Path.StartsWith(@"\\?\hid#{", StringComparison.OrdinalIgnoreCase)) continue; // Bluetooth: já coberto
            if (claimed.Contains(((uint)i.Vid << 16) | i.Pid)) continue;
            string name = i.Product.Length > 0 ? i.Product : "Dispositivo " + i.Vid.ToString("X4") + ":" + i.Pid.ToString("X4");
            var d = New(found, Proto.Generic, KindOf(ifs, i.Vid, i.Pid, name), name,
                        "hid:" + i.Vid.ToString("X4") + i.Pid.ToString("X4"), new List<Hid.Iface> { i }, 0, true);
            if (d != null) d.Bat = i;
        }
    }

    static PollResult Generic(Dev d)
    {
        var b = d.Bat;
        if (d.H == -1)
        {
            // Feature: acesso zero basta. Input: tenta leitura/escrita e cai para acesso zero.
            fixed (char* p = b.Path)
            {
                d.H = b.BatType == 0 ? CreateFileW(p, 0xC0000000, 3, 0, 3, 0, 0) : -1;
                if (d.H == -1) d.H = CreateFileW(p, 0, 3, 0, 3, 0, 0);
            }
            if (d.H == -1) return PollResult.Lost;
            Hid.Handles.Add(d.H);
        }
        int len = Math.Min((int)(b.BatType == 2 ? b.FeatLen : b.InLen), Hid.BufSize);
        if (len <= 0) return PollResult.Asleep;
        NativeMemory.Clear(Hid.Rx, (nuint)len);
        Hid.Rx[0] = b.BatReportId;
        bool ok = b.BatType == 2 ? HidD_GetFeature(d.H, Hid.Rx, (uint)len) != 0 : HidD_GetInputReport(d.H, Hid.Rx, (uint)len) != 0;
        if (!ok) return PollResult.Asleep;
        nint pp;
        if (HidD_GetPreparsedData(d.H, &pp) == 0) return PollResult.Asleep;
        uint v;
        int st = HidP_GetUsageValue(b.BatType, b.BatPage, b.BatLink, b.BatUsage, &v, pp, Hid.Rx, (uint)len);
        HidD_FreePreparsedData(pp);
        if (st != 0x00110000) return PollResult.Asleep;
        int lo = b.BatMin, hi = b.BatMax;
        if (hi <= lo) { lo = 0; hi = v <= 100 ? 100 : 255; }
        if (v == 0 && !d.Confirmed) return PollResult.Asleep; // muitos aparelhos informam 0 antes da primeira medição
        return Ok(d, Map((int)v, lo, hi), false, false);
    }
}
