using System.Management;
using System.Reflection;
using System.Security.Principal;
using KonzolakServis.Models;
using Microsoft.Win32;

namespace KonzolakServis.Services;

/// <summary>
/// Dočasné vypnutí ochranných programů Windows kvůli instalaci softwaru, který ochrany blokují.
/// Jedním tlačítkem vypne ochranu Defenderu v reálném čase, cloud, firewall i SmartScreen a stejně jednoduše vše zase zapne.
/// Vyžaduje práva správce (řeší app.manifest).
///
/// DŮLEŽITÉ (0.20): všechno se dělá PŘÍMO V PROCESU přes .NET – NESPOUŠTÍ se powershell.exe ani netsh.exe.
/// Na některých PC totiž spuštění powershell.exe končí „Přístup byl odepřen" (blokace vytváření procesů –
/// ASR pravidlo, zásady spouštění apod.), i když je aplikace správce a WMI/registr fungují. Proto:
///   - nastavení Defenderu  → WMI třída MSFT_MpPreference, metoda Set (totéž co Set-MpPreference, ale in-process),
///   - firewall             → COM rozhraní HNetCfg.FwPolicy2 (náhradně zápis do registru),
///   - SmartScreen/reputace  → přímý zápis do registru.
///
/// Vypnutí ochrany v reálném čase ve Windows KASKÁDOVĚ vypne i cloudovou ochranu (MAPSReporting),
/// automatické odesílání vzorků (SubmitSamplesConsent) a „blokování při prvním výskytu" (BlockAtFirstSeen).
/// Opětovné zapnutí realtime je samo NEvrátí – proto „Znovu zapnout" nastaví celý ochranný stav najednou.
/// </summary>
public static class OchranyService
{
    private const string DefenderScope = @"root\Microsoft\Windows\Defender";

    // --- SmartScreen / reputační registry (karta „Řízení aplikací a prohlížečů") ---
    private const string ExplorerKlic = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer"; // SmartScreen pro aplikace
    private const string DefenderKlic = @"SOFTWARE\Microsoft\Windows Defender";
    private const string PuaHodnota = "SmartScreenPuaEnabled";
    private const string StoreKlic = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppHost";
    private const string StoreHodnota = "EnableWebContentEvaluation";
    private const string FirewallKlic = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";

    // ---------- pomocné (WMI / registr / COM, vše in-process) ----------

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

    /// <summary>Běží aplikace jako správce? Bez toho nejde nic z toho změnit.</summary>
    private static bool JsemSpravce()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Nastaví volby Defenderu přes WMI MSFT_MpPreference.Set (jako Set-MpPreference, ale bez powershellu).</summary>
    private static void SetMp(Action<ManagementBaseObject> nastav)
    {
        var scope = new ManagementScope(@"\\.\" + DefenderScope);
        scope.Connect();
        using var mc = new ManagementClass(scope, new ManagementPath("MSFT_MpPreference"), null);
        using var inParams = mc.GetMethodParameters("Set");
        nastav(inParams);
        mc.InvokeMethod("Set", inParams, null);
    }

    private static void RegStr(string hive, string cesta, string nazev, string hodnota)
    {
        using var k = ZalohaService.Koren(hive).CreateSubKey(cesta, true);
        k.SetValue(nazev, hodnota, RegistryValueKind.String);
    }

    private static void RegDword(string hive, string cesta, string nazev, int hodnota)
    {
        using var k = ZalohaService.Koren(hive).CreateSubKey(cesta, true);
        k.SetValue(nazev, hodnota, RegistryValueKind.DWord);
    }

    /// <summary>Zapne/vypne firewall ve všech profilech. Primárně COM (okamžitě platí), náhradně registr.</summary>
    private static void FirewallVse(bool zapnout)
    {
        const int NetFwProfile2All = 2147483647;
        try
        {
            var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (t != null)
            {
                var fw = Activator.CreateInstance(t);
                t.InvokeMember("FirewallEnabled", BindingFlags.SetProperty, null, fw,
                    new object[] { NetFwProfile2All, zapnout });
                return;
            }
        }
        catch { /* spadneme do náhradního zápisu do registru */ }

        foreach (var profil in new[] { "DomainProfile", "StandardProfile", "PublicProfile" })
            RegDword("HKLM", $@"{FirewallKlic}\{profil}", "EnableFirewall", zapnout ? 1 : 0);
    }

    private static string StrucneChyba(Exception ex) =>
        ex is UnauthorizedAccessException or ManagementException
            ? "přístup odepřen (nejspíš Ochrana proti neoprávněným změnám / Tamper Protection – vypněte ji ručně v Zabezpečení Windows)"
            : ex.Message.Trim();

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

    private static long? MpPref(string vlastnost)
    {
        var p = Wmi(DefenderScope, $"SELECT {vlastnost} FROM MSFT_MpPreference");
        var v = p?[vlastnost];
        if (v == null) return null;
        try { return Convert.ToInt64(v); } catch { return null; }
    }

    private static bool? CloudZapnuta()
    {
        var v = MpPref("MAPSReporting");
        return v == null ? (bool?)null : v != 0;
    }

    private static bool? VzorkyZapnute()
    {
        var v = MpPref("SubmitSamplesConsent");
        return v == null ? (bool?)null : v is 1 or 3;
    }

    private static bool FirewallProfilZapnuty(string profil) =>
        ZalohaService.CtiReg("HKLM", $@"{FirewallKlic}\{profil}", "EnableFirewall") != "0";

    private static bool SmartScreenZapnuty()
    {
        var v = ZalohaService.CtiReg("HKLM", ExplorerKlic, "SmartScreenEnabled");
        return !string.Equals(v, "Off", StringComparison.OrdinalIgnoreCase);
    }

    private static bool? PuaZapnuty()
    {
        var v = ZalohaService.CtiReg("HKLM", DefenderKlic, PuaHodnota);
        return v == null ? (bool?)null : v != "0";
    }

    // ---------- Hromadné akce (vše in-process, žádný externí proces) ----------

    private static Task<string> VypnoutVse(IProgress<string> l, CancellationToken c) => Task.Run(() =>
    {
        if (!JsemSpravce())
            return "Aplikace neběží jako správce, takže ochrany nejde změnit. Zavřete ji a spusťte znovu "
                 + "pravým tlačítkem → „Spustit jako správce“.";

        var ok = new List<string>();
        var chyby = new List<string>();

        try
        {
            l.Report("Vypínám ochranu Defenderu v reálném čase…");
            SetMp(p =>
            {
                p["DisableRealtimeMonitoring"] = true;
                p["DisableIOAVProtection"] = true;
                p["DisableBehaviorMonitoring"] = true;
                p["DisableScriptScanning"] = true;
                p["PUAProtection"] = (byte)0;
            });
            if (DefenderZapnuty() == false) ok.Add("Defender v reálném čase a PUA vypnuto");
            else if (TamperOchrana())
                chyby.Add("Defender NELZE vypnout – je zapnutá Ochrana proti neoprávněným změnám (Tamper Protection); "
                        + "vypněte ji ručně v Zabezpečení Windows → Ochrana před viry a hrozbami → Spravovat nastavení");
        }
        catch (Exception ex) { chyby.Add("Defender: " + StrucneChyba(ex)); }

        try { l.Report("Vypínám firewall…"); FirewallVse(false); ok.Add("firewall vypnut ve všech profilech"); }
        catch (Exception ex) { chyby.Add("firewall: " + StrucneChyba(ex)); }

        try { RegStr("HKLM", ExplorerKlic, "SmartScreenEnabled", "Off"); ok.Add("SmartScreen pro aplikace vypnut"); }
        catch (Exception ex) { chyby.Add("SmartScreen: " + StrucneChyba(ex)); }

        try { RegDword("HKLM", DefenderKlic, PuaHodnota, 0); ok.Add("reputační blokování nežádoucích aplikací (PUA) vypnuto"); }
        catch (Exception ex) { chyby.Add("reputace (PUA): " + StrucneChyba(ex)); }

        var jine = CizíAntiviry();
        if (jine.Count > 0)
            ok.Add($"POZOR: jiný antivirus ({string.Join(", ", jine)}) vypněte ručně v jeho okně (ikona u hodin → pozastavit ochranu)");

        var zprava = ok.Count > 0 ? "Vypnuto: " + string.Join("; ", ok) + "." : "";
        if (chyby.Count > 0) zprava += (zprava == "" ? "" : "\n") + "Nepodařilo se: " + string.Join("; ", chyby) + ".";
        zprava += "\nSpolu s ochranou v reálném čase Windows dočasně vypne i cloudovou ochranu a odesílání vzorků – "
                + "tlačítko „Znovu zapnout všechny ochrany“ je všechny vrátí zpět.";
        return zprava.Trim();
    }, c);

    private static Task<string> ZapnoutVse(IProgress<string> l, CancellationToken c) => Task.Run(() =>
    {
        if (!JsemSpravce())
            return "Aplikace neběží jako správce, takže ochrany nejde zapnout. Zavřete ji a spusťte znovu "
                 + "pravým tlačítkem → „Spustit jako správce“.";

        var ok = new List<string>();
        var chyby = new List<string>();

        // 1) Nejdřív zpět ochrana v reálném čase + PUA (realtime musí běžet, aby šlo zapnout cloud/vzorky).
        try
        {
            l.Report("Zapínám ochranu Defenderu v reálném čase…");
            SetMp(p =>
            {
                p["DisableRealtimeMonitoring"] = false;
                p["DisableIOAVProtection"] = false;
                p["DisableBehaviorMonitoring"] = false;
                p["DisableScriptScanning"] = false;
                p["PUAProtection"] = (byte)1;
            });
            ok.Add("Defender v reálném čase + PUA");
        }
        catch (Exception ex) { chyby.Add("Defender v reálném čase: " + StrucneChyba(ex)); }

        // 2) Cloudová ochrana (Advanced), automatické odesílání vzorků (bezpečné) a blokování při prvním výskytu.
        try
        {
            l.Report("Zapínám cloudovou ochranu a odesílání vzorků…");
            SetMp(p =>
            {
                p["MAPSReporting"] = (byte)2;          // Advanced
                p["SubmitSamplesConsent"] = (byte)1;    // SendSafeSamples
                p["DisableBlockAtFirstSeen"] = false;
            });
            ok.Add("cloudová ochrana, odesílání vzorků, blokování při prvním výskytu");
        }
        catch (Exception ex) { chyby.Add("cloud/vzorky: " + StrucneChyba(ex)); }

        try { l.Report("Zapínám firewall…"); FirewallVse(true); ok.Add("firewall ve všech profilech"); }
        catch (Exception ex) { chyby.Add("firewall: " + StrucneChyba(ex)); }

        try { RegStr("HKLM", ExplorerKlic, "SmartScreenEnabled", "Warn"); ok.Add("SmartScreen pro aplikace"); }
        catch (Exception ex) { chyby.Add("SmartScreen: " + StrucneChyba(ex)); }

        try { RegDword("HKLM", DefenderKlic, PuaHodnota, 1); ok.Add("reputační blokování (PUA)"); }
        catch (Exception ex) { chyby.Add("reputace (PUA): " + StrucneChyba(ex)); }

        try { RegDword("HKCU", StoreKlic, StoreHodnota, 1); ok.Add("SmartScreen pro aplikace z Microsoft Store"); }
        catch (Exception ex) { chyby.Add("SmartScreen Store: " + StrucneChyba(ex)); }

        var zprava = ok.Count > 0 ? "Zase zapnuto: " + string.Join(", ", ok) + "." : "";
        if (chyby.Count > 0) zprava += (zprava == "" ? "" : "\n") + "Nepodařilo se: " + string.Join("; ", chyby) + ".";
        return zprava.Trim();
    }, c);

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

        var s = z.PridejSekci("Aktuální stav ochran");
        s.Pridej("Verze aplikace", AktualizaceService.VerzeText, Stav.Info);

        if (!JsemSpravce())
            s.Pridej("Oprávnění", "aplikace neběží jako správce", Stav.Varovani,
                "Bez práv správce nejde ochrany měnit. Spusťte aplikaci pravým tlačítkem → „Spustit jako správce“.");

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

        var profily = new[] { "DomainProfile", "StandardProfile", "PublicProfile" };
        var fwZap = profily.Count(FirewallProfilZapnuty);
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
