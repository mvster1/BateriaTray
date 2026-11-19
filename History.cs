using System;
using System.Collections.Generic;
using System.IO;
using static BateriaTray.N;

namespace BateriaTray;

// Histórico de leituras para o relatório. O app da bandeja só acrescenta uma linha quando a % ou o
// estado de carga muda (poucas vezes por hora). Formato: "aaaa-mm-dd hh:mm;chave;tipo;%;estado;nome"
// estado: d = descarregando, c = carregando, f = carga completa.
internal static unsafe class History
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BateriaTray");
    public static readonly string File_ = Path.Combine(Dir, "historico.csv");
    const long MaxBytes = 1_000_000;
    const int KeepDays = 90;

    static bool _ready;

    static void Ensure()
    {
        if (_ready) return;
        _ready = true;
        try { Directory.CreateDirectory(Dir); Compact(); } catch { }
    }

    public static void Log(Dev d)
    {
        if (d.Pct < 0) return;
        char st = d.Charging ? 'c' : d.Full ? 'f' : 'd';
        if (d.Pct == d.LoggedPct && st == d.LoggedState) return;
        d.LoggedPct = d.Pct; d.LoggedState = st;
        Ensure();
        SYSTEMTIME t; GetLocalTime(&t);
        string line = $"{t.Year:0000}-{t.Month:00}-{t.Day:00} {t.Hour:00}:{t.Minute:00};{d.Key};{d.Kind};{d.Pct};{st};{d.Name.Replace(';', ',')}\n";
        try { File.AppendAllText(File_, line); } catch { }
    }

    // Mantém o arquivo pequeno: acima de ~1 MB, descarta o que tem mais de 90 dias.
    static void Compact()
    {
        var fi = new FileInfo(File_);
        if (!fi.Exists || fi.Length < MaxBytes) return;
        string cutoff = DateTime.Now.AddDays(-KeepDays).ToString("yyyy-MM-dd");
        var keep = new List<string>();
        foreach (var l in File.ReadLines(File_))
            if (l.Length > 10 && string.CompareOrdinal(l, 0, cutoff, 0, 10) >= 0) keep.Add(l);
        File.WriteAllLines(File_, keep);
    }
}
