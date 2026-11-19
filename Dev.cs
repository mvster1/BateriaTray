namespace BateriaTray;

internal enum Kind { Mouse, Headset, Keyboard, Other }

internal enum Proto
{
    Compx, Blk, Logitech, Razer, Bluetooth,
    HyperX, SteelSeries, CorsairBragi, CorsairVoid, Glorious, Lamzu, Pulsar, Endgame, Areson, Xenta, Generic
}

// Um dispositivo com bateria (um ícone na bandeja).
internal sealed class Dev
{
    // identidade
    public string Key;            // estável entre execuções (define o uID do ícone)
    public uint Id;               // uID na bandeja
    public Proto Proto;
    public Kind Kind;
    public string Name;
    public ushort Vid, Pid;
    public int Sub;               // variante do protocolo (modelo)

    // conexão (preenchida a cada varredura)
    public nint H = -1;           // handle HID (não é dono: os handles ficam em Hid.Handles)
    public nint H2 = -1;          // segundo handle (caminho alternativo ou coleção de resposta)
    public string Path;           // interface HID principal
    public int OutLen, FeatLen;   // tamanhos de report da interface usada
    public bool Hidden;           // Bluetooth pareado porém desconectado
    public byte DevIdx;           // Logitech: índice no receptor (1..6) ou 0xFF (direto)
    public byte FeatIdx;          // Logitech: índice da feature de bateria
    public ushort Feat;           // Logitech: id da feature de bateria
    public byte TxId;             // Razer: transaction id
    public string InstanceId;     // Bluetooth: id do nó PnP
    public Hid.Iface Bat;         // HID genérico: onde está o valor de bateria padrão
    public System.Collections.Generic.List<Hid.Iface> Cands; // interfaces candidatas (testa uma por vez)
    public int CandIdx;
    public bool Locked;           // já achou a interface certa: para de alternar
    public long FrameAt;          // Attack Shark: quando chegou o último quadro

    // Dispositivos que podem não ter bateria (ex.: teclado Corsair com fio) só aparecem
    // depois da primeira leitura válida.
    public bool NeedConfirm, Confirmed;

    // estado
    public bool Present;
    public int Pct = -1;
    public int MilliVolts;
    public bool Charging, Full, Asleep;
    public string ReadAt = "";
    public long NextPoll;
    public long LinkUpAt;         // HyperX Haste: leitura só é confiável alguns segundos após conectar
    public ulong LastFrame, PrevFrame; // Attack Shark: dois últimos quadros de bateria recebidos
    public int LoggedPct = -2;    // último valor gravado no histórico
    public char LoggedState;

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
        Charging = charging; Full = full; Asleep = false; Confirmed = true;
        SYSTEMTIME t; unsafe { N.GetLocalTime(&t); }
        ReadAt = t.Hour.ToString("00") + ":" + t.Minute.ToString("00");
    }
}
