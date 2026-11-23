using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>
/// Index spolehlivosti a pády – kritické a chybové události za posledních 7 dní.
/// Místo surového výpisu je seskupí, česky vysvětlí, řekne jak jsou vážné a co s tím,
/// a neškodný systémový šum (DCOM, aktualizační služby, CAPI2…) označí jako nevýznamný.
/// </summary>
public static class SpolehlivostService
{
    // Vypíše každou událost jako jeden řádek: Provider||Id||Level||čas||první řádek zprávy.
    private const string Skript =
        "Get-WinEvent -FilterHashtable @{LogName='System','Application';Level=1,2;StartTime=(Get-Date).AddDays(-7)} -MaxEvents 300 -ErrorAction SilentlyContinue " +
        "| ForEach-Object { '{0}||{1}||{2}||{3:yyyy-MM-dd HH:mm}||{4}' -f $_.ProviderName,$_.Id,[int]$_.Level,$_.TimeCreated,((($_.Message -split \"`n\")[0]) -replace '[\\r\\t]',' ').Trim() }";

    private enum Zav { Sum, Drobnost, Problem }
    private sealed record Druh(string Nazev, string CoToJe, Zav Zavaznost, string Rada);
    private sealed record Udalost(string Provider, int Id, int Level, string Cas, string Zprava);

    public static async Task<Zprava> ZpravaAsync(IProgress<string> log, CancellationToken ct)
    {
        log.Report("Načítám kritické a chybové události za posledních 7 dní…");
        var radky = new List<string>();
        await ProcessRunner.PowerShellAsync(Skript, new Sberac(radky, log), ct);

        var udalosti = radky
            .Select(Rozparsuj)
            .Where(u => u != null).Select(u => u!)
            .ToList();

        var z = new Zprava("Index spolehlivosti a pády") { Obnovit = ZpravaAsync };

        if (udalosti.Count == 0)
        {
            z.Stav = Stav.Ok;
            z.Shrnuti = "Za posledních 7 dní žádné kritické ani chybové události. Systém běží spolehlivě.";
            z.PridejSekci("Výsledek").Pridej("Žádné problémy nenalezeny", "", Stav.Ok,
                "Windows si za uplynulý týden nezapsal jedinou kritickou událost ani chybu aplikace. To je dobře.");
            return z;
        }

        // Seskupení podle srozumitelné kategorie (stejná příčina se sloučí).
        var skupiny = udalosti
            .GroupBy(u => Klasifikuj(u))
            .Select(g => new
            {
                Druh = g.Key,
                Pocet = g.Count(),
                Naposledy = g.Max(u => u.Cas),
                Priklad = g.OrderByDescending(u => u.Cas).First().Zprava
            })
            .ToList();

        var problemy = skupiny.Where(s => s.Druh.Zavaznost == Zav.Problem)
                              .OrderByDescending(s => s.Pocet).ToList();
        var drobnosti = skupiny.Where(s => s.Druh.Zavaznost == Zav.Drobnost)
                               .OrderByDescending(s => s.Pocet).ToList();
        var sum = skupiny.Where(s => s.Druh.Zavaznost == Zav.Sum)
                         .OrderByDescending(s => s.Pocet).ToList();

        z.Stav = problemy.Count > 0 ? Stav.Varovani : Stav.Ok;
        z.Shrnuti = problemy.Count > 0
            ? $"Za 7 dní: všimni si hlavně – {string.Join(", ", problemy.Select(p => p.Druh.Nazev))}. " +
              $"Zbytek ({drobnosti.Count + sum.Count} druhů) jsou drobnosti a běžný systémový šum, které řešit nemusíš."
            : $"Za 7 dní nic vážného – jen běžné hlášky a šum Windows ({udalosti.Count} událostí, {skupiny.Count} druhů). " +
              "Nic z toho nevyžaduje zásah, systémové služby se zotavily samy.";

        string Detail(Druh d, int pocet, string naposledy, string priklad) =>
            $"Co to je: {d.CoToJe}\n" +
            $"Jak je to vážné: {ZavText(d.Zavaznost)}\n" +
            $"Co s tím: {d.Rada}\n" +
            $"Výskyt: {pocet}× za 7 dní, naposledy {naposledy}.\n" +
            $"Z logu: {priklad}";

        if (problemy.Count > 0)
        {
            var s = z.PridejSekci("Čemu se věnovat");
            s.Popis = "Opakované nebo vážnější potíže, které stojí za pozornost.";
            foreach (var p in problemy)
                s.Pridej(p.Druh.Nazev, $"{p.Pocet}×", Stav.Varovani, Detail(p.Druh, p.Pocet, p.Naposledy, p.Priklad));
        }

        if (drobnosti.Count > 0)
        {
            var s = z.PridejSekci("Drobnosti (zřejmě nevadí)");
            s.Popis = "Ojedinělé nebo málo závažné události. Když se neopakují a vše funguje, nech být.";
            foreach (var d in drobnosti)
                s.Pridej(d.Druh.Nazev, $"{d.Pocet}×", Stav.Info, Detail(d.Druh, d.Pocet, d.Naposledy, d.Priklad));
        }

        if (sum.Count > 0)
        {
            var s = z.PridejSekci($"Běžný systémový šum – neřeš ({sum.Count})");
            s.Sbalena = true;
            s.Popis = "Hlášky, které Windows a aplikace zapisují běžně a nemají vliv na provoz.";
            foreach (var n in sum)
                s.Pridej(n.Druh.Nazev, $"{n.Pocet}×", Stav.Info, Detail(n.Druh, n.Pocet, n.Naposledy, n.Priklad));
        }

        return z;
    }

    private static Udalost? Rozparsuj(string radek)
    {
        var c = radek.Split(new[] { "||" }, StringSplitOptions.None);
        if (c.Length < 5) return null;
        int.TryParse(c[1], out var id);
        int.TryParse(c[2], out var level);
        return new Udalost(c[0].Trim(), id, level, c[3].Trim(), string.Join("||", c.Skip(4)).Trim());
    }

    private static string ZavText(Zav z) => z switch
    {
        Zav.Problem => "Může obtěžovat nebo něco nefunguje – vyplatí se to vyřešit.",
        Zav.Drobnost => "Málo závažné. Pokud se neopakuje, pravděpodobně nevadí.",
        _ => "Nevýznamné – běžná hláška systému, nic se neděje."
    };

    /// <summary>Zařadí událost do srozumitelné kategorie a určí závažnost. Pořadí = od nejkonkrétnějšího.</summary>
    private static Druh Klasifikuj(Udalost u)
    {
        var p = u.Provider.ToLowerInvariant();
        var m = u.Zprava.ToLowerInvariant();

        bool Má(params string[] kw) => kw.Any(k => p.Contains(k) || m.Contains(k));

        // --- Pády a vážné potíže ---
        if (Má("armourycrate", "armoury crate"))
            return new("Armoury Crate (ASUS) padá", "Software ASUS Armoury Crate se opakovaně ukončuje nebo se nespustí včas.",
                Zav.Problem, "Je to chyba programu ASUS, ne Windows. Aktualizuj Armoury Crate na nejnovější verzi; když ho nepotřebuješ, klidně ho odinstaluj.");

        if (Má("bugcheck", "nenadálé vypnutí", "systém se restartoval, aniž") || (p.Contains("kernel-power") && u.Id == 41))
            return new("Neočekávaný restart / modrá obrazovka", "Počítač se vypnul nebo spadl do modré obrazovky bez řádného vypnutí.",
                Zav.Problem, "Pokud se to stalo víckrát, spusť Opravy → DISM a SFC a zkontroluj ovladače a teploty. Jednorázově po výpadku proudu to nevadí.");

        if (Má("disk", " ntfs", "vadný blok", "bad block") && u.Provider.ToLowerInvariant() is "disk" or "ntfs")
            return new("Chyby disku", "Windows hlásí chyby čtení/zápisu na disku.",
                Zav.Problem, "Spusť Disky → Kvalita a zdraví disků a chkdsk. Opakované chyby mohou znamenat končící disk – zazálohuj data.");

        // --- Šum a neškodné hlášky ---
        if (Má("capi2"))
            return new("Kryptografická služba (CAPI2)", "Windows si při zálohovacím úkonu stěžuje na CAPI2 / System Writer.",
                Zav.Sum, "Kosmetická hláška Windows. Nemá vliv na provoz ani zabezpečení, nemusíš ji řešit.");

        if (Má("googleupdater", "aktualizační služba google"))
            return new("Aktualizace Google (Chrome)", "Aktualizační služba Googlu se nestihla spustit v časovém limitu.",
                Zav.Sum, "Běžné u Chromu/Googlu. Chrome se aktualizuje i tak, neškodné.");

        if (Má("distributedcom") || u.Provider.ToLowerInvariant().Contains("dcom"))
            return new("DCOM – časové limity služeb", "Některá služba se včas nezaregistrovala u systémové komponenty DCOM.",
                Zav.Sum, "Velmi častý šum Windows, typicky kolem aktualizačních a doprovodných služeb. Řešit není potřeba.");

        if (Má("msmpeng", "antivirová ochrana", "microsoft defender"))
            return new("Microsoft Defender se restartoval", "Antivirová služba Defenderu se ukončila a Windows ji hned znovu spustil.",
                Zav.Sum, "Stává se hlavně po změně ochrany v reálném čase – i po funkci „Dočasně vypnout ochrany\" v této aplikaci. Defender sám naběhl zpět a chrání dál. Není to závada.");

        // --- Drobnosti ---
        if (Má("bitlocker"))
            return new("BitLocker – čtení klíče při restartu", "Při restartu se hned nenačetl klíč šifrování disku BitLocker.",
                Zav.Drobnost, "Když počítač normálně nastartoval a nechce po tobě obnovovací klíč, nic se neděje. Jen si pro jistotu ulož obnovovací klíč BitLockeru.");

        if (Má("bthusb", "bluetooth"))
            return new("Bluetooth adaptér", "Bluetooth adaptér jednou selhal nebo se odpojil.",
                Zav.Drobnost, "Když Bluetooth funguje, přehlédni to. Při potížích zařízení odpoj a znovu připoj, případně restartuj PC.");

        if (Má("razer"))
            return new("Razer software", "Služba Razeru (myš/klávesnice) jednou selhala.",
                Zav.Drobnost, "Software třetí strany. Když periferie fungují, neřeš; jinak aktualizuj Razer Synapse.");

        if (Má("windowsupdateclient", "instalace se nezdařila"))
            return new("Windows Update – nenainstalovaná aktualizace", "Jedna aktualizace (často ovladač) se nenainstalovala.",
                Zav.Drobnost, "Windows to většinou zkusí znovu sám. Když to vadí, spusť Opravy → Oprava Windows Update a pak aktualizace znovu.");

        // --- Výchozí: neznámá ojedinělá chyba ---
        var nazev = string.IsNullOrWhiteSpace(u.Provider) ? "Chyba aplikace / systému" : u.Provider;
        return new(nazev, u.Zprava.Length > 0 ? u.Zprava : "Chybová událost bez bližšího popisu.",
            Zav.Drobnost, "Ojedinělá chyba. Když se neopakuje, pravděpodobně nevadí. Při opakování vyhledej název v detailu na webu.");
    }

    /// <summary>Posbírá řádky výstupu a zároveň je pošle do podrobného logu.</summary>
    private sealed class Sberac(List<string> cil, IProgress<string> log) : IProgress<string>
    {
        public void Report(string l)
        {
            if (l.Contains("||")) cil.Add(l);
            log.Report(l);
        }
    }
}
