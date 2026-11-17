using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>Uložení výsledku do souboru – HTML v barvách Konzolák (s logem) nebo prostý text.</summary>
public static class ZpravaExport
{
    public static void Uloz(Zprava z, string cesta)
    {
        var obsah = Path.GetExtension(cesta).Equals(".txt", StringComparison.OrdinalIgnoreCase) ? Txt(z) : Html(z);
        File.WriteAllText(cesta, obsah, new UTF8Encoding(true));
    }

    public static string StavText(Stav? s) => s switch
    {
        Models.Stav.Ok => "OK", Models.Stav.Varovani => "VAROVÁNÍ", Models.Stav.Chyba => "CHYBA",
        Models.Stav.Info => "INFO", _ => ""
    };

    public static string Txt(Zprava z)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"KONZOLÁK SERVIS – {z.Nadpis}");
        sb.AppendLine($"Počítač: {z.Pocitac}    Datum: {z.Cas:d. M. yyyy HH:mm}");
        sb.AppendLine($"Výsledek: [{StavText(z.Stav)}] {z.Shrnuti}");
        foreach (var s in z.Sekce)
        {
            sb.AppendLine().AppendLine(s.Nadpis.ToUpperInvariant()).AppendLine(new string('-', 60));
            if (s.Mapa != null)
            {
                sb.AppendLine("Legenda: # v pořádku, ~ pomalé, X chyba čtení, . netestováno");
                for (var i = 0; i < s.Mapa.Count; i += 50)
                    sb.AppendLine(string.Concat(s.Mapa.Skip(i).Take(50).Select(b => b.Stav switch
                    {
                        Models.Stav.Ok => '#', Models.Stav.Varovani => '~', Models.Stav.Chyba => 'X', _ => '.'
                    })));
            }
            foreach (var r in s.Radky)
            {
                var st = r.Stav is null ? "" : $"[{StavText(r.Stav)}] ";
                sb.AppendLine(r.Hodnota.Length > 0 ? $"{st}{r.Popis}: {r.Hodnota}" : $"{st}{r.Popis}");
                if (r.Detail.Length > 0)
                    foreach (var l in r.Detail.Split('\n')) sb.AppendLine("    " + l.TrimEnd());
            }
        }
        return sb.ToString();
    }

    public static string Html(Zprava z)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        static string Barva(Stav? s) => s switch
        {
            Models.Stav.Ok => "#22C55E", Models.Stav.Varovani => "#F59E0B",
            Models.Stav.Chyba => "#EF4444", Models.Stav.Info => "#8C9AB3", _ => "transparent"
        };
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html><html lang="cs"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{E(z.Nadpis)}} – Konzolák Servis</title>
            <style>
            body{margin:0;font-family:"Segoe UI",Arial,sans-serif;background:#0F2038;color:#F1F3F7}
            .wrap{max-width:900px;margin:0 auto;padding:28px 20px}
            header{display:flex;align-items:center;gap:20px;border-bottom:2px solid #C8992E;padding-bottom:16px}
            header img{height:70px}
            h1{margin:0;font-size:26px} .meta{color:#8C9AB3;font-size:14px}
            .sum{margin:20px 0;padding:14px 18px;border-radius:8px;background:#0B1A30;border-left:6px solid {{Barva(z.Stav)}};font-size:17px}
            h2{color:#C8992E;font-size:18px;margin:26px 0 8px}
            table{width:100%;border-collapse:collapse;background:#0B1A30;border-radius:8px;overflow:hidden}
            td{padding:9px 12px;border-bottom:1px solid #1c2f4d;vertical-align:top}
            td.d{width:14px} .dot{display:inline-block;width:10px;height:10px;border-radius:50%;margin-top:5px}
            td.p{width:38%;font-weight:600} .det{color:#8C9AB3;font-size:13px;white-space:pre-wrap;margin-top:4px}
            footer{margin-top:30px;color:#8C9AB3;font-size:12px;text-align:center}
            @media print{body{background:#fff;color:#000}table,.sum{background:#f4f4f4}td{border-color:#ddd} }
            </style></head><body><div class="wrap">
            <header>{{Logo()}}<div><h1>{{E(z.Nadpis)}}</h1>
            <div class="meta">Počítač {{E(z.Pocitac)}} · {{z.Cas:d. M. yyyy HH:mm}}</div></div></header>
            <div class="sum">{{E(z.Shrnuti)}}</div>
            """);
        foreach (var s in z.Sekce)
        {
            sb.Append($"<h2>{E(s.Nadpis)}</h2>");
            if (s.Popis.Length > 0) sb.Append($"<div class=\"det\" style=\"margin-bottom:8px\">{E(s.Popis)}</div>");
            if (s.Mapa != null)
            {
                sb.Append("<div style=\"background:#0B1A30;border-radius:8px;padding:10px;margin-bottom:8px;line-height:0\">");
                foreach (var b in s.Mapa)
                    sb.Append($"<span title=\"{E(b.Popis)}\" style=\"display:inline-block;width:12px;height:12px;margin:1px;border-radius:2px;"
                        + $"background:{Barva(b.Stav)};opacity:{(b.Stav == Models.Stav.Info ? "0.35" : "1")}\"></span>");
                sb.Append("</div>");
            }
            sb.Append("<table>");
            foreach (var r in s.Radky)
            {
                var det = r.Detail.Length > 0 ? $"<div class=\"det\">{E(r.Detail)}</div>" : "";
                sb.Append($"<tr><td class=\"d\"><span class=\"dot\" style=\"background:{Barva(r.Stav)}\"></span></td>");
                if (r.Hodnota.Length > 0)
                    sb.Append($"<td class=\"p\">{E(r.Popis)}{det}</td><td>{E(r.Hodnota)}{PruhHtml(r)}</td></tr>");
                else
                    sb.Append($"<td colspan=\"2\">{E(r.Popis)}{det}</td></tr>");
            }
            sb.Append("</table>");
        }
        sb.Append("<footer>Vytvořeno aplikací Konzolák Servis</footer></div></body></html>");
        return sb.ToString();
    }

    private static string PruhHtml(Radek r) => r.Pruh is not { } p ? "" :
        $"<div style=\"background:#0F2038;border-radius:4px;height:8px;margin-top:6px;max-width:420px\"><div style=\"height:8px;border-radius:4px;width:{p * 100:0.#}%;background:"
        + (r.Stav switch { Models.Stav.Ok => "#22C55E", Models.Stav.Varovani => "#F59E0B", Models.Stav.Chyba => "#EF4444", _ => "#C8992E" })
        + "\"></div></div>";

    private static string Logo()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/logo.png"));
            if (res == null) return "";
            using var ms = new MemoryStream();
            res.Stream.CopyTo(ms);
            return $"<img alt=\"Konzolák\" src=\"data:image/png;base64,{Convert.ToBase64String(ms.ToArray())}\">";
        }
        catch { return ""; }
    }
}
