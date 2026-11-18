namespace BateriaTray;

internal enum Kind { Mouse, Headset, Keyboard, Other }

internal enum Proto { Compx, Blk, Logitech, Razer, Bluetooth }

// Um dispositivo com bateria (um ícone na bandeja).
internal sealed class Dev
{
    // identidade
    public string Key;            // estável entre execuções (define o uID do ícone)
    public uint Id;               // uID na bandeja
    public Proto Proto;
    public Kind Kind;
    public string Name;

    // conexão (preenchida a cada varredura)
    public nint H = -1;           // handle HID (não é dono: os handles ficam em Hid.Handles)
    public nint H2 = -1;          // caminho alternativo (ex.: mesmo mouse pelo cabo e pelo receptor)
    public string Path;           // interface HID (para saber se o receptor ainda está conectado)
    public bool Hidden;           // Bluetooth pareado porém desconectado
    public byte DevIdx;           // Logitech: índice no receptor (1..6) ou 0xFF (direto)
    public byte FeatIdx;          // Logitech: índice da feature de bateria
    public ushort Feat;           // Logitech: id da feature de bateria (0x1004/0x1000/0x1001/0x1F20)
    public byte TxId;             // Razer: transaction id
    public string InstanceId;     // Bluetooth: id do nó PnP

    // estado
    public bool Present;
    public int Pct = -1;
    public int MilliVolts;
    public bool Charging, Full, Asleep;
    public string ReadAt = "";
    public long NextPoll;

    // bandeja
    public nint Icon;
    public bool Shown, LowWarned;
    public string LastSig;

    public Dev(Proto p, Kind k, string name, string key)
    {
        Proto = p; Kind = k; Name = name; Key = key;
        // FNV-1a: uID estável a partir da chave (o Windows lembra a posição por uID)
        uint h = 2166136261;
        foreach (char c in key) { h ^= c; h *= 16777619; }
        Id = (h & 0x7FFFFFFF) | 0x100;
    }

    public void MarkRead(int pct, bool charging, bool full)
    {
        Pct = pct < 0 ? 0 : pct > 100 ? 100 : pct;
        Charging = charging; Full = full; Asleep = false;
        SYSTEMTIME t; unsafe { N.GetLocalTime(&t); }
        ReadAt = t.Hour.ToString("00") + ":" + t.Minute.ToString("00");
    }
}
