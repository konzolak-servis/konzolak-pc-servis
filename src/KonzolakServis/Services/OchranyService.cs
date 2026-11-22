using System.Management;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>
/// Dočasné vypnutí ochranných programů Windows kvůli instalaci softwaru, který ochrany blokují.
/// Jedním tlačítkem vypne ochranu Defenderu v reálném čase, firewall i SmartScreen a stejně jednoduše vše zase zapne.
/// Vyžaduje práva správce (řeší app.manifest).
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

    private const string ExplorerKlic = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer";

    /// <summary>Je ochrana Defenderu v reálném čase zapnutá? (null = Defender na tomto PC není)</summary>
    private static bool? DefenderZapnuty()
    {
        var mp = Wmi(@"root\Microsoft\Windows\Defender", "SELECT RealTimeProtectionEnabled FROM MSFT_MpComputerStatus");
        return mp?["RealTimeProtectionEnabled"] is bool b ? b : (bool?)null;
    }

    private static bool TamperOchrana()
    {
        var mp = Wmi(@"root\Microsoft\Windows\Defender", "SELECT IsTamperProtected FROM MSFT_MpComputerStatus");
        return mp?["IsTamperProtected"] is true;
    }

    private static bool FirewallProfilZapnuty(string profil) =>
        ZalohaService.CtiReg("HKLM",
            $@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{profil}", "EnableFirewall") != "0";

    private static bool SmartScreenZapnuty()
    {
        var v = ZalohaService.CtiReg("HKLM", ExplorerKlic, "SmartScreenEnabled");
        return !string.Equals(v, "Off", StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Hromadné akce ----------

    private static async Task<string> VypnoutVse(IProgress<string> l, CancellationToken c)
    {
        var vysledky = new List<string>();

        await ProcessRunner.PowerShellAsync(
            "Set-MpPreference -DisableRealtimeMonitoring $true -DisableIOAVProtection $true " +
            "-DisableBehaviorMonitoring $true -DisableScriptScanning $true -ErrorAction SilentlyContinue",
            l, c);
        if (DefenderZapnuty() == false) vysledky.Add("ochrana Defenderu v reálném čase vypnuta");
        else if (TamperOchrana())
            vysledky.Add("Defender NELZE vypnout – je zapnutá Ochrana proti neoprávněným změnám (Tamper Protection). " +
                         "Vypněte ji ručně v Zabezpečení Windows → Ochrana před viry a hrozbami → Spravovat nastavení.");

        await ProcessRunner.RunAsync("netsh.exe", "advfirewall set allprofiles state off", l, c);
        vysledky.Add("firewall vypnut ve všech profilech");

        await ProcessRunner.PowerShellAsync(
            "New-ItemProperty -Path 'HKLM:\\" + ExplorerKlic +
            "' -Name SmartScreenEnabled -Value 'Off' -PropertyType String -Force | Out-Null", l, c);
        vysledky.Add("SmartScreen pro aplikace vypnut");

        var jine = CizíAntiviry();
        if (jine.Count > 0)
            vysledky.Add($"POZOR: jiný antivirus ({string.Join(", ", jine)}) vypněte ručně v jeho okně (ikona u hodin → pozastavit ochranu)");

        return "Ochrany vypnuty: " + string.Join("; ", vysledky) +
               ". Po instalaci je prosím znovu zapněte tlačítkem výše.";
    }

    private static async Task<string> ZapnoutVse(IProgress<string> l, CancellationToken c)
    {
        await ProcessRunner.PowerShellAsync(
            "Set-MpPreference -DisableRealtimeMonitoring $false -DisableIOAVProtection $false " +
            "-DisableBehaviorMonitoring $false -DisableScriptScanning $false -ErrorAction SilentlyContinue",
            l, c);
        await ProcessRunner.RunAsync("netsh.exe", "advfirewall set allprofiles state on", l, c);
        await ProcessRunner.PowerShellAsync(
            "New-ItemProperty -Path 'HKLM:\\" + ExplorerKlic +
            "' -Name SmartScreenEnabled -Value 'Warn' -PropertyType String -Force | Out-Null", l, c);
        return "Ochrany zase zapnuty: Defender v reálném čase, firewall ve všech profilech i SmartScreen.";
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
            Potvrzeni = "Tím dočasně VYPNETE ochranu počítače (Defender, firewall i SmartScreen).\n\n" +
                        "Používejte jen na krátkou dobu kvůli instalaci důvěryhodného programu a hned poté ochrany zase zapněte. " +
                        "Mezitím stahujte a instalujte jen soubory, kterým věříte.\n\nOpravdu vypnout ochrany?"
        });
        z.HromadneAkce.Add(new AkceRadku("Znovu zapnout všechny ochrany", ZapnoutVse) { Doporucena = true });

        // Stav jednotlivých ochran
        var s = z.PridejSekci("Aktuální stav ochran");

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
        }

        var profily = new[] { ("DomainProfile", "Doménová síť"), ("StandardProfile", "Soukromá síť"), ("PublicProfile", "Veřejná síť") };
        var fwZap = profily.Count(p => FirewallProfilZapnuty(p.Item1));
        s.Pridej("Firewall", fwZap == 0 ? "VYPNUTÝ ve všech profilech" : fwZap == 3 ? "zapnutý" : $"zapnutý v {fwZap} ze 3 profilů",
            fwZap == 0 ? Stav.Varovani : Stav.Ok);

        s.Pridej("SmartScreen pro aplikace", SmartScreenZapnuty() ? "zapnutý" : "VYPNUTÝ",
            SmartScreenZapnuty() ? Stav.Ok : Stav.Varovani);

        var jine = CizíAntiviry();
        foreach (var n in jine)
            s.Pridej(n, "jiný antivirus", Stav.Info,
                "Jiný antivirus než Defender aplikace vypnout neumí. Pozastavte jeho ochranu ručně – ikonou u hodin (pravé tlačítko → pozastavit ochranu).");

        var vypnuto = def == false || fwZap == 0 || !SmartScreenZapnuty();
        z.Stav = vypnuto ? Stav.Varovani : Stav.Ok;
        z.Shrnuti = vypnuto
            ? "Některé ochrany jsou VYPNUTÉ. Po instalaci je znovu zapněte tlačítkem „Znovu zapnout všechny ochrany“."
            : "Všechny ochrany jsou zapnuté. Tlačítkem výše je dočasně vypnete kvůli instalaci programu.";
        return z;
    }, ct);
}
