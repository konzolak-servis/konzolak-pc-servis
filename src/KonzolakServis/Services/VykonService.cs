using System.Text.RegularExpressions;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>Nastavení Windows pro výkon a soukromí. Každé jde použít i vrátit (původní hodnoty se zálohují).</summary>
public static class VykonService
{
    private sealed record Hodnota(string Hive, string Cesta, string Nazev, int Doporucena);

    private sealed record Uprava(string Nazev, string Sekce, string CoToJe, Doporuceni Dop, string Vliv, string Proc, Hodnota[] Hodnoty,
        string? Poznamka = null);

    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    private static readonly Uprava[] Upravy =
    {
        new("Nahrávání her na pozadí (Xbox Game DVR)", "Výkon",
            "Windows průběžně nahrává hru, aby šlo uložit posledních pár minut.", Doporuceni.Vypnout, "Střední až vysoký (FPS ve hrách)",
            "Stálé nahrávání ubírá výkon grafiky. Kdo nenahrává herní klipy, nepotřebuje ho.",
            new[] { new Hodnota("HKCU", @"System\GameConfigStore", "GameDVR_Enabled", 0),
                    new Hodnota("HKCU", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0) }),
        new("Herní režim", "Výkon",
            "Při hraní dá Windows hře přednost a odloží aktualizace na pozadí.", Doporuceni.Ponechat, "Nízký až střední (plynulost her)",
            "Na moderních Windows 10/11 má být zapnutý.",
            new[] { new Hodnota("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1) }),
        new("Vizuální efekty – nejlepší výkon", "Výkon",
            "Vypne animace, stíny a průhlednost oken.", Doporuceni.PodlePotreby, "Střední na slabých PC, na výkonných zanedbatelný",
            "Doporučeno pro starší a slabé počítače (integrovaná grafika, 4–8 GB RAM). Windows pak vypadá jednodušeji.",
            new[] { new Hodnota("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 2) },
            "Projeví se po odhlášení nebo restartu."),
        new("Průhlednost oken", "Výkon",
            "Efekt průhledného Start menu a hlavního panelu.", Doporuceni.PodlePotreby, "Nízký",
            "Na slabých PC s integrovanou grafikou ušetří trochu výkonu.",
            new[] { new Hodnota("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0) }),
        new("Rychlé spuštění (Fast Startup)", "Výkon",
            "Windows se při vypnutí jen „uspí“, aby příští start byl rychlejší.", Doporuceni.PodlePotreby, "Rychlejší start, ale možné problémy",
            "Technici ho často vypínají: způsobuje problémy s ovladači, aktualizacemi a dual-bootem a PC se nikdy plně nerestartuje. Na SSD je rozdíl v rychlosti startu malý.",
            new[] { new Hodnota("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0) }),
        new("Telemetrie na minimum", "Soukromí",
            "Omezí množství diagnostických dat odesílaných Microsoftu.", Doporuceni.Vypnout, "Nízký",
            "Méně činnosti na pozadí a lepší soukromí. Windows Update funguje dál.",
            new[] { new Hodnota("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1) }),
        new("Reklamní ID", "Soukromí",
            "Identifikátor pro cílenou reklamu v aplikacích.", Doporuceni.Vypnout, "Žádný",
            "Pro uživatele nemá žádný přínos.",
            new[] { new Hodnota("HKCU", @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0) }),
        new("Tipy, návrhy a reklamy ve Windows", "Soukromí",
            "Reklamy ve Start menu, tipy na zamykací obrazovce a automatická instalace navrhovaných aplikací.", Doporuceni.Vypnout, "Nízký",
            "Zabrání tichému instalování „doporučených“ aplikací a her a ruším upozorněním.",
            new[] { new Hodnota("HKCU", Cdm, "SystemPaneSuggestionsEnabled", 0),
                    new Hodnota("HKCU", Cdm, "SilentInstalledAppsEnabled", 0),
                    new Hodnota("HKCU", Cdm, "SoftLandingEnabled", 0),
                    new Hodnota("HKCU", Cdm, "SubscribedContent-338388Enabled", 0),
                    new Hodnota("HKCU", Cdm, "SubscribedContent-338389Enabled", 0),
                    new Hodnota("HKCU", Cdm, "SubscribedContent-353694Enabled", 0),
                    new Hodnota("HKCU", Cdm, "SubscribedContent-353696Enabled", 0) }),
        new("Historie aktivit", "Soukromí",
            "Windows si pamatuje, co jste dělali (otevřené soubory, weby), a může to odesílat.", Doporuceni.Vypnout, "Nízký",
            "Pro běžné použití není potřeba.",
            new[] { new Hodnota("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0) }),
    };

    private static bool JePouzito(Uprava u) =>
        u.Hodnoty.All(h => ZalohaService.CtiReg(h.Hive, h.Cesta, h.Nazev) == h.Doporucena.ToString());

    private static bool JeZalohovano(Uprava u) =>
        u.Hodnoty.Any(h => ZalohaService.Puvodni($"reg:{h.Hive}\\{h.Cesta}\\{h.Nazev}") != null);

    private static void Pouzij(Uprava u) { foreach (var h in u.Hodnoty) ZalohaService.NastavDword(h.Hive, h.Cesta, h.Nazev, h.Doporucena); }
    private static void Vrat(Uprava u) { foreach (var h in u.Hodnoty) ZalohaService.VratDword(h.Hive, h.Cesta, h.Nazev); }

    // ---------- plán napájení ----------

    private const string Vyvazeny = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string Vysoky = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    private static async Task<(string Guid, string Nazev)> AktivniPlanAsync(CancellationToken ct)
    {
        var radky = new List<string>();
        await ProcessRunner.RunAsync("powercfg.exe", "/getactivescheme", new Progress0(radky), ct);
        var m = radky.Select(r => Regex.Match(r, @"([0-9a-f]{8}-[0-9a-f-]{27})\s+\((.*)\)", RegexOptions.IgnoreCase)).FirstOrDefault(x => x.Success);
        return m == null ? ("", "neznámý") : (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value);
    }

    private sealed class Progress0(List<string> l) : IProgress<string> { public void Report(string v) => l.Add(v); }

    private static bool JeNotebook()
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_Battery");
            return s.Get().Count > 0;
        }
        catch { return false; }
    }

    public static async Task<Zprava> ZpravaAsync(IProgress<string> log, CancellationToken ct)
    {
        var z = new Zprava("Výkon a soukromí") { Obnovit = ZpravaAsync };
        var notebook = JeNotebook();

        // Napájení
        var (guid, nazevPlanu) = await AktivniPlanAsync(ct);
        var nap = z.PridejSekci("Napájení");
        var cilPlan = notebook ? Vyvazeny : Vysoky;
        var planOk = guid == cilPlan || (!notebook && guid != Vyvazeny && guid.Length > 0);
        var planAkce = new List<AkceRadku>();
        if (guid != Vysoky)
            planAkce.Add(new AkceRadku("Přepnout na Vysoký výkon", async (l, c) =>
            {
                ZalohaService.Zapamatuj("plan", guid);
                var rc = await ProcessRunner.RunAsync("powercfg.exe", $"/setactive {Vysoky}", l, c);
                if (rc != 0) // plán může chybět (Moderní pohotovostní režim) – zkusíme ho vytvořit
                    await ProcessRunner.RunAsync("powercfg.exe", $"/duplicatescheme {Vysoky}", l, c);
                return "Plán napájení: Vysoký výkon";
            }) { Doporucena = !notebook });
        if (guid != Vyvazeny)
            planAkce.Add(new AkceRadku("Přepnout na Vyvážený", async (l, c) =>
            {
                await ProcessRunner.RunAsync("powercfg.exe", $"/setactive {Vyvazeny}", l, c);
                return "Plán napájení: Vyvážený";
            }) { Doporucena = notebook });
        nap.Pridej("Plán napájení", nazevPlanu, planOk ? Stav.Ok : Stav.Varovani,
            "Co to je: Určuje, jak agresivně Windows šetří energii (takt procesoru, uspávání disků).\n" +
            (notebook
                ? "Doporučení: Notebook – Vyvážený. Vysoký výkon zkracuje výdrž baterie a zvyšuje teplotu; při práci na síti lze výkon přidat ikonou baterie.\n"
                : "Doporučení: Stolní PC – Vysoký výkon. Procesor reaguje rychleji, spotřeba stoupne jen mírně.\n") +
            "Vliv na výkon: Střední (odezva systému, výkon CPU)", planAkce.ToArray());

        var hib = await HibernaceAsync(ct);
        nap.Pridej("Hibernace (soubor hiberfil.sys)", hib ? "Zapnuto" : "Vypnuto", Stav.Info,
            "Co to je: Uložení obsahu paměti na disk – potřebné pro režim Hibernace a Rychlé spuštění.\n" +
            "Doporučení: Podle potřeby – vypnutí uvolní na systémovém disku místo velikosti zhruba 40 % RAM. Vhodné při nedostatku místa na disku. Vypne zároveň Rychlé spuštění.\n" +
            "Vliv na výkon: Žádný (jen místo na disku)",
            hib ? new AkceRadku("Vypnout hibernaci", async (l, c) => { await ProcessRunner.RunAsync("powercfg.exe", "/hibernate off", l, c); return "Hibernace vypnuta"; })
                  { Potvrzeni = "Vypnout hibernaci? Uvolní se místo na disku, ale nebude fungovat režim Hibernace ani Rychlé spuštění." }
                : new AkceRadku("Zapnout hibernaci", async (l, c) => { await ProcessRunner.RunAsync("powercfg.exe", "/hibernate on", l, c); return "Hibernace zapnuta"; }));

        // Registrové úpravy
        var doporucene = new List<Uprava>();
        foreach (var skupina in Upravy.GroupBy(u => u.Sekce))
        {
            var s = z.PridejSekci(skupina.Key);
            foreach (var u in skupina)
            {
                var pouzito = JePouzito(u);
                var zaloha = JeZalohovano(u);
                if (!pouzito && u.Dop is Doporuceni.Vypnout or Doporuceni.Ponechat) doporucene.Add(u);
                var akce = new List<AkceRadku>();
                if (!pouzito)
                    akce.Add(new AkceRadku(TextPouzit(u), (_, _) => { Pouzij(u); return Task.FromResult($"{u.Nazev}: použito. {u.Poznamka}".Trim()); })
                        { Doporucena = u.Dop is Doporuceni.Vypnout or Doporuceni.Ponechat });
                if (zaloha)
                    akce.Add(new AkceRadku("Vrátit původní", (_, _) => { Vrat(u); return Task.FromResult($"{u.Nazev}: vráceno na původní nastavení."); }));
                s.Pridej(u.Nazev, pouzito ? "Upraveno doporučeně" : "Výchozí Windows",
                    pouzito ? Stav.Ok : u.Dop == Doporuceni.PodlePotreby ? Stav.Info : Stav.Varovani,
                    $"Co to je: {u.CoToJe}\nDoporučení: {TextDop(u)} – {u.Proc}\nVliv na výkon: {u.Vliv}" +
                    (u.Poznamka != null ? $"\nPozn.: {u.Poznamka}" : ""), akce.ToArray());
            }
        }

        if (doporucene.Count > 0)
            z.HromadneAkce.Add(new AkceRadku($"Použít doporučené ({doporucene.Count})", (l, _) =>
            {
                foreach (var u in doporucene) { Pouzij(u); l.Report($"{u.Nazev}: použito"); }
                return Task.FromResult($"Použito {doporucene.Count} úprav. Některé se projeví po odhlášení nebo restartu.");
            })
            { Doporucena = true, Potvrzeni = "Použít tyto úpravy?\n\n" + string.Join("\n", doporucene.Select(u => "• " + u.Nazev)) +
                "\n\nPoložky „podle potřeby“ (vizuální efekty, rychlé spuštění…) se nemění. Vše jde vrátit." });

        z.Stav = doporucene.Count > 0 || !planOk ? Stav.Varovani : Stav.Ok;
        z.Shrnuti = (notebook ? "Notebook. " : "Stolní počítač. ") +
                    (doporucene.Count > 0 ? $"Doporučujeme {doporucene.Count} úprav pro výkon a soukromí." : "Doporučené úpravy jsou použité.") +
                    (planOk ? "" : " Zkontrolujte plán napájení.");
        return z;
    }

    private static string TextPouzit(Uprava u) => u.Dop == Doporuceni.Ponechat ? "Zapnout" : u.Nazev.StartsWith("Vizuální") ? "Nastavit nejlepší výkon" : "Vypnout";

    private static string TextDop(Uprava u) => u.Dop switch
    {
        Doporuceni.Ponechat => "Mít zapnuté",
        Doporuceni.Vypnout => "Vypnout",
        _ => "Podle potřeby"
    };

    private static async Task<bool> HibernaceAsync(CancellationToken ct)
    {
        var v = ZalohaService.CtiReg("HKLM", @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled");
        await Task.CompletedTask;
        return v != "0";
    }
}
