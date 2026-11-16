using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>Problém ze sekce „Výsledky diagnostiky“ (chyba / upozornění / informace).</summary>
public sealed record Zjisteni(Stav Zavaznost, string Priznak, string Pricina, string Podrobnosti,
                              string Reseni, IReadOnlyList<string> TykaSe)
{
    /// <summary>ID pravidla ve zprávě (např. ruleFailService_1) – spojuje problém se základní kontrolou.</summary>
    public string Id { get; init; } = "";
}

/// <summary>Řádek „Základní kontroly systému“ (OS, disky, zabezpečení, služby, ovladače).</summary>
public sealed record Kontrola(Stav Stav, string Nazev, string Vysledek, string Popis)
{
    /// <summary>Odkaz na tabulku zprávy (např. TableBasicService) – podle něj víme, o jakou kontrolu jde.</summary>
    public string Odkaz { get; init; } = "";
    /// <summary>ID problémů, které kontrolu shodily.</summary>
    public IReadOnlyList<string> Priciny { get; init; } = Array.Empty<string>();
}

/// <summary>Řádek „Přehled prostředků“ (procesor, síť, disk, paměť).</summary>
public sealed record Vytizeni(Stav Stav, string Soucast, string Hodnoceni, string Procent, string Detail);

public sealed record VysledekDiagnostiky(IReadOnlyList<Zjisteni> Zjisteni, IReadOnlyList<Kontrola> Kontroly,
                                         IReadOnlyList<Vytizeni> Prostredky);

public sealed record PerfmonZprava(string SlozkaZpravy, string? HtmlCesta, VysledekDiagnostiky Vysledek)
{
    public IReadOnlyList<Zjisteni> Zjisteni => Vysledek.Zjisteni;
}

/// <summary>
/// Spustí "Diagnostiku systému" (stejné jako perfmon /report), počká na vygenerování
/// zprávy (~60 s) a vytáhne z ní varování a chyby.
/// Zprávy ukládá Windows do %SystemDrive%\PerfLogs\System\Diagnostics\&lt;PC&gt;_&lt;datum&gt;-&lt;n&gt;\.
/// </summary>
public static class PerfmonReportService
{
    private static string SlozkaZprav => Path.Combine(
        Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "PerfLogs", "System", "Diagnostics");

    public static async Task<PerfmonZprava> VytvorAsync(IProgress<string> log, CancellationToken ct)
    {
        var start = DateTime.Now.AddSeconds(-5);
        log.Report("Spouštím diagnostiku systému (perfmon /report). Trvá zhruba 60 sekund…");

        // 1) Tichá cesta přes logman (bez okna). 2) Když selže, klasické perfmon /report.
        var rc = await ProcessRunner.RunAsync("logman.exe", "start \"System\\System Diagnostics\"", log, ct);
        Process? perfmon = null;
        if (rc != 0)
        {
            log.Report("logman se nepovedl, spouštím perfmon /report.");
            perfmon = Process.Start(new ProcessStartInfo("perfmon.exe", "/report") { UseShellExecute = true });
        }

        var slozka = await PockejNaZpravuAsync(start, log, ct);
        try { if (perfmon is { HasExited: false }) perfmon.CloseMainWindow(); } catch { }

        var xml = Path.Combine(slozka, "report.xml");
        var html = Path.Combine(slozka, "report.html");
        var v = File.Exists(xml) ? Parsuj(XDocument.Load(xml))
                                 : new VysledekDiagnostiky(new List<Zjisteni>(), new List<Kontrola>(), new List<Vytizeni>());
        log.Report($"Zpráva hotová: {slozka}");
        foreach (var r in Vypis(v)) log.Report(r);
        return new PerfmonZprava(slozka, File.Exists(html) ? html : null, v);
    }

    /// <summary>Převod výsledku diagnostiky do čitelné zprávy pro aplikaci a export.</summary>
    public static Zprava DoZpravy(PerfmonZprava pz)
    {
        var v = pz.Vysledek;
        var z = new Zprava("Kompletní diagnostika systému")
        {
            // po nápravě se celá diagnostika spustí znovu a ukáže nový výsledek
            Obnovit = async (l, c) => DoZpravy(await VytvorAsync(l, c))
        };
        var chyby = v.Zjisteni.Count(x => x.Zavaznost == Stav.Chyba) + v.Kontroly.Count(k => k.Stav == Stav.Chyba);
        var varov = v.Zjisteni.Count(x => x.Zavaznost == Stav.Varovani) + v.Kontroly.Count(k => k.Stav == Stav.Varovani)
                  + v.Prostredky.Count(q => q.Stav is Stav.Varovani or Stav.Chyba);
        z.Stav = chyby > 0 ? Stav.Chyba : varov > 0 ? Stav.Varovani : Stav.Ok;
        z.Shrnuti = chyby + varov == 0 ? "Diagnostika nenašla žádné problémy."
                  : $"Diagnostika našla {chyby} {Tvar(chyby, "chybu", "chyby", "chyb")} a {varov} {Tvar(varov, "varování", "varování", "varování")}.";

        var p = z.PridejSekci("Nalezené problémy");
        foreach (var x in v.Zjisteni)
        {
            var det = new List<string>();
            if (x.Pricina.Length > 0) det.Add($"Příčina: {x.Pricina}");
            if (x.Podrobnosti.Length > 0) det.Add($"Podrobnosti: {x.Podrobnosti}");
            if (x.TykaSe.Count > 0) det.Add($"Týká se: {string.Join(", ", x.TykaSe)}");
            if (x.Reseni.Length > 0) det.Add($"Řešení: {x.Reseni}");
            // Náprava: u havarovaných služeb nabídneme restart
            var akce = x.TykaSe.Where(t => !t.Contains(':') && SluzbyService.Existuje(t))
                .Select(t => new AkceRadku($"Restartovat službu {t}", (l, c) => SluzbyService.RestartAsync(t, l, c)) { Doporucena = true })
                .ToList();
            if (akce.Count > 0)
                det.Add("Tip: Pokud službu nepotřebujete (např. MapsBroker – offline mapy), omezte ji v sekci Optimalizace → Služby Windows.");
            p.Pridej(x.Priznak, "", x.Zavaznost, string.Join("\n", det), akce.ToArray());
        }
        if (p.Radky.Count == 0) p.Pridej("Žádné problémy nenalezeny", "", Stav.Ok);

        if (v.Kontroly.Count > 0)
        {
            var k = z.PridejSekci("Základní kontroly systému");
            foreach (var x in v.Kontroly)
            {
                var (coZnamena, overit, akceKontroly) = VysvetleniKontroly(x.Odkaz);
                if (x.Stav is Stav.Ok or Stav.Info)
                {
                    k.Pridej(x.Nazev, x.Vysledek, x.Stav, $"{x.Popis}. {coZnamena}");
                    continue;
                }
                var pricinyZ = v.Zjisteni.Where(p => x.Priciny.Contains(p.Id)).ToList();
                var det = new List<string>();
                det.Add(pricinyZ.Count > 0
                    ? "Důvod: " + string.Join("; ", pricinyZ.Select(p => p.TykaSe.Count > 0 ? $"{p.Priznak} ({string.Join(", ", p.TykaSe)})" : p.Priznak))
                    : "Důvod: Windows ve zprávě konkrétní příčinu neuvedl – podívejte se do sekce Nalezené problémy výše.");
                det.Add($"Co to znamená: {coZnamena}");
                foreach (var p in pricinyZ.Where(p => p.Reseni.Length > 0)) det.Add($"Řešení: {p.Reseni}");
                det.Add($"Jak to ověřit: {overit}");
                var akce = pricinyZ.SelectMany(p => p.TykaSe).Where(t => !t.Contains(':') && SluzbyService.Existuje(t)).Distinct()
                    .Select(t => new AkceRadku($"Restartovat službu {t}", (l, c) => SluzbyService.RestartAsync(t, l, c)) { Doporucena = true })
                    .Concat(akceKontroly).ToArray();
                k.Pridej(x.Nazev, x.Vysledek, x.Stav, string.Join("\n", det), akce);
            }
        }
        if (v.Prostredky.Count > 0)
        {
            var r = z.PridejSekci("Vytížení prostředků");
            foreach (var x in v.Prostredky)
                r.Pridej(x.Soucast, x.Procent.Length > 0 ? $"{x.Hodnoceni} ({x.Procent} %)" : x.Hodnoceni, x.Stav, x.Detail);
        }
        z.PridejSekci("Zpráva Windows").Pridej("Úplná zpráva perfmon", pz.HtmlCesta ?? pz.SlozkaZpravy);
        return z;
    }

    private static AkceRadku Otevrit(string text, string exe, string args = "") =>
        new(text, (_, _) =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = true });
            return Task.FromResult($"Otevřeno: {text.Replace("Otevřít ", "")}");
        });

    /// <summary>Co základní kontrola znamená, jak ji ověřit a čím otevřít.</summary>
    private static (string CoZnamena, string Overit, AkceRadku[] Akce) VysvetleniKontroly(string odkaz) => odkaz switch
    {
        "TableBasicService" => ("Windows kontroluje, jestli některá služba nespadla nebo neskončila s chybou. Jednorázový pád nevadí, opakovaný ukazuje na problém s programem nebo ovladačem.",
            "Otevřete Služby (services.msc) a zkontrolujte stav uvedené služby; v Prohlížeči událostí → Systém hledejte chyby „Service Control Manager“ (ID 7031, 7034).",
            new[] { Otevrit("Otevřít Služby", "services.msc"), Otevrit("Otevřít Prohlížeč událostí", "eventvwr.msc") }),
        "TableBasicSMART" => ("Kontrola stavu disků podle SMART a příznaku „dirty“ (poškozený souborový systém).",
            "Spusťte Disky → Zdraví disků (SMART) a Kontrola systémového disku.",
            Array.Empty<AkceRadku>()),
        "TableBasicOperatingSystem" => ("Kontrola základních údajů o Windows (verze, nastavení systému).",
            "Spusťte Opravy → DISM a SFC, zkontrolujte Windows Update.",
            new[] { Otevrit("Otevřít Windows Update", "ms-settings:windowsupdate") }),
        "TableBasicAntiSpywareProduct" or "TableBasicAntiVirusProduct" or "TableBasicFirewallProduct" =>
            ("Kontrola Centra zabezpečení: antivirus, antispyware, firewall a UAC.",
            "Spusťte Diagnostika → Zabezpečení a licence.",
            new[] { Otevrit("Otevřít Zabezpečení Windows", "windowsdefender:") }),
        "TableController" => ("Kontrola zařízení a ovladačů přes WMI.",
            "Spusťte Diagnostika → Zařízení a ovladače, nebo otevřete Správce zařízení a hledejte vykřičníky.",
            new[] { Otevrit("Otevřít Správce zařízení", "devmgmt.msc") }),
        _ => ("", "Podívejte se na sekci Nalezené problémy výše.", Array.Empty<AkceRadku>())
    };

    private static string Tvar(int n, string jeden, string dva, string pet) =>
        n == 1 ? jeden : n is >= 2 and <= 4 ? dva : pet;

    /// <summary>Čitelný textový výpis výsledku (do konzole aplikace a do logu).</summary>
    public static IEnumerable<string> Vypis(VysledekDiagnostiky v)
    {
        yield return $"Nalezeno: {v.Zjisteni.Count(z => z.Zavaznost == Stav.Chyba)} chyb, " +
                     $"{v.Zjisteni.Count(z => z.Zavaznost == Stav.Varovani)} varování, " +
                     $"{v.Zjisteni.Count(z => z.Zavaznost == Stav.Info)} informací.";
        foreach (var z in v.Zjisteni)
        {
            yield return $"  [{Popisek(z.Zavaznost)}] {z.Priznak}";
            if (z.Pricina.Length > 0) yield return $"      Příčina: {z.Pricina}";
            if (z.Podrobnosti.Length > 0) yield return $"      Podrobnosti: {z.Podrobnosti}";
            if (z.TykaSe.Count > 0) yield return $"      Týká se: {string.Join(", ", z.TykaSe)}";
            if (z.Reseni.Length > 0) yield return $"      Řešení: {z.Reseni}";
        }
        if (v.Kontroly.Count > 0) yield return "Základní kontroly systému:";
        foreach (var k in v.Kontroly) yield return $"  [{Popisek(k.Stav)}] {k.Nazev} – {k.Vysledek}";
        if (v.Prostredky.Count > 0) yield return "Přehled prostředků:";
        foreach (var p in v.Prostredky)
            yield return $"  [{Popisek(p.Stav)}] {p.Soucast}: {p.Hodnoceni}{(p.Procent.Length > 0 ? $" ({p.Procent} %)" : "")} – {p.Detail}";
    }

    private static async Task<string> PockejNaZpravuAsync(DateTime start, IProgress<string> log, CancellationToken ct)
    {
        var limit = DateTime.Now.AddMinutes(5);
        var tick = 0;
        while (DateTime.Now < limit)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(SlozkaZprav))
            {
                var hotova = new DirectoryInfo(SlozkaZprav).GetDirectories()
                    .Where(d => d.CreationTime >= start)
                    .OrderByDescending(d => d.CreationTime)
                    .FirstOrDefault(d => File.Exists(Path.Combine(d.FullName, "report.html")));
                if (hotova != null) return hotova.FullName;
            }
            if (++tick % 3 == 0) log.Report($"…sbírám data ({tick * 5} s)");
            await Task.Delay(5000, ct);
        }
        throw new TimeoutException("Zpráva perfmon se do 5 minut nevytvořila.");
    }

    /// <summary>
    /// Parser report.xml (ověřeno na skutečné zprávě z Windows 11, česky). Struktura:
    ///   /Report/Section[@name='advice']/Table[@name='error'|'warning'|...]/Item/Data[@name='symptom'|'cause'|'details'|'resolution'|'severity']
    ///   /Report/Section[@name='advice']/Table[@name='alltests']          – základní kontroly (stav podle Data/@img: green/yellow/red)
    ///   /Report/Section[@name='advice']/Table[@name='sysHealthSummary']  – vytížení CPU/sítě/disku/paměti
    ///   Table[@warning=ID] a Data[@warning=ID]                           – čeho se problém ID týká (např. název služby)
    /// Na jazyku Windows nezávisí: řídíme se jmény prvků a atributem img, texty bereme tak, jak je Windows napsal.
    /// </summary>
    public static VysledekDiagnostiky Parsuj(XDocument doc)
    {
        var zjisteni = new List<Zjisteni>();
        var kontroly = new List<Kontrola>();
        var prostredky = new List<Vytizeni>();
        var root = doc.Root;
        if (root == null) return new(zjisteni, kontroly, prostredky);

        var texty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in root.Element("StringTable")?.Elements("String") ?? Enumerable.Empty<XElement>())
            if (e.Attribute("ID") is { } id) texty.TryAdd(id.Value, e.Value.Trim());
        string T(string? s) => s != null && texty.TryGetValue(s, out var v) ? v : s ?? "";

        static XElement? D(XElement item, string jmeno) =>
            item.Elements("Data").FirstOrDefault(d => (string?)d.Attribute("name") == jmeno);
        string H(XElement item, string jmeno) => T(D(item, jmeno)?.Value.Trim());

        var advice = root.Elements("Section").Where(s => (string?)s.Attribute("name") == "advice").ToList();
        foreach (var table in advice.Elements("Table"))
        {
            var jmeno = (string?)table.Attribute("name") ?? "";
            switch (jmeno)
            {
                case "alltests":
                    foreach (var i in table.Elements("Item"))
                    {
                        var odkaz = (string?)D(i, "alltestgroups")?.Attribute("link") ?? "";
                        kontroly.Add(new Kontrola(ZObrazku((string?)D(i, "allteststatus")?.Attribute("img"), Stav.Info),
                            H(i, "alltestgroups"), H(i, "allteststatus"), H(i, "alltestdesc"))
                            { Odkaz = odkaz, Priciny = PricinyKontroly(root, odkaz) });
                    }
                    break;

                case "sysHealthSummary":
                    foreach (var i in table.Elements("Item"))
                    {
                        var detail = H(i, "detail");
                        var pozn = (string?)D(i, "detail")?.Attribute("note");
                        prostredky.Add(new Vytizeni(ZObrazku((string?)D(i, "status")?.Attribute("img"), Stav.Info),
                            Soucast(H(i, "component")), H(i, "status"), H(i, "util"),
                            string.IsNullOrEmpty(pozn) ? detail : $"{detail} ({pozn})"));
                    }
                    break;

                default:   // error, warning, info, … – seznam problémů
                    foreach (var i in table.Elements("Item"))
                    {
                        var priznak = H(i, "symptom");
                        if (priznak.Length == 0) continue;
                        var sym = D(i, "symptom")!;
                        var sev = D(i, "severity");
                        var stav = ZObrazku((string?)sym.Attribute("img") ?? (string?)sev?.Attribute("img") ?? sev?.Value,
                                   jmeno.Contains("error") ? Stav.Chyba : jmeno.Contains("warn") ? Stav.Varovani : Stav.Info);
                        var id = (string?)sym.Attribute("message") ?? (string?)sev?.Attribute("message");
                        zjisteni.Add(new Zjisteni(stav, priznak, H(i, "cause"), H(i, "details"), H(i, "resolution"),
                            id == null ? new List<string>() : TykaSe(root, id, T)) { Id = id ?? "" });
                    }
                    break;
            }
        }
        return new(zjisteni.OrderBy(z => Poradi(z.Zavaznost)).ToList(), kontroly, prostredky);
    }

    /// <summary>
    /// Proč kontrola selhala: tabulka kontroly (odkaz) patří do tématu (topic); tabulky stejného tématu
    /// s atributem warning nesou ID problému (např. TableBasicAbnormallyStoppedServices → ruleFailService_1).
    /// </summary>
    private static List<string> PricinyKontroly(XElement root, string odkaz)
    {
        if (odkaz.Length == 0) return new();
        var tema = root.Descendants("Table").FirstOrDefault(t => (string?)t.Attribute("name") == odkaz)?.Attribute("topic")?.Value;
        if (tema == null) return new();
        return root.Descendants("Table")
            .Where(t => (string?)t.Attribute("topic") == tema && t.Attribute("warning") != null)
            .Select(t => t.Attribute("warning")!.Value).Distinct().ToList();
    }

    /// <summary>Konkrétní položky, kterých se problém týká (např. názvy havarovaných služeb).</summary>
    private static List<string> TykaSe(XElement root, string id, Func<string?, string> T)
    {
        var mimoAdvice = root.Elements("Section").Where(s => (string?)s.Attribute("name") != "advice");
        var z = mimoAdvice.Descendants("Table").Where(t => (string?)t.Attribute("warning") == id)
                    .Elements("Item").Select(i => i.Elements("Data").FirstOrDefault()?.Value.Trim())
                    .Concat(mimoAdvice.Descendants("Data").Where(d => (string?)d.Attribute("warning") == id)
                        .Select(d => $"{T((string?)d.Attribute("name"))}: {d.Value.Trim()}"));
        return z.Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct().Take(20).ToList();
    }

    /// <summary>Řazení: chyby, varování, informace, v pořádku.</summary>
    public static int Poradi(Stav s) => s switch { Stav.Chyba => 0, Stav.Varovani => 1, Stav.Info => 2, _ => 3 };

    private static Stav ZObrazku(string? img, Stav vychozi) => img?.ToLowerInvariant() switch
    {
        "green" or "ok" or "pass" => Stav.Ok,
        "yellow" or "warning" or "warn" => Stav.Varovani,
        "red" or "error" or "critical" => Stav.Chyba,
        "info" or "information" => Stav.Info,
        _ => vychozi
    };

    private static string Soucast(string s) => s switch
    {
        "SysHealthCpuComponent" => "Procesor",
        "SysHealthNetComponent" => "Síť",
        "SysHealthDiskComponent" => "Disk",
        "SysHealthMemComponent" => "Paměť",
        _ => s.Replace("SysHealth", "").Replace("Component", "")
    };

    public static string Popisek(Stav s) => s switch
    {
        Stav.Chyba => "CHYBA", Stav.Varovani => "VAROVÁNÍ", Stav.Ok => "OK", _ => "INFO"
    };
}
