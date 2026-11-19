using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using static BateriaTray.N;

namespace BateriaTray;

// Relatório (BateriaTray.exe --report): processo separado e de vida curta. Lê o histórico, gera uma
// página HTML, abre no navegador e termina. Nada fica rodando depois que a página é fechada.
internal static unsafe class Report
{
    readonly record struct R(DateTime T, int Pct, char St);

    sealed class DevH
    {
        public string Key, Name, Kind;
        public readonly List<R> L = new();
    }

    sealed class Session { public DateTime Start, End; public int StartPct, MaxPct; public bool Open; }

    const double GapH = 3;                 // sem leituras por mais que isso = aparelho desligado
    static readonly string[] Wd = { "dom", "seg", "ter", "qua", "qui", "sex", "sáb" };
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // --report [saida.html [historico.csv]]: com saída informada, só grava (não abre o navegador).
    public static int Run(string[] args)
    {
        string src = args.Length > 2 ? args[2] : History.File_;
        var devs = Load(src);
        var now = DateTime.Now;
        string html = Build(devs, now);
        string path = args.Length > 1 ? args[1] : Path.Combine(History.Dir, "relatorio.html");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        File.WriteAllText(path, html, new UTF8Encoding(false));
        if (args.Length <= 1)
            fixed (char* op = "open") fixed (char* f = path) ShellExecuteW(0, op, f, null, null, 1);
        return 0;
    }

    static List<DevH> Load(string src)
    {
        var map = new Dictionary<string, DevH>();
        var order = new List<DevH>();
        if (!File.Exists(src)) return order;
        foreach (var line in File.ReadLines(src))
        {
            var p = line.Split(';');
            if (p.Length < 6 || !DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm", Inv, DateTimeStyles.None, out var t)) continue;
            if (!int.TryParse(p[3], out int pct) || p[4].Length != 1) continue;
            if (!map.TryGetValue(p[1], out var d)) { d = new DevH { Key = p[1] }; map[p[1]] = d; order.Add(d); }
            d.Name = p[5]; d.Kind = p[2];
            d.L.Add(new R(t, pct, p[4][0]));
        }
        foreach (var d in order) d.L.Sort((a, b) => a.T.CompareTo(b.T));
        return order;
    }

    // ============================== cálculos ==============================

    static List<Session> Sessions(List<R> L, DateTime now)
    {
        var s = new List<Session>();
        int i = 0;
        while (i < L.Count)
        {
            if (L[i].St == 'd') { i++; continue; }
            var x = new Session { Start = L[i].T, StartPct = L[i].Pct, MaxPct = L[i].Pct };
            while (i < L.Count && L[i].St != 'd') { x.MaxPct = Math.Max(x.MaxPct, L[i].Pct); i++; }
            if (i < L.Count) x.End = L[i].T;
            else { x.Open = true; x.End = now - L[^1].T < TimeSpan.FromHours(GapH) ? now : L[^1].T; }
            s.Add(x);
        }
        return s;
    }

    // Consumo médio em %/hora, só em trechos descarregando com leituras próximas (aparelho em uso).
    static (double rate, double hours) Drain(List<R> L)
    {
        double drop = 0, hours = 0;
        for (int i = 1; i < L.Count; i++)
        {
            var a = L[i - 1]; var b = L[i];
            double h = (b.T - a.T).TotalHours;
            if (a.St != 'd' || b.St != 'd' || h <= 0 || h > GapH) continue;
            drop += Math.Max(0, a.Pct - b.Pct);
            hours += h;
        }
        return hours >= 2 && drop >= 3 ? (drop / hours, hours) : (-1, hours);
    }

    static double ChargeRate(List<Session> ss)
    {
        double gain = 0, hours = 0;
        foreach (var s in ss)
        {
            double h = (s.End - s.Start).TotalHours;
            if (h < 0.25 || s.MaxPct - s.StartPct < 5) continue;
            gain += s.MaxPct - s.StartPct; hours += h;
        }
        return hours > 0 ? gain / hours : -1;
    }

    // ============================== formatação ==============================

    static string Num(double v, int dec) => Math.Round(v, dec).ToString(dec == 0 ? "0" : "0." + new string('#', dec), Inv).Replace('.', ',');

    static string Dur(double h)
    {
        if (h < 1) return $"{Math.Max(1, Math.Round(h * 60))} min";
        if (h < 48) return $"{Math.Round(h)} h";
        int d = (int)(h / 24); int hh = (int)Math.Round(h - d * 24);
        return hh > 0 ? $"{d} dias e {hh} h" : $"{d} dias";
    }

    static string When(DateTime t, DateTime now)
    {
        var dd = (now.Date - t.Date).Days;
        string hm = t.ToString("HH:mm", Inv);
        return dd == 0 ? $"hoje às {hm}" : dd == 1 ? $"ontem às {hm}" : $"{Wd[(int)t.DayOfWeek]}, {t:dd/MM} às {hm}";
    }

    static string Ago(DateTime t, DateTime now)
    {
        var s = now - t;
        if (s.TotalMinutes < 60) return $"há {Math.Max(1, (int)s.TotalMinutes)} min";
        if (s.TotalHours < 24) return $"há {(int)s.TotalHours} h";
        int d = (int)s.TotalDays;
        return d == 1 ? "há 1 dia" : $"há {d} dias";
    }

    static string StateWord(char st) => st == 'c' ? "carregando" : st == 'f' ? "carga completa" : "descarregando";

    static string KindWord(string k) => k switch { "Mouse" => "Mouse", "Headset" => "Headset", "Keyboard" => "Teclado", _ => "Dispositivo" };

    static string H(string s) => WebUtility.HtmlEncode(s);

    // ============================== HTML ==============================

    static string Build(List<DevH> devs, DateTime now)
    {
        var sb = new StringBuilder(32 * 1024);
        sb.Append("<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>Relatório de bateria</title><style>").Append(Css).Append("</style></head><body><main>");
        DateTime first = DateTime.MaxValue;
        foreach (var d in devs) if (d.L.Count > 0 && d.L[0].T < first) first = d.L[0].T;
        sb.Append("<header><h1>Relatório de bateria</h1><p class=\"sub\">Gerado ").Append(When(now, now));
        if (first != DateTime.MaxValue) sb.Append(" · histórico desde ").Append(first.ToString("dd/MM/yyyy", Inv));
        sb.Append("</p></header>");

        if (devs.Count == 0)
            sb.Append("<section class=\"card empty\"><h2>Ainda não há leituras</h2><p>O histórico começa a ser gravado a partir de agora. Volte em algumas horas para ver consumo e duração estimada.</p></section>");

        int chartId = 0;
        foreach (var d in devs) DeviceCard(sb, d, now, chartId++);

        AppCard(sb);
        sb.Append("<p class=\"foot\">As estimativas usam o histórico gravado enquanto o PC está ligado: períodos sem leituras por mais de 3 h (aparelho desligado) ficam de fora. Elas melhoram com alguns dias de uso.</p>");
        sb.Append("</main><div id=\"tip\" role=\"status\" hidden></div><script>").Append(Js).Append("</script></body></html>");
        return sb.ToString();
    }

    static void DeviceCard(StringBuilder sb, DevH d, DateTime now, int id)
    {
        var L = d.L; var last = L[^1];
        var ss = Sessions(L, now);
        var (rate, obsH) = Drain(L);
        double crate = ChargeRate(ss);
        bool charging = last.St != 'd';
        bool stale = now - last.T > TimeSpan.FromHours(GapH);
        int rech30 = 0, low30 = 0;
        foreach (var s in ss) if (s.Start >= now.AddDays(-30)) rech30++;
        for (int i = 1; i < L.Count; i++) if (L[i].T >= now.AddDays(-30) && L[i - 1].Pct > 15 && L[i].Pct <= 15) low30++;

        sb.Append("<section class=\"card\"><div class=\"head\"><div><span class=\"kind\">").Append(KindWord(d.Kind)).Append("</span><h2>")
          .Append(H(d.Name)).Append("</h2></div></div><div class=\"tiles\">");

        // Agora
        string lvl = last.Pct <= 20 ? "crit" : last.Pct < 60 ? "warn" : "good";
        sb.Append("<div class=\"tile hero\"><span class=\"lbl\">Última leitura</span><span class=\"big\">").Append(last.Pct).Append("<small>%</small></span>")
          .Append("<span class=\"meter\"><i class=\"").Append(lvl).Append("\" style=\"width:").Append(last.Pct).Append("%\"></i></span>")
          .Append("<span class=\"note\">").Append(StateWord(last.St)).Append(" · ").Append(When(last.T, now)).Append("</span></div>");

        // Duração de uma carga completa
        Tile(sb, "Dura uma carga", rate > 0 ? "≈ " + Dur(100 / rate) : "—",
             rate > 0 ? "de uso, pelo consumo médio" : $"coletando dados ({Dur(obsH)} de uso registrado)");
        // Restante
        string rest = charging ? "carregando" : rate > 0 ? "≈ " + Dur(last.Pct / rate) : "—";
        Tile(sb, "Restante", rest, charging ? "na tomada agora" : rate > 0 ? (stale ? "a partir da última leitura" : "no ritmo atual") : "precisa de algumas horas de uso");
        // Consumo
        Tile(sb, "Consumo médio", rate > 0 ? Num(rate, rate < 10 ? 1 : 0) + " %/h" : "—", rate > 0 ? "enquanto está em uso" : "ainda calculando");
        // Recarga
        var lastS = ss.Count > 0 ? ss[^1] : null;
        Tile(sb, "Última recarga", lastS == null ? "—" : lastS.Open ? "agora" : Ago(lastS.End, now),
             lastS == null ? "nenhuma registrada ainda" : $"{lastS.StartPct}% → {lastS.MaxPct}%, {When(lastS.Start, now)}");
        Tile(sb, "Tempo de carga", crate > 0 ? "≈ " + Dur(100 / crate) : "—", crate > 0 ? "de 0 a 100%, estimado" : "ainda sem recarga completa");
        Tile(sb, "Recargas (30 dias)", rech30.ToString(), low30 > 0 ? $"chegou a 15% ou menos {low30}×" : "sem bateria fraca");
        sb.Append("</div>");

        Chart(sb, L, ss, now, id, d.Name);

        // Tabela (acessibilidade / dados exatos)
        sb.Append("<details><summary>Ver leituras</summary><div class=\"tw\"><table><thead><tr><th>Data e hora</th><th class=\"n\">Bateria</th><th>Estado</th></tr></thead><tbody>");
        for (int i = L.Count - 1, k = 0; i >= 0 && k < 60; i--, k++)
            sb.Append("<tr><td>").Append(Wd[(int)L[i].T.DayOfWeek]).Append(' ').Append(L[i].T.ToString("dd/MM HH:mm", Inv))
              .Append("</td><td class=\"n\">").Append(L[i].Pct).Append("%</td><td>").Append(StateWord(L[i].St)).Append("</td></tr>");
        sb.Append("</tbody></table></div></details></section>");
    }

    static void Tile(StringBuilder sb, string label, string value, string note) =>
        sb.Append("<div class=\"tile\"><span class=\"lbl\">").Append(label).Append("</span><span class=\"val\">").Append(H(value))
          .Append("</span><span class=\"note\">").Append(H(note)).Append("</span></div>");

    // Gráfico em degraus da % nos últimos 7 dias, com faixas de carregamento. SVG puro + dica ao passar o mouse.
    static void Chart(StringBuilder sb, List<R> L, List<Session> ss, DateTime now, int id, string name)
    {
        const double W = 760, Hh = 220, pl = 44, pr = 12, pt = 12, pb = 28;
        DateTime t1 = now, t0 = now.AddDays(-7);
        double X(DateTime t) => pl + (t - t0).TotalSeconds / (t1 - t0).TotalSeconds * (W - pl - pr);
        double Y(int p) => pt + (100 - p) / 100.0 * (Hh - pt - pb);
        string F(double v) => v.ToString("0.#", Inv);

        // pontos na janela (inclui o último antes do início, para o valor de partida)
        var pts = new List<R>();
        for (int i = 0; i < L.Count; i++)
        {
            if (L[i].T < t0) { if (i + 1 < L.Count && L[i + 1].T >= t0 || i + 1 == L.Count) pts.Add(L[i] with { T = t0 }); continue; }
            pts.Add(L[i]);
        }

        sb.Append("<div class=\"chart\"><div class=\"ch\"><h3>Nível nos últimos 7 dias</h3><span class=\"keys\"><span class=\"k chg\"></span>carregando<span class=\"k gap\"></span>sem leituras</span></div>");
        sb.Append("<svg viewBox=\"0 0 ").Append(W).Append(' ').Append(Hh).Append("\" role=\"img\" aria-label=\"Nível de bateria de ").Append(H(name)).Append(" nos últimos 7 dias\" data-chart=\"").Append(id).Append("\">");
        // grade
        foreach (int g in new[] { 0, 25, 50, 75, 100 })
            sb.Append("<line class=\"grid\" x1=\"").Append(pl).Append("\" x2=\"").Append(W - pr).Append("\" y1=\"").Append(F(Y(g))).Append("\" y2=\"").Append(F(Y(g)))
              .Append("\"/><text class=\"ax\" x=\"").Append(pl - 8).Append("\" y=\"").Append(F(Y(g) + 4)).Append("\" text-anchor=\"end\">").Append(g).Append("%</text>");
        for (var day = t0.Date.AddDays(1); day <= t1; day = day.AddDays(1))
        {
            double x = X(day);
            sb.Append("<line class=\"grid v\" x1=\"").Append(F(x)).Append("\" x2=\"").Append(F(x)).Append("\" y1=\"").Append(pt).Append("\" y2=\"").Append(Hh - pb).Append("\"/>");
            if (x + 40 <= W) // rótulo só se couber
                sb.Append("<text class=\"ax\" x=\"").Append(F(x + 4)).Append("\" y=\"").Append(Hh - 9).Append("\">").Append(Wd[(int)day.DayOfWeek]).Append(' ').Append(day.Day).Append("</text>");
        }
        // faixas de carregamento
        foreach (var s in ss)
        {
            if (s.End < t0) continue;
            double a = X(s.Start < t0 ? t0 : s.Start), b = X(s.End > t1 ? t1 : s.End);
            sb.Append("<rect class=\"chg\" x=\"").Append(F(a)).Append("\" y=\"").Append(pt).Append("\" width=\"").Append(F(Math.Max(2, b - a))).Append("\" height=\"").Append(Hh - pt - pb).Append("\"/>");
        }
        // linha em degraus (sólida com leituras; tracejada nos buracos > 3 h)
        var solid = new StringBuilder(); var gaps = new StringBuilder();
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            DateTime nextT = i + 1 < pts.Count ? pts[i + 1].T : (now - a.T < TimeSpan.FromHours(GapH) ? now : a.T.AddHours(GapH) > now ? now : a.T.AddHours(GapH));
            bool gap = (nextT - a.T).TotalHours > GapH;
            var target = gap ? gaps : solid;
            target.Append('M').Append(F(X(a.T))).Append(',').Append(F(Y(a.Pct))).Append('H').Append(F(X(nextT)));
            if (i + 1 < pts.Count) target.Append('V').Append(F(Y(pts[i + 1].Pct)));
        }
        if (gaps.Length > 0) sb.Append("<path class=\"gapl\" d=\"").Append(gaps).Append("\"/>");
        if (solid.Length > 0) sb.Append("<path class=\"line\" d=\"").Append(solid).Append("\"/>");
        sb.Append("<line class=\"cross\" y1=\"").Append(pt).Append("\" y2=\"").Append(Hh - pb).Append("\" hidden/><circle class=\"dot\" r=\"4\" hidden/>");
        sb.Append("<rect class=\"hit\" x=\"").Append(pl).Append("\" y=\"0\" width=\"").Append(W - pl - pr).Append("\" height=\"").Append(Hh).Append("\"/></svg>");
        // dados para a dica (segundos desde t0, %, estado)
        sb.Append("<script type=\"application/json\" id=\"d").Append(id).Append("\">{\"t0\":").Append(((DateTimeOffset)t0).ToUnixTimeSeconds())
          .Append(",\"t1\":").Append(((DateTimeOffset)t1).ToUnixTimeSeconds()).Append(",\"pl\":").Append(pl).Append(",\"pr\":").Append(pr).Append(",\"pt\":").Append(pt).Append(",\"pb\":").Append(pb)
          .Append(",\"w\":").Append(W).Append(",\"h\":").Append(Hh).Append(",\"p\":[");
        for (int i = 0; i < pts.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('[').Append(((DateTimeOffset)pts[i].T).ToUnixTimeSeconds()).Append(',').Append(pts[i].Pct).Append(",\"").Append(pts[i].St).Append("\"]");
        }
        sb.Append("]}</script></div>");
    }

    // Consumo do próprio BateriaTray (o processo da bandeja, não este que gera o relatório).
    static void AppCard(StringBuilder sb)
    {
        try
        {
            var me = Environment.ProcessId;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("BateriaTray"))
            {
                if (p.Id == me) continue;
                double mb = p.PrivateMemorySize64 / 1048576.0, cpu = p.TotalProcessorTime.TotalSeconds;
                double up = (DateTime.Now - p.StartTime).TotalHours;
                long hist = File.Exists(History.File_) ? new FileInfo(History.File_).Length : 0;
                sb.Append("<section class=\"card app\"><div class=\"head\"><div><span class=\"kind\">Este app</span><h2>BateriaTray</h2></div></div><div class=\"tiles\">");
                Tile(sb, "Memória", Num(mb, 1) + " MB", "RAM privada");
                Tile(sb, "Processador", Num(cpu, 1) + " s", $"no total, em {Dur(up)} ligado");
                Tile(sb, "Uso médio de CPU", Num(cpu / Math.Max(up * 3600, 1) * 100, 3) + " %", "de um núcleo");
                Tile(sb, "Histórico", Num(hist / 1024.0, 1) + " KB", "no disco (até 90 dias)");
                sb.Append("</div></section>");
                return;
            }
        }
        catch { }
    }

    const string Css = """
:root{color-scheme:light;--bg:#f4f3f0;--surface:#fcfcfb;--line:#e4e2dc;--text:#0b0b0b;--text2:#52514e;--muted:#8a8984;
--series:#2a78d6;--good:#0ca30c;--warn:#eda100;--crit:#d03b3b;--chg:rgba(12,163,12,.13)}
@media (prefers-color-scheme:dark){:root{color-scheme:dark;--bg:#111110;--surface:#1a1a19;--line:#2e2e2b;--text:#f0efec;--text2:#c3c2b7;--muted:#86857e;
--series:#3987e5;--good:#0ca30c;--warn:#c98500;--crit:#e66767;--chg:rgba(12,163,12,.2)}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif}
main{max-width:860px;margin:0 auto;padding:32px 20px 48px}
header h1{font-size:26px;font-weight:600;margin:0}.sub{color:var(--text2);margin:4px 0 24px}
.card{background:var(--surface);border:1px solid var(--line);border-radius:14px;padding:20px 20px 12px;margin-bottom:18px}
.card.empty{padding-bottom:20px}.card.empty p{color:var(--text2);margin:6px 0 0}
.head{display:flex;justify-content:space-between;align-items:flex-start;margin-bottom:14px}
.kind{font-size:12px;letter-spacing:.04em;text-transform:uppercase;color:var(--muted)}
h2{font-size:18px;font-weight:600;margin:2px 0 0}h3{font-size:13px;font-weight:600;margin:0;color:var(--text2)}
.tiles{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:10px}
.tile{border:1px solid var(--line);border-radius:10px;padding:10px 12px;display:flex;flex-direction:column;gap:2px;min-width:0}
.tile.hero{grid-row:span 2;justify-content:center}
.lbl{font-size:12px;color:var(--text2)}.val{font-size:20px;font-weight:600;font-variant-numeric:tabular-nums}
.big{font-size:44px;font-weight:600;line-height:1.05;font-variant-numeric:tabular-nums}.big small{font-size:20px;font-weight:500;color:var(--text2)}
.note{font-size:12px;color:var(--muted)}
.meter{display:block;height:6px;border-radius:3px;background:var(--line);overflow:hidden;margin:6px 0 4px}
.meter i{display:block;height:100%;border-radius:3px}.meter .good{background:var(--good)}.meter .warn{background:var(--warn)}.meter .crit{background:var(--crit)}
.chart{margin-top:18px}.ch{display:flex;justify-content:space-between;align-items:baseline;flex-wrap:wrap;gap:6px;margin-bottom:4px}
.keys{font-size:12px;color:var(--text2);display:flex;align-items:center;gap:6px}
.k{display:inline-block;width:14px;height:10px;border-radius:2px;margin-left:8px}.k.chg{background:var(--chg);border:1px solid rgba(12,163,12,.45)}
.k.gap{height:0;border-top:2px dashed var(--muted);border-radius:0}
svg{width:100%;height:auto;display:block;overflow:visible}
.grid{stroke:var(--line);stroke-width:1}.grid.v{stroke-dasharray:2 4}.ax{fill:var(--muted);font-size:11px}
.chg{fill:var(--chg)}.line{fill:none;stroke:var(--series);stroke-width:2;stroke-linejoin:round}
.gapl{fill:none;stroke:var(--muted);stroke-width:1.5;stroke-dasharray:4 4}
.cross{stroke:var(--text2);stroke-width:1}.dot{fill:var(--series);stroke:var(--surface);stroke-width:2}
.hit{fill:transparent;cursor:crosshair}
svg [hidden]{display:none}
#tip{position:fixed;pointer-events:none;background:var(--surface);color:var(--text);border:1px solid var(--line);border-radius:8px;
padding:6px 9px;font-size:12px;box-shadow:0 4px 14px rgba(0,0,0,.14);white-space:nowrap;z-index:9}
#tip b{font-variant-numeric:tabular-nums}
details{margin:14px 0 6px}summary{cursor:pointer;color:var(--text2);font-size:13px}
.tw{max-height:260px;overflow:auto;margin-top:8px}table{border-collapse:collapse;width:100%;font-size:13px}
th,td{text-align:left;padding:5px 8px;border-bottom:1px solid var(--line)}th{color:var(--text2);font-weight:600;position:sticky;top:0;background:var(--surface)}
.n{text-align:right;font-variant-numeric:tabular-nums}
.foot{color:var(--muted);font-size:12px;margin-top:8px}
@media (max-width:520px){.tile.hero{grid-row:auto}.big{font-size:36px}}
""";

    const string Js = """
(()=>{const tip=document.getElementById('tip');const wd=['dom','seg','ter','qua','qui','sex','sáb'];
const st={d:'descarregando',c:'carregando',f:'carga completa'};const p2=n=>String(n).padStart(2,'0');
document.querySelectorAll('svg[data-chart]').forEach(svg=>{
const D=JSON.parse(document.getElementById('d'+svg.dataset.chart).textContent);
const cross=svg.querySelector('.cross'),dot=svg.querySelector('.dot'),hit=svg.querySelector('.hit');
const iw=D.w-D.pl-D.pr,ih=D.h-D.pt-D.pb;
const hide=()=>{cross.setAttribute('hidden','');dot.setAttribute('hidden','');tip.hidden=true};
hit.addEventListener('mouseleave',hide);
hit.addEventListener('mousemove',e=>{const r=svg.getBoundingClientRect();const x=(e.clientX-r.left)*D.w/r.width;
const t=D.t0+(x-D.pl)/iw*(D.t1-D.t0);let k=-1;for(let i=0;i<D.p.length;i++){if(D.p[i][0]<=t)k=i;else break}
if(k<0){hide();return}const pt=D.p[k],y=D.pt+(100-pt[1])/100*ih;
cross.setAttribute('x1',x);cross.setAttribute('x2',x);cross.removeAttribute('hidden');
dot.setAttribute('cx',x);dot.setAttribute('cy',y);dot.removeAttribute('hidden');
const d=new Date(t*1000);tip.innerHTML=`${wd[d.getDay()]} ${p2(d.getDate())}/${p2(d.getMonth()+1)} ${p2(d.getHours())}:${p2(d.getMinutes())} · <b>${pt[1]}%</b> · ${st[pt[2]]}`;
tip.hidden=false;let left=e.clientX+14;if(left+tip.offsetWidth>innerWidth-8)left=e.clientX-tip.offsetWidth-14;
tip.style.left=left+'px';tip.style.top=(e.clientY-36)+'px'});});})();
""";
}
