using System.Management;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>
/// Služby Windows, které lze bezpečně omezit. Měníme jen typ spuštění (Automaticky → Ručně / Zakázáno),
/// původní stav si pamatujeme a jde vrátit jedním tlačítkem.
/// </summary>
public static class SluzbyService
{
    /// <summary>Cíl: "Manual" = spustí se jen když je potřeba, "Disabled" = nikdy.</summary>
    private sealed record Def(string Sluzba, string Nazev, string CoToJe, string Cil, string Vliv, string Proc, Doporuceni Dop = Doporuceni.Vypnout);

    private static readonly Def[] Seznam =
    {
        new("DiagTrack", "Telemetrie Windows (Connected User Experiences)", "Odesílá diagnostická a uživatelská data Microsoftu.",
            "Disabled", "Střední", "Pro chod počítače není potřeba; běží neustále a zatěžuje disk i síť. Zlepší i soukromí."),
        new("dmwappushservice", "Směrování push zpráv WAP", "Pomocná služba telemetrie.",
            "Disabled", "Nízký", "Doplněk telemetrie, není potřeba."),
        new("MapsBroker", "Správce stažených map", "Aktualizuje offline mapy aplikace Mapy.",
            "Disabled", "Nízký", "Aplikaci Mapy dnes téměř nikdo nepoužívá; služba občas padá (hlásí ji i diagnostika)."),
        new("RetailDemo", "Ukázkový režim obchodu", "Režim pro vystavené počítače v obchodech.",
            "Disabled", "Nízký", "Doma ani ve firmě nemá využití."),
        new("Fax", "Fax", "Odesílání a příjem faxů.", "Disabled", "Nízký", "Bez faxmodemu nemá využití."),
        new("RemoteRegistry", "Vzdálený registr", "Umožňuje měnit registr přes síť.",
            "Disabled", "Nízký", "Bezpečnostní riziko – na běžném PC má být zakázaná."),
        new("lfsvc", "Geolokace", "Poskytuje polohu počítače aplikacím.",
            "Manual", "Nízký", "Na stolním PC zbytečná; u notebooku ji ponechte, pokud se používá Najít zařízení.", Doporuceni.PodlePotreby),
        new("XblAuthManager", "Xbox Live – ověřování", "Přihlášení k Xbox Live ve hrách.",
            "Manual", "Nízký", "Ručně = spustí se jen když ji hra potřebuje. Úplně nevypínat, pokud se hrají hry z Xbox/Game Passu.", Doporuceni.PodlePotreby),
        new("XblGameSave", "Xbox Live – ukládání her", "Synchronizace uložených her.",
            "Manual", "Nízký", "Ručně = spustí se jen při hraní.", Doporuceni.PodlePotreby),
        new("XboxNetApiSvc", "Xbox Live – síť", "Síťové funkce Xboxu.", "Manual", "Nízký", "Ručně = spustí se jen při hraní.", Doporuceni.PodlePotreby),
        new("WSearch", "Windows Search (indexování)", "Indexuje soubory pro rychlé hledání ve Start menu a Průzkumníku.",
            "Manual", "Střední", "Na pomalých počítačích s HDD výrazně zatěžuje disk. Na SSD ponechat – vyhledávání bez indexu je pomalejší.", Doporuceni.PodlePotreby),
        new("SysMain", "SysMain (Superfetch)", "Předem načítá často používané programy do paměti.",
            "Manual", "Střední", "Na starších PC s HDD a málo RAM způsobuje 100% vytížení disku – tam vypnout. Na SSD má zanedbatelný přínos i dopad.", Doporuceni.PodlePotreby),
        new("Spooler", "Zařazování tisku", "Potřebná pro tiskárny a tisk do PDF.",
            "Manual", "Nízký", "Ponechat, pokud se tiskne. Bez tiskárny ji lze nastavit na ručně.", Doporuceni.PodlePotreby),
        new("WerSvc", "Zasílání zpráv o chybách", "Posílá Microsoftu hlášení o pádech aplikací.",
            "Manual", "Nízký", "Ručně = hlášení se zpracuje jen při pádu."),
        new("PhoneSvc", "Telefonní služba", "Telefonie přes aplikaci Propojení s telefonem.", "Manual", "Nízký", "Spustí se jen když je potřeba."),
        new("WbioSrvc", "Biometrie Windows", "Čtečka otisků prstů / Windows Hello.",
            "Manual", "Nízký", "Bez čtečky otisků zbytečná; S čtečkou NEMĚNIT.", Doporuceni.PodlePotreby),
    };

    private sealed record Stav0(string Rezim, string Beh, bool Existuje);

    private static Dictionary<string, Stav0> NactiStavy()
    {
        var d = new Dictionary<string, Stav0>(StringComparer.OrdinalIgnoreCase);
        using var s = new ManagementObjectSearcher("SELECT Name, StartMode, State FROM Win32_Service");
        foreach (var o in s.Get())
            d[(string)o["Name"]] = new Stav0((string)o["StartMode"], (string)o["State"], true);
        return d;
    }

    private static string Cesky(string rezim) => rezim switch
    {
        "Auto" => "Automaticky", "Manual" => "Ručně", "Disabled" => "Zakázáno", "Boot" or "System" => "Systémová", _ => rezim
    };

    private static string Sc(string rezim) => rezim switch { "Auto" => "auto", "Disabled" => "disabled", _ => "demand" };

    private static async Task Nastav(string sluzba, string rezim, IProgress<string> log, CancellationToken ct)
    {
        await ProcessRunner.RunAsync("sc.exe", $"config \"{sluzba}\" start= {Sc(rezim)}", log, ct);
        if (rezim == "Disabled")
            await ProcessRunner.RunAsync("sc.exe", $"stop \"{sluzba}\"", log, ct);
    }

    public static async Task<string> PouzijAsync(string sluzba, string cil, string puvodni, IProgress<string> log, CancellationToken ct)
    {
        ZalohaService.Zapamatuj($"svc:{sluzba}", puvodni);
        await Nastav(sluzba, cil, log, ct);
        return $"{sluzba}: nastaveno na {Cesky(cil)}";
    }

    public static async Task<string> VratAsync(string sluzba, IProgress<string> log, CancellationToken ct)
    {
        var puv = ZalohaService.Puvodni($"svc:{sluzba}") ?? "Auto";
        await Nastav(sluzba, puv, log, ct);
        if (puv == "Auto") await ProcessRunner.RunAsync("sc.exe", $"start \"{sluzba}\"", log, ct);
        ZalohaService.Zapomen($"svc:{sluzba}");
        return $"{sluzba}: vráceno na {Cesky(puv)}";
    }

    public static async Task<string> RestartAsync(string sluzba, IProgress<string> log, CancellationToken ct)
    {
        var pred = StavSluzby(sluzba);
        if (pred.Rezim == "Disabled")
            return $"CHYBA: Služba {sluzba} je zakázaná, nejde spustit. Pokud ji potřebujete, povolte ji v Optimalizace → Služby Windows.";
        if (pred.Beh == "Running")
        {
            await ProcessRunner.RunAsync("sc.exe", $"stop \"{sluzba}\"", log, ct);
            for (var i = 0; i < 15 && StavSluzby(sluzba).Beh != "Stopped"; i++) await Task.Delay(1000, ct);
        }
        await ProcessRunner.RunAsync("sc.exe", $"start \"{sluzba}\"", log, ct);
        for (var i = 0; i < 15 && StavSluzby(sluzba).Beh != "Running"; i++)
            await Task.Delay(1000, ct);
        var po = StavSluzby(sluzba);
        if (po.Beh == "Running")
            return $"Služba {sluzba} byla restartována a běží.";
        if (po.Rezim == "Manual")
            return $"Služba {sluzba} se spustila a zase sama ukončila – to je u ní normální (spouští se jen když ji Windows potřebuje). Chyba z diagnostiky byla jednorázová, není třeba nic dalšího dělat.";
        return $"CHYBA: Služba {sluzba} se nespustila (stav: {po.Beh}). Podrobnosti v záložce Podrobný log.";
    }

    private static Stav0 StavSluzby(string sluzba)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT StartMode, State FROM Win32_Service WHERE Name='{sluzba.Replace("'", "")}'");
            foreach (var o in s.Get()) return new Stav0((string)o["StartMode"], (string)o["State"], true);
        }
        catch { }
        return new Stav0("", "neznámý", false);
    }

    public static bool Existuje(string sluzba)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT Name FROM Win32_Service WHERE Name='{sluzba.Replace("'", "")}'");
            return s.Get().Count > 0;
        }
        catch { return false; }
    }

    public static Task<Zprava> ZpravaAsync(IProgress<string> log, CancellationToken ct) => Task.Run(() =>
    {
        var stavy = NactiStavy();
        var z = new Zprava("Služby Windows") { Obnovit = ZpravaAsync };
        var s = z.PridejSekci("Služby, které lze omezit");
        s.Popis = "„Ručně“ znamená, že se služba spustí jen ve chvíli, kdy ji něco potřebuje. Každou změnu lze vrátit tlačítkem „Vrátit původní“.";
        var kZmene = new List<(Def D, string Puv)>();

        foreach (var d in Seznam)
        {
            if (!stavy.TryGetValue(d.Sluzba, out var st)) continue;
            var upraveno = ZalohaService.Puvodni($"svc:{d.Sluzba}") != null;
            var splneno = st.Rezim == d.Cil || (d.Cil == "Manual" && st.Rezim == "Disabled");
            var stav = splneno ? Stav.Ok : d.Dop == Doporuceni.Vypnout ? Stav.Varovani : Stav.Info;
            if (!splneno && d.Dop == Doporuceni.Vypnout) kZmene.Add((d, st.Rezim));

            var akce = new List<AkceRadku>();
            if (!splneno)
                akce.Add(new AkceRadku(d.Cil == "Disabled" ? "Zakázat" : "Nastavit na Ručně",
                    (l, c) => PouzijAsync(d.Sluzba, d.Cil, st.Rezim, l, c)) { Doporucena = d.Dop == Doporuceni.Vypnout });
            if (upraveno)
                akce.Add(new AkceRadku("Vrátit původní", (l, c) => VratAsync(d.Sluzba, l, c)));

            s.Pridej($"{d.Nazev}", $"{Cesky(st.Rezim)}, {(st.Beh == "Running" ? "běží" : "neběží")}", stav,
                $"Co to je: {d.CoToJe}\n" +
                $"Doporučení: {(d.Dop == Doporuceni.Vypnout ? "" : "Podle potřeby – ")}{Cesky(d.Cil)}. {d.Proc}\n" +
                $"Vliv na výkon: {d.Vliv}\n" +
                $"Název služby: {d.Sluzba}" + (upraveno ? $" · původně: {Cesky(ZalohaService.Puvodni($"svc:{d.Sluzba}")!)}" : ""),
                akce.ToArray());
        }

        if (kZmene.Count > 0)
            z.HromadneAkce.Add(new AkceRadku($"Použít doporučené ({kZmene.Count})", async (l, c) =>
            {
                foreach (var (d, puv) in kZmene) await PouzijAsync(d.Sluzba, d.Cil, puv, l, c);
                return $"Upraveno {kZmene.Count} služeb.";
            })
            { Doporucena = true, Potvrzeni = "Nastavit doporučený režim u těchto služeb?\n\n" +
                string.Join("\n", kZmene.Select(k => $"• {k.D.Nazev} → {Cesky(k.D.Cil)}")) +
                "\n\nPoložky „podle potřeby“ se nemění – posuďte je jednotlivě." });

        z.Stav = kZmene.Count > 0 ? Stav.Varovani : Stav.Ok;
        z.Shrnuti = kZmene.Count > 0
            ? $"{kZmene.Count} služeb běží zbytečně – doporučujeme je omezit. Ostatní posuďte podle toho, jak zákazník počítač používá."
            : "Zbytečné služby jsou už omezené.";
        return z;
    }, ct);
}
