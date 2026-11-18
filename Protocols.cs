using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static BateriaTray.N;

namespace BateriaTray;

internal enum PollResult { Ok, Asleep, Lost }

internal static class Proto_
{
    public static bool Exists(List<Dev> found, string key) => found.Exists(d => d.Key == key);

    // Curva típica de bateria Li-Po (mV -> %), usada quando o dispositivo só informa tensão.
    static readonly (int mv, int pct)[] Lipo =
        { (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50), (3778, 40),
          (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0) };

    public static int LipoPct(int mv)
    {
        if (mv >= Lipo[0].mv) return 100;
        for (int i = 1; i < Lipo.Length; i++)
            if (mv >= Lipo[i].mv)
                return Lipo[i].pct + (mv - Lipo[i].mv) * (Lipo[i - 1].pct - Lipo[i].pct) / (Lipo[i - 1].mv - Lipo[i].mv);
        return 0;
    }
}

// ============================================================================================
// Compx (VID 0x3554): mouses Kysona e outras marcas com o mesmo chip.
// Report 0x08 de 17 bytes, checksum = 0x55 - soma. Comando 0x04 = bateria.
// Resposta: [6]=% (firmware), [7]=1 carregando, [8..9]=mV (big-endian). Testado no KYSONA M600.
// ============================================================================================
internal static unsafe class Compx
{
    static readonly byte[] Match = { 0x08, 0x04 };
    static readonly ushort[] Volt = { 3050, 3420, 3480, 3540, 3600, 3660, 3720, 3760, 3800, 3840, 3880,
                                      3920, 3940, 3960, 3980, 4000, 4020, 4040, 4060, 4080, 4110 };

    public static void Scan(List<Hid.Iface> ifs, List<Dev> found)
    {
        foreach (var i in ifs)
        {
            if (i.Vid != 0x3554 || i.UsagePage != 0xFF02 || i.OutLen != 17) continue;
            string name = i.Product.Length > 0 ? i.Product : "Mouse";
            string key = "compx:" + name;
            var existing = found.Find(d => d.Key == key);
            nint h = Hid.OpenRW(i.Path);
            if (h == -1) continue;
            // Receptor sem fio e cabo ao mesmo tempo: guarda o segundo caminho como alternativa.
            if (existing != null) { if (existing.H2 == -1) existing.H2 = h; continue; }
            found.Add(new Dev(Proto.Compx, Kind.Mouse, name, key) { H = h, Path = i.Path });
        }
    }

    public static PollResult Poll(Dev d)
    {
        var r = Query(d.H, d);
        if (r != PollResult.Ok && d.H2 != -1)
        {
            var r2 = Query(d.H2, d);
            if (r2 == PollResult.Ok) { (d.H, d.H2) = (d.H2, d.H); return r2; }
        }
        return r;
    }

    static PollResult Query(nint h, Dev d)
    {
        byte* tx = Hid.Tx, rx = Hid.Rx;
        NativeMemory.Clear(tx, 17);
        tx[0] = 0x08; tx[1] = 0x04;
        byte sum = 0; for (int i = 0; i < 16; i++) sum += tx[i];
        tx[16] = (byte)(0x55 - sum);
        bool io = false; int ok = 0;
        for (int a = 0; a < 2 && ok == 0 && !io; a++)
            ok = Hid.Transact(h, 17, 250, Match, default, out io);
        if (io) return PollResult.Lost;
        if (ok == 0) return PollResult.Asleep; // mouse dormindo: o receptor não repassa a consulta

        int mv = (rx[8] << 8) | rx[9];
        d.MilliVolts = mv;
        int pct = rx[6] <= 100 ? rx[6] : VoltToPct(mv);
        d.MarkRead(pct, rx[7] == 1, rx[7] == 2);
        return PollResult.Ok;
    }

    static int VoltToPct(int mv)
    {
        if (mv <= Volt[0]) return 0;
        if (mv >= Volt[^1]) return 100;
        for (int i = 1; i < Volt.Length; i++)
            if (mv < Volt[i]) return (i - 1) * 5 + (mv - Volt[i - 1]) * 5 / (Volt[i] - Volt[i - 1]);
        return 100;
    }
}

// ============================================================================================
// MCHOSE (protocolo BLK, receptor 291D:385D): V9 / V9 PRO / G9 PRO / S9 PRO.
// Report 0x55 de 64 bytes, [0x65, 0x01] = bateria. Resposta: [2]=%, [3]=status
// (2 descarregando, 3 carregando, 4 cheio, 26 dormindo). Testado no V9 PRO.
// ============================================================================================
internal static unsafe class Blk
{
    static readonly byte[] Match = { 0x55, 0x65 };
    public static void Scan(List<Hid.Iface> ifs, List<Dev> found)
    {
        foreach (var i in ifs)
        {
            if (i.Vid != 0x291D || i.Pid != 0x385D || i.UsagePage != 0xFF90 || i.OutLen != 64) continue;
            string name = i.Product.Length > 0 ? i.Product : "Headset";
            string key = "blk:" + name;
            if (Proto_.Exists(found, key)) continue;
            nint h = Hid.OpenRW(i.Path);
            if (h == -1) continue;
            found.Add(new Dev(Proto.Blk, Kind.Headset, name, key) { H = h, Path = i.Path });
        }
    }

    public static PollResult Poll(Dev d)
    {
        byte* tx = Hid.Tx, rx = Hid.Rx;
        NativeMemory.Clear(tx, 64);
        tx[0] = 0x55; tx[1] = 0x65; tx[2] = 0x01;
        bool io = false; int ok = 0;
        for (int a = 0; a < 2 && ok == 0 && !io; a++)
            ok = Hid.Transact(d.H, 64, 400, Match, default, out io);
        if (io) return PollResult.Lost;
        if (ok == 0) return PollResult.Asleep;
        int status = rx[3];
        if (status == 26) return PollResult.Asleep; // desligado/dormindo
        d.MarkRead(rx[2], status == 3, status == 4);
        return PollResult.Ok;
    }
}

// ============================================================================================
// Logitech HID++ 2.0: receptores Unifying / Lightspeed / Bolt (índices 1..6) e dispositivos
// diretos por cabo/dongle próprio (índice 0xFF). Só leitura. Features de bateria suportadas:
// 0x1004 Unified Battery, 0x1000 Battery Status, 0x1001 Battery Voltage, 0x1F20 ADC (headsets).
// ============================================================================================
internal static unsafe class Logitech
{
    const byte SW = 0x0B; // "software id" deste app (distingue nossas respostas das do G HUB)
    static readonly ushort[] BatteryFeatures = { 0x1004, 0x1000, 0x1001, 0x1F20 };
    static readonly byte[] Indices = { 0xFF, 1, 2, 3, 4, 5, 6 };

    public static void Scan(List<Hid.Iface> ifs, List<Dev> found)
    {
        foreach (var i in ifs)
        {
            if (i.Vid != 0x046D || i.UsagePage != 0xFF00 || i.OutLen != 20) continue; // canal "long" do HID++
            nint h = Hid.OpenRW(i.Path);
            if (h == -1) continue;
            bool used = false;
            foreach (byte idx in Indices)
            {
                var d = Probe(h, idx, i);
                if (d == null) continue;
                string key = d.Key; int n = 2;
                while (Proto_.Exists(found, d.Key)) d.Key = key + "#" + n++;
                found.Add(d);
                used = true;
                if (idx == 0xFF) break; // dispositivo direto: não há outros índices
            }
            if (!used) Hid.Release(h);
        }
    }

    // Retorna: 1 = resposta, 2 = erro HID++ do dispositivo, 0 = sem resposta.
    static int Req(nint h, byte idx, byte feat, byte fn, byte a0, byte a1, int timeout, out bool io)
    {
        byte* tx = Hid.Tx;
        NativeMemory.Clear(tx, 20);
        byte fsw = (byte)((fn << 4) | SW);
        tx[0] = 0x11; tx[1] = idx; tx[2] = feat; tx[3] = fsw; tx[4] = a0; tx[5] = a1;
        return Hid.Transact(h, 20, timeout, stackalloc byte[] { 0x11, idx, feat, fsw },
                            stackalloc byte[] { 0x11, idx, 0xFF, feat, fsw }, out io);
    }

    static byte FeatureIndex(nint h, byte idx, ushort feature, out bool noReply)
    {
        int r = Req(h, idx, 0x00 /*IRoot*/, 0 /*getFeature*/, (byte)(feature >> 8), (byte)feature, 150, out bool io);
        noReply = r == 0 || io;
        return r == 1 ? Hid.Rx[4] : (byte)0;
    }

    static Dev Probe(nint h, byte idx, Hid.Iface iface)
    {
        byte bIdx = 0; ushort bFeat = 0;
        foreach (var f in BatteryFeatures)
        {
            byte fi = FeatureIndex(h, idx, f, out bool noReply);
            if (noReply) return null;          // índice vazio, dispositivo desligado ou não é HID++ 2.0
            if (fi != 0) { bIdx = fi; bFeat = f; break; }
        }
        if (bIdx == 0) return null;            // sem bateria (ex.: dispositivo com fio)

        // Nome e tipo (feature 0x0005)
        Kind kind = bFeat == 0x1F20 ? Kind.Headset : Kind.Other;
        string name = null;
        byte ni = FeatureIndex(h, idx, 0x0005, out _);
        if (ni != 0)
        {
            if (Req(h, idx, ni, 2 /*getDeviceType*/, 0, 0, 200, out _) == 1)
                kind = Hid.Rx[4] switch { 0 or 2 => Kind.Keyboard, 3 or 4 or 5 => Kind.Mouse, 8 => Kind.Headset, _ => kind };
            if (Req(h, idx, ni, 0 /*getCount*/, 0, 0, 200, out _) == 1)
            {
                int len = Math.Min((int)Hid.Rx[4], 48);
                var sb = new System.Text.StringBuilder(len);
                while (sb.Length < len && Req(h, idx, ni, 1 /*getName*/, (byte)sb.Length, 0, 200, out _) == 1)
                {
                    int before = sb.Length;
                    for (int k = 4; k < 20 && sb.Length < len; k++) if (Hid.Rx[k] != 0) sb.Append((char)Hid.Rx[k]);
                    if (sb.Length == before) break;
                }
                if (sb.Length > 0) name = sb.ToString().Trim();
            }
        }
        name ??= idx == 0xFF && iface.Product.Length > 0 ? iface.Product
               : "Logitech " + (kind == Kind.Mouse ? "mouse" : kind == Kind.Keyboard ? "teclado" : kind == Kind.Headset ? "headset" : "dispositivo");
        if (!name.StartsWith("Logitech", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("G ") && !name.StartsWith("PRO"))
            name = "Logitech " + name;

        return new Dev(Proto.Logitech, kind, name, "logi:" + name)
        { H = h, Path = iface.Path, DevIdx = idx, FeatIdx = bIdx, Feat = bFeat };
    }

    public static PollResult Poll(Dev d)
    {
        byte fn = d.Feat == 0x1004 ? (byte)1 /*get_status*/ : (byte)0;
        int r = Req(d.H, d.DevIdx, d.FeatIdx, fn, 0, 0, 400, out bool io);
        if (io) return PollResult.Lost;
        if (r != 1) return PollResult.Asleep;
        byte* rx = Hid.Rx;
        switch (d.Feat)
        {
            case 0x1004:
            {
                int pct = rx[4];
                if (pct == 0 && rx[5] != 0) // só níveis: crítico/baixo/bom/cheio
                    pct = (rx[5] & 8) != 0 ? 90 : (rx[5] & 4) != 0 ? 50 : (rx[5] & 2) != 0 ? 20 : 5;
                d.MarkRead(pct, rx[6] == 1 || rx[6] == 2, rx[6] == 3);
                break;
            }
            case 0x1000:
                d.MarkRead(rx[4], rx[6] == 1 || rx[6] == 2 || rx[6] == 4, rx[6] == 3);
                break;
            case 0x1001:
            {
                int mv = (rx[4] << 8) | rx[5]; byte fl = rx[6];
                bool chg = (fl & 0x80) != 0;
                d.MilliVolts = mv;
                d.MarkRead(Proto_.LipoPct(mv), chg && (fl & 7) != 1, chg && (fl & 7) == 1);
                break;
            }
            case 0x1F20:
            {
                int mv = (rx[4] << 8) | rx[5]; byte fl = rx[6];
                if (mv == 0) return PollResult.Asleep;
                d.MilliVolts = mv;
                d.MarkRead(Proto_.LipoPct(mv), (fl & 1) != 0 && (fl & 2) == 0, (fl & 3) == 3);
                break;
            }
        }
        return PollResult.Ok;
    }
}

// ============================================================================================
// Razer (mesmo protocolo do projeto OpenRazer): feature report de 90 bytes.
// Classe 0x07 / comando 0x80 = nível (0..255), 0x84 = carregando. Só leitura.
// O "transaction id" varia por modelo (0x1F, 0x3F ou 0xFF): descobre na varredura.
// ============================================================================================
internal static unsafe class Razer
{
    static readonly byte[] TxIds = { 0x1F, 0x3F, 0xFF };

    public static void Scan(List<Hid.Iface> ifs, List<Dev> found)
    {
        foreach (var i in ifs)
        {
            if (i.Vid != 0x1532 || i.FeatLen != 91) continue;
            string name = i.Product.Length > 0 ? i.Product : "Razer";
            if (!name.StartsWith("Razer", StringComparison.OrdinalIgnoreCase)) name = "Razer " + name;
            string key = "razer:" + name;
            if (Proto_.Exists(found, key)) continue;
            nint h = Hid.OpenFeature(i.Path);
            if (h == -1) continue;
            byte tx = 0, asleepTx = 0;
            foreach (byte t in TxIds)
            {
                int st = Cmd(h, t, 0x07, 0x80);
                if (st == 2) { tx = t; break; }
                if (st == 4 && asleepTx == 0) asleepTx = t; // sem fio dormindo
            }
            if (tx == 0) tx = asleepTx;
            if (tx == 0) { Hid.Release(h); continue; } // sem bateria / não responde
            var kind = i.UsagePage == 1 && i.Usage == 6 ? Kind.Keyboard : Kind.Mouse;
            found.Add(new Dev(Proto.Razer, kind, name, key) { H = h, Path = i.Path, TxId = tx });
        }
    }

    // Retorna o status Razer (2 = sucesso, 4 = dispositivo sem fio inacessível, 5 = não suportado), 0 = falha de I/O.
    static int Cmd(nint h, byte tx, byte cls, byte id)
    {
        byte* b = Hid.Tx;
        NativeMemory.Clear(b, 91);
        byte* r = b + 1;            // b[0] = report id 0
        r[1] = tx; r[5] = 0x02; r[6] = cls; r[7] = id;
        byte crc = 0; for (int k = 2; k < 88; k++) crc ^= r[k];
        r[88] = crc;
        if (HidD_SetFeature(h, b, 91) == 0) return 0;
        for (int t = 0; t < 8; t++)
        {
            Sleep(t == 0 ? 2u : 8u);
            NativeMemory.Clear(Hid.Rx, 91);
            if (HidD_GetFeature(h, Hid.Rx, 91) == 0) return 0;
            byte* a = Hid.Rx + 1;
            if (a[0] == 0x01 || a[0] == 0x00) continue;     // ocupado / ainda sem resposta
            if (a[6] != cls || a[7] != id) continue;
            return a[0];
        }
        return 1;
    }

    public static PollResult Poll(Dev d)
    {
        int st = Cmd(d.H, d.TxId, 0x07, 0x80);
        if (st != 2)
            foreach (byte t in TxIds)
                if (t != d.TxId && (st = Cmd(d.H, t, 0x07, 0x80)) == 2) { d.TxId = t; break; }
        if (st == 0) return PollResult.Lost;
        if (st != 2) return PollResult.Asleep;
        int level = Hid.Rx[1 + 9];
        bool chg = Cmd(d.H, d.TxId, 0x07, 0x84) == 2 && Hid.Rx[1 + 9] == 1;
        d.MarkRead((level * 100 + 127) / 255, chg, false);
        return PollResult.Ok;
    }
}

// ============================================================================================
// Bluetooth (qualquer marca): lê a mesma % que o Windows mostra em Configurações > Bluetooth.
// Não conversa com o dispositivo; só consulta uma propriedade do sistema.
// ============================================================================================
internal static unsafe class Bluetooth
{
    static DEVPROPKEY KBattery = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
    static DEVPROPKEY KConnected = new("83DA6326-97A6-4088-9453-A1923F573B29", 15);
    static DEVPROPKEY KName = new("B725F130-47EF-101A-A5F1-02608C9EEBAC", 10);
    static DEVPROPKEY KContainer = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);
    static DEVPROPKEY KClassOfDevice = new("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A", 10);

    public static void Scan(List<Dev> found)
    {
        var byContainer = new Dictionary<Guid, Dev>();
        foreach (var en in new[] { "BTHENUM", "BTHLE", "BTHLEDEVICE" })
            foreach (var id in ListIds(en))
            {
                uint dn;
                fixed (char* p = id) if (CM_Locate_DevNodeW(&dn, p, 0) != 0) continue;
                if (GetUInt(dn, ref KBattery) is not uint) continue;
                Guid c = GetGuid(dn, ref KContainer);
                string name = GetString(dn, ref KName) ?? id;
                bool devNode = id.Contains("\\DEV_", StringComparison.OrdinalIgnoreCase);
                if (c != Guid.Empty && byContainer.TryGetValue(c, out var ex))
                {
                    if (devNode) ex.Name = name; // o nó "Dev_" tem o nome do aparelho; os outros, o do serviço
                    continue;
                }
                var d = new Dev(Proto.Bluetooth, Kind.Other, name, "bt:" + (c != Guid.Empty ? c.ToString("N") : id)) { InstanceId = id };
                d.Kind = KindOf(dn, name);
                if (c != Guid.Empty) byContainer[c] = d;
                found.Add(d);
            }
        foreach (var d in byContainer.Values)
            if (d.Kind == Kind.Other) d.Kind = KindOf(0, d.Name);
    }

    public static PollResult Poll(Dev d)
    {
        uint dn;
        fixed (char* p = d.InstanceId) if (CM_Locate_DevNodeW(&dn, p, 0) != 0) return PollResult.Lost;
        uint? connected = GetUInt(dn, ref KConnected);
        d.Hidden = connected == 0;                 // pareado mas desconectado: esconde o ícone
        if (d.Hidden) return PollResult.Asleep;
        if (GetUInt(dn, ref KBattery) is not uint pct) return PollResult.Asleep;
        d.MarkRead((int)pct, false, false);
        return PollResult.Ok;
    }

    static Kind KindOf(uint dn, string name)
    {
        if (dn != 0 && GetUInt(dn, ref KClassOfDevice) is uint cod && cod != 0)
        {
            uint major = (cod >> 8) & 0x1F, minor = (cod >> 6) & 0x3;
            if (major == 4) return Kind.Headset;
            if (major == 5) return minor == 2 ? Kind.Mouse : minor != 0 ? Kind.Keyboard : Kind.Other;
        }
        string n = name.ToLowerInvariant();
        if (n.Contains("mouse") || n.Contains("mx ") || n.Contains("trackball")) return Kind.Mouse;
        if (n.Contains("keyboard") || n.Contains("teclado") || n.Contains("keys")) return Kind.Keyboard;
        if (n.Contains("head") || n.Contains("bud") || n.Contains("pods") || n.Contains("fone") || n.Contains("ear") ||
            n.Contains("wh-") || n.Contains("wf-") || n.Contains("audio") || n.Contains("jbl") || n.Contains("sound")) return Kind.Headset;
        return Kind.Other;
    }

    static List<string> ListIds(string enumerator)
    {
        var res = new List<string>();
        uint len = 0;
        const uint flags = 0x1 /*FILTER_ENUMERATOR*/ | 0x100 /*FILTER_PRESENT*/;
        fixed (char* f = enumerator)
        {
            if (CM_Get_Device_ID_List_SizeW(&len, f, flags) != 0 || len <= 1) return res;
            var buf = new char[len];
            fixed (char* b = buf)
                if (CM_Get_Device_ID_ListW(f, b, len, flags) != 0) return res;
            int start = 0;
            for (int i = 0; i < buf.Length; i++)
                if (buf[i] == '\0') { if (i > start) res.Add(new string(buf, start, i - start)); start = i + 1; }
        }
        return res;
    }

    static uint? GetUInt(uint dn, ref DEVPROPKEY k)
    {
        uint type, size = 8; ulong v = 0;
        fixed (DEVPROPKEY* pk = &k)
            if (CM_Get_DevNode_PropertyW(dn, pk, &type, (byte*)&v, &size, 0) != 0) return null;
        // BYTE (0x3), BOOLEAN (0x11: 0xFF = verdadeiro), UINT16/32
        return type switch { 0x3 => (uint)(v & 0xFF), 0x11 => (v & 0xFF) != 0 ? 1u : 0u, 0x5 => (uint)(v & 0xFFFF), _ => (uint)v };
    }

    static Guid GetGuid(uint dn, ref DEVPROPKEY k)
    {
        uint type, size = 16; Guid g = default;
        fixed (DEVPROPKEY* pk = &k)
            if (CM_Get_DevNode_PropertyW(dn, pk, &type, (byte*)&g, &size, 0) != 0) return Guid.Empty;
        return g;
    }

    static string GetString(uint dn, ref DEVPROPKEY k)
    {
        uint type, size = 512; char* buf = stackalloc char[256];
        fixed (DEVPROPKEY* pk = &k)
            if (CM_Get_DevNode_PropertyW(dn, pk, &type, (byte*)buf, &size, 0) != 0) return null;
        var s = new string(buf, 0, (int)Math.Min(size / 2, 255)).TrimEnd('\0').Trim();
        return s.Length > 0 ? s : null;
    }
}
