using System.Management;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>
/// Dočasné vypnutí ochranných programů Windows kvůli instalaci softwaru, který ochrany blokují.
/// Jedním tlačítkem vypne ochranu Defenderu v reálném čase, firewall i SmartScreen a stejně jednoduše vše zase zapne.
/// Vyžaduje práva správce (řeší app.manifest).
///
/// DŮLEŽITÉ: vypnutí ochrany v reálném čase ve Windows KASKÁDOVĚ vypne i cloudovou ochranu (MAPSReporting),
/// automatické odesílání vzorků (SubmitSamplesConsent) a „blokování při prvním výskytu" (BlockAtFirstSeen).
/// Opětovné zapnutí realtime je samo NEvrátí – proto je „Znovu zapnout všechny ochrany" napsané deklarativně:
/// nastaví VŠECHNA nastavení na jejich ochranný (výchozí) stav, ne jen to, co „Vypnout" zrovna změnilo.
/// </summary>
public static class OchranyService
{
    private static ManagementBaseObject? Wmi(string scope, string dotaz)
    {
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(dotaz));
            foreach (var o in s.Get()) return o;
        }
        catch { /* třída na tomto PC neexistuje */ }
        return null;
    }

    private static IEnumerable<ManagementBaseObject> WmiVse(string scope, string dotaz)
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(dotaz));
            foreach (var o in s.Get()) list.Add(o);
        }
        catch { }
        return list;
    }

    private const string DefenderScope = @"root\Microsoft\Windows\Defender";

    // --- SmartScreen / reputační registry (karta „Řízení aplikací a prohlížečů") ---
    private const string ExplorerKlic = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer"; // SmartScreen pro aplikace
    // Toggle „blokování potenciálně nežádoucích aplikací" v Ochraně na základě reputace – řídí SmartScreen, NE Set-MpPreference.
    private const string DefenderKlic = @"SOFTWARE\Microsoft\Windows Defender";
    private const string PuaHodnota = "SmartScreenPuaEnabled";
    // SmartScreen pro aplikace z Microsoft Store (per-uživatel).
    private const string StoreKlic = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppHost";
    private const string StoreHodnota = "EnableWebContentEvaluation";

    // ---------- čtečky stavu ----------

    private static bool? DefenderZapnuty()
    {
        var mp = Wmi(DefenderScope, "SELECT RealTimeProtectionEnabled FROM MSFT_MpComputerStatus");
        return mp?["RealTimeProtectionEnabled"] is bool b ? b : (bool?)null;
    }

    private static bool TamperOchrana()
    {
        var mp = Wmi(DefenderScope, "SELECT IsTamperProtected FROM MSFT_MpComputerStatus");
        return mp?["IsTamperProtected"] is true;
    }

    /// <summary>Hodnota z MSFT_MpPreference (null = Defender/vlastnost není). Vrací se jako long kvůli byte/uint.</summary>
    private static long? MpPref(string vlastnost)
    {
        var p = Wmi(DefenderScope, $"SELECT {vlastnost} FROM MSFT_MpPreference");
        var v = p?[vlastnost];
        if (v == null) return null;
        try { return Convert.ToInt64(v); } catch { return null; }
    }

    /// <summary>Cloudová ochrana (MAPSReporting): 0 = vypnuto, >0 zapnuto (1 Basic / 2 Advanced).</summary>
    private static bool? CloudZapnuta()
    {
        var v = MpPref("MAPSReporting");
        return v == null ? (bool?)null : v != 0;
    }

    /// <summary>Automatické odesílání vzorků (SubmitSamplesConsent): zapnuto = 1 (bezpečné) nebo 3 (vše).</summary>
    private static bool? VzorkyZapnute()
    {
        var v = MpPref("SubmitSamplesConsent");
        return v == null ? (bool?)null : v is 1 or 3;
    }

    private static bool FirewallProfilZapnuty(string profil) =>
        ZalohaService.CtiReg("HKLM",
            $@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{profil}", "EnableFirewall") != "0";

    private static bool SmartScreenZapnuty()
    {
        var v = ZalohaService.CtiReg("HKLM", ExplorerKlic, "SmartScreenEnabled");
        return !string.Equals(v, "Off", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Blokování potenciálně nežádoucích aplikací (PUA) v Ochraně na základě reputace. (null = hodnota není nastavená)</summary>
    private static bool? PuaZapnuty()
    {
        var v = ZalohaService.CtiReg("HKLM", DefenderKlic, PuaHodnota);
        return v == null ? (bool?)null : v != "0";
    }

    // ---------- Hromadné akce ----------

    private static async Task<string> VypnoutVse(IProgress<string> l, CancellationToken c)
    {
        var vysledky = new List<string>();

        // Ochrana v reálném čase + navázané skenování. (Tím Windows kaskádově vypne i cloud, vzorky a block-at-first-seen.)
        await ProcessRunner.PowerShellAsync(
            "Set-MpPreference -DisableRealtimeMonitoring $true -DisableIOAVProtection $true " +
            "-DisableBehaviorMonitoring $true -DisableScriptScanning $true -PUAProtection Disabled -ErrorAction SilentlyContinue",
            l, c);
        if (DefenderZapnuty() == false) vysledky.Add("ochrana Defenderu v reálném čase a blokování nežádoucích aplikací (PUA) vypnuto");
        else if (TamperOchrana())
            vysledky.Add("Defender NELZE vypnout – je zapnutá Ochrana proti neoprávněným změnám (Tamper Protection). " +
                         "Vypněte ji ručně v Zabezpečení Windows → Ochrana před viry a hrozbami → Spravovat nastavení.");

        await ProcessRunner.RunAsync("netsh.exe", "advfirewall set allprofiles state off", l, c);
        vysledky.Add("firewall vypnut ve všech profilech");

        await ProcessRunner.PowerShellAsync(
            "New-ItemProperty -Path 'HKLM:\\" + ExplorerKlic +
            "' -Name SmartScreenEnabled -Value 'Off' -PropertyType String -Force | Out-Null", l, c);
        vysledky.Add("SmartScreen pro aplikace vypnut");

        // Toggle „blokování nežádoucích aplikací" v Ochraně na základě reputace (SmartScreen, ne MpPreference).
        await ProcessRunner.PowerShellAsync(
            "New-ItemProperty -Path 'HKLM:\\" + DefenderKlic +
            "' -Name " + PuaHodnota + " -Value 0 -PropertyType DWord -Force | Out-Null", l, c);
        vysledky.Add("ochrana na základě reputace (blokování nežádoucích aplikací) vypnuta");

        var jine = CizíAntiviry();
        if (jine.Count > 0)
            vysledky.Add($"POZOR: jiný antivirus ({string.Join(", ", jine)}) vypněte ručně v jeho okně (ikona u hodin → pozastavit ochranu)");

        return "Ochrany vypnuty: " + string.Join("; ", vysledky) +
               ". Spolu s ochranou v reálném čase Windows dočasně vypne i cloudovou ochranu a odesílání vzorků – " +
               "tlačítko „Znovu zapnout všechny ochrany“ je všechny vrátí zpět.";
    }

    private static async Task<string> ZapnoutVse(IProgress<string> l, CancellationToken c)
    {
        // Deklarativně obnovíme CELÝ ochranný stav – ne jen to, co „Vypnout" změnilo.
        // 1) Nejdřív zpět ochrana v reálném čase (musí běžet, aby šlo zapnout cloud/vzorky).
        await ProcessRunner.PowerShellAsync(
            "Set-MpPreference -DisableRealtimeMonitoring $false -DisableIOAVProtection $false " +
            "-DisableBehaviorMonitoring $false -DisableScriptScanning $false -PUAProtection Enabled -ErrorAction SilentlyContinue",
            l, c);
        // 2) Cloudová ochrana (Advanced), automatické odesílání vzorků (bezpečné) a blokování při prvním výskytu.
        await ProcessRunner.PowerShellAsync(
            "Set-MpPreference -MAPSReporting Advanced -SubmitSamplesConsent SendSafeSamples " +
            "-DisableBlockAtFirstSeen $false -ErrorAction SilentlyContinue",
            l, c);

        // 3) Firewall ve všech profilech.
        await ProcessRunner.RunAsync("netsh.exe", "advfirewall set allprofiles state on", l, c);

        // 4) SmartScreen pro aplikace.
        await ProcessRunner.PowerShellAsync(
            "New-ItemProperty -Path 'HKLM:\\" + ExplorerKlic +
            "' -Name SmartScreenEnabled -Value 'Warn' -PropertyType String -Force | Out-Null", l, c);
        // 5) Reputační blokování nežádoucích aplikací (PUA).
        await ProcessRunner.PowerShellAsync(
            "New-ItemProperty -Path 'HKLM:\\" + DefenderKlic +
            "' -Name " + PuaHodnota + " -Value 1 -PropertyType DWord -Force | Out-Null", l, c);
        // 6) SmartScreen pro aplikace z Microsoft Store (per-uživatel).
        await ProcessRunner.PowerShellAsync(
            "New-ItemProperty -Path 'HKCU:\\" + StoreKlic +
            "' -Name " + StoreHodnota + " -Value 1 -PropertyType DWord -Force | Out-Null", l, c);

        return "Ochrany zase zapnuty: ochrana v reálném čase, cloudová ochrana, automatické odesílání vzorků, " +
               "blokování při prvním výskytu, blokování nežádoucích aplikací (PUA), firewall ve všech profilech, " +
               "SmartScreen pro aplikace i pro aplikace z Microsoft Store.";
    }

    private static List<string> CizíAntiviry() =>
        WmiVse(@"root\SecurityCenter2", "SELECT displayName FROM AntiVirusProduct")
            .Select(o => $"{o["displayName"]}")
            .Where(n => !n.Contains("Defender", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .ToList();

    // ---------- Zpráva ----------

    public static Task<Zprava> ZpravaAsync(IProgress<string> log, CancellationToken ct) => Task.Run(() =>
    {
        var z = new Zprava("Dočasné vypnutí ochran") { Obnovit = ZpravaAsync };

        z.HromadneAkce.Add(new AkceRadku("Vypnout všechny ochrany", VypnoutVse)
        {
            Potvrzeni = "Tím dočasně VYPNETE ochranu počítače (Defender v reálném čase, cloudová ochrana, firewall i SmartScreen).\n\n" +
                        "Používejte jen na krátkou dobu kvůli instalaci důvěryhodného programu a hned poté ochrany zase zapněte. " +
                        "Mezitím stahujte a instalujte jen soubory, kterým věříte.\n\nOpravdu vypnout ochrany?"
        });
        z.HromadneAkce.Add(new AkceRadku("Znovu zapnout všechny ochrany", ZapnoutVse) { Doporucena = true });

        // Stav jednotlivých ochran
        var s = z.PridejSekci("Aktuální stav ochran");
        s.Pridej("Verze aplikace", AktualizaceService.VerzeText, Stav.Info);

        var def = DefenderZapnuty();
        if (def == null)
            s.Pridej("Windows Defender", "není na tomto PC", Stav.Info);
        else
        {
            s.Pridej("Defender – ochrana v reálném čase", def == true ? "zapnutá" : "VYPNUTÁ",
                def == true ? Stav.Ok : Stav.Varovani);
            if (def == true && TamperOchrana())
                s.Pridej("Ochrana proti neoprávněným změnám", "zapnutá", Stav.Info,
                    "Dokud je zapnutá (Tamper Protection), nejde Defender vypnout z aplikace. " +
                    "Vypněte ji ručně: Zabezpečení Windows → Ochrana před viry a hrozbami → Spravovat nastavení.");

            var cloud = CloudZapnuta();
            if (cloud != null)
                s.Pridej("Cloudová ochrana", cloud == true ? "zapnutá" : "VYPNUTÁ",
                    cloud == true ? Stav.Ok : Stav.Varovani, "MAPSReporting – ochrana z cloudu Microsoftu.");
            var vzorky = VzorkyZapnute();
            if (vzorky != null)
                s.Pridej("Automatické odesílání vzorků", vzorky == true ? "zapnuté" : "VYPNUTÉ",
                    vzorky == true ? Stav.Ok : Stav.Varovani, "SubmitSamplesConsent – odesílání podezřelých souborů Microsoftu.");
        }

        var profily = new[] { ("DomainProfile", "Doménová síť"), ("StandardProfile", "Soukromá síť"), ("PublicProfile", "Veřejná síť") };
        var fwZap = profily.Count(p => FirewallProfilZapnuty(p.Item1));
        s.Pridej("Firewall", fwZap == 0 ? "VYPNUTÝ ve všech profilech" : fwZap == 3 ? "zapnutý" : $"zapnutý v {fwZap} ze 3 profilů",
            fwZap == 0 ? Stav.Varovani : Stav.Ok);

        s.Pridej("SmartScreen pro aplikace", SmartScreenZapnuty() ? "zapnutý" : "VYPNUTÝ",
            SmartScreenZapnuty() ? Stav.Ok : Stav.Varovani);

        var pua = PuaZapnuty();
        if (pua != null)
            s.Pridej("Ochrana na základě reputace (PUA)", pua == true ? "zapnutá" : "VYPNUTÁ",
                pua == true ? Stav.Ok : Stav.Varovani, "Blokování potenciálně nežádoucích aplikací ve Windows.");

        var jine = CizíAntiviry();
        foreach (var n in jine)
            s.Pridej(n, "jiný antivirus", Stav.Info,
                "Jiný antivirus než Defender aplikace vypnout neumí. Pozastavte jeho ochranu ručně – ikonou u hodin (pravé tlačítko → pozastavit ochranu).");

        var vypnuto = def == false || fwZap == 0 || !SmartScreenZapnuty() || pua == false
                      || CloudZapnuta() == false || VzorkyZapnute() == false;
        z.Stav = vypnuto ? Stav.Varovani : Stav.Ok;
        z.Shrnuti = vypnuto
            ? "Některé ochrany jsou VYPNUTÉ. Po instalaci je znovu zapněte tlačítkem „Znovu zapnout všechny ochrany“."
            : "Všechny ochrany jsou zapnuté. Tlačítkem výše je dočasně vypnete kvůli instalaci programu.";
        return z;
    }, ct);
}
