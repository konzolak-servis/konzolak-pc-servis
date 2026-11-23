using System.ComponentModel;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows;
using KonzolakServis.Models;
using Microsoft.Win32;

namespace KonzolakServis.Services;

/// <summary>
/// Dočasné vypnutí ochranných programů Windows kvůli instalaci softwaru, který ochrany blokují.
/// Jedním tlačítkem vypne ochranu Defenderu v reálném čase, cloud, firewall i SmartScreen a stejně jednoduše vše zase zapne.
/// Vyžaduje práva správce (řeší app.manifest).
///
/// Všechno se dělá PŘÍMO V PROCESU přes .NET – NESPOUŠTÍ se powershell.exe ani netsh.exe (na některých PC je
/// vytváření procesů blokované). Defender → WMI MSFT_MpPreference.Set; firewall → COM HNetCfg.FwPolicy2 (náhradně
/// registr); SmartScreen/reputace → registr.
///
/// DIAGNOSTIKA (0.21): u každé ochrany se po akci ověří, jestli se hodnota opravdu zapsala (zápis → zpětné přečtení),
/// a když ne, uloží se přesná příčina (typ výjimky, kód, text, nebo „systém hodnotu vrátil zpět"). Zobrazí se v sekci
/// „Diagnostika poslední akce" a jde zkopírovat tlačítkem – ať je z jednoho screenshotu vidět skutečný důvod
/// (Tamper Protection, zásada/GPO, práva), místo hádání.
/// </summary>
public static class OchranyService
{
    private const string DefenderScope = @"root\Microsoft\Windows\Defender";
    private const string ExplorerKlic = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer";
    private const string StoreKlic = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppHost";
    private const string StoreHodnota = "EnableWebContentEvaluation";
    private const string FirewallKlic = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";

    /// <summary>Jeden výsledek pokusu o změnu ochrany – pro diagnostiku.</summary>
    private sealed record Polozka(string Nazev, bool Ok, string Detail);

    private static List<Polozka> _diag = new();
    private static string _diagAkce = "";
    private static DateTime _diagCas;

    // ---------- pomocné (WMI / registr / COM, vše in-process) ----------

    // Pojistka proti zaseknutí: f poběží na vlákně a čeká se nejvýš ms. Když WMI/COM provider neodpoví
    // (na některých PC se dotaz na Defender zasekne napořád), vrátí se záloha a pokračuje se dál –
    // zaseknuté vlákno doběhne samo na pozadí. Díky tomu okno NIKDY nevisí donekonečna.
    private static T SLimitem<T>(Func<T> f, T zaloha, int ms = 4000)
    {
        try
        {
            var t = Task.Run(f);
            return t.Wait(ms) && !t.IsFaulted ? t.Result : zaloha;
        }
        catch { return zaloha; }
    }

    private static ManagementBaseObject? Wmi(string scope, string dotaz) => SLimitem<ManagementBaseObject?>(() =>
    {
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(dotaz));
            foreach (var o in s.Get()) return o;
        }
        catch { }
        return null;
    }, null);

    private static IEnumerable<ManagementBaseObject> WmiVse(string scope, string dotaz) => SLimitem<IEnumerable<ManagementBaseObject>>(() =>
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(dotaz));
            foreach (var o in s.Get()) list.Add(o);
        }
        catch { }
        return list;
    }, new List<ManagementBaseObject>());

    private static bool JsemSpravce()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Čitelný popis výjimky i s kódem – pro diagnostiku.</summary>
    private static string Detail(Exception ex) => ex switch
    {
        ManagementException me => $"ManagementException „{me.ErrorCode}“ (0x{me.HResult:X8}): {me.Message.Trim()}",
        COMException ce => $"COMException 0x{ce.HResult:X8}: {ce.Message.Trim()}",
        Win32Exception we => $"Win32 {we.NativeErrorCode} (0x{we.HResult:X8}): {we.Message.Trim()}",
        UnauthorizedAccessException => $"Přístup odepřen (0x{ex.HResult:X8}): {ex.Message.Trim()}",
        _ => $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message.Trim()}"
    };

    private static void SetMp(Action<ManagementBaseObject> nastav)
    {
        var hotovo = Task.Run(() =>
        {
            var scope = new ManagementScope(@"\\.\" + DefenderScope);
            scope.Connect();
            using var mc = new ManagementClass(scope, new ManagementPath("MSFT_MpPreference"), null);
            using var inParams = mc.GetMethodParameters("Set");
            nastav(inParams);
            mc.InvokeMethod("Set", inParams, null);
        });
        if (!hotovo.Wait(6000))
            throw new TimeoutException("Nastavení Defenderu přes WMI neodpovědělo do 6 s – zaseknutý WMI provider nebo služba Defenderu (Antimalware Service).");
        if (hotovo.IsFaulted && hotovo.Exception?.InnerException is { } ex) throw ex;
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

    /// <summary>Firewall ve všech profilech (COM, náhradně registr). Vrátí popis použitého mechanismu; vyhodí jen úplné selhání.</summary>
    private static string FirewallVse(bool zapnout)
    {
        Exception? comEx = null;
        try
        {
            // COM s časovým limitem – ať se zápis přes firewall nemůže zaseknout napořád.
            var hotovo = Task.Run(() =>
            {
                var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                        ?? throw new InvalidOperationException("Rozhraní firewallu (HNetCfg.FwPolicy2) není dostupné.");
                var fw = Activator.CreateInstance(t);
                foreach (var prof in new[] { 1, 2, 4 }) // doména, soukromá, veřejná
                    t.InvokeMember("FirewallEnabled", BindingFlags.SetProperty, null, fw, new object[] { prof, zapnout });
            });
            if (!hotovo.Wait(5000))
                throw new TimeoutException("Rozhraní firewallu (COM) neodpovědělo do 5 s.");
            if (hotovo.IsFaulted && hotovo.Exception?.InnerException is { } fex) throw fex;
            return "COM (HNetCfg.FwPolicy2)";
        }
        catch (Exception ex) { comEx = ex; }

        foreach (var profil in new[] { "DomainProfile", "StandardProfile", "PublicProfile" })
            RegDword("HKLM", $@"{FirewallKlic}\{profil}", "EnableFirewall", zapnout ? 1 : 0);
        return comEx == null ? "registr" : $"registr (COM selhalo: {Detail(comEx)})";
    }

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

    private static int FirewallZapnutoProfilu() =>
        new[] { "DomainProfile", "StandardProfile", "PublicProfile" }
            .Count(p => ZalohaService.CtiReg("HKLM", $@"{FirewallKlic}\{p}", "EnableFirewall") != "0");

    private static bool SmartScreenZapnuty()
    {
        var v = ZalohaService.CtiReg("HKLM", ExplorerKlic, "SmartScreenEnabled");
        return !string.Equals(v, "Off", StringComparison.OrdinalIgnoreCase);
    }

    // PUA (blokování nežádoucích aplikací) se řídí přes WMI PUAProtection (0=vyp, 1=zap, 2=audit).
    // Registr HKLM\SOFTWARE\Microsoft\Windows Defender\SmartScreenPuaEnabled je zamčený ACL (zápis = 0x80070005),
    // proto ho nepoužíváme ani k zápisu, ani ke čtení.
    private static bool? PuaZapnuty()
    {
        var v = MpPref("PUAProtection");
        return v == null ? (bool?)null : v != 0;
    }

    // Zásady (GPO), které mohou držet Defender vypnutý i po zápisu přes WMI. Smažeme je, aby šel zase zapnout.
    private static void VymazDefenderZasady()
    {
        try
        {
            using var k = ZalohaService.Koren("HKLM").OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows Defender", true);
            if (k == null) return;
            foreach (var n in new[] { "DisableAntiSpyware", "DisableAntiVirus" })
                if (k.GetValue(n) != null) k.DeleteValue(n, false);
            using var rtp = k.OpenSubKey("Real-Time Protection", true);
            if (rtp != null)
                foreach (var n in new[] { "DisableRealtimeMonitoring", "DisableBehaviorMonitoring",
                                          "DisableOnAccessProtection", "DisableScanOnRealtimeEnable", "DisableIOAVProtection" })
                    if (rtp.GetValue(n) != null) rtp.DeleteValue(n, false);
        }
        catch { }
    }

    // Zapnutí Defenderu v reálném čase bývá „líné" – WMI zápis projde bez chyby, ale stav se vrátí zpět.
    // Proto nejdřív smažeme blokující zásady a pak zápis několikrát zopakujeme s krátkou prodlevou a ověřením.
    private static void ZapniDefenderRealtime()
    {
        VymazDefenderZasady();
        for (int pokus = 0; pokus < 3; pokus++)
        {
            SetMp(p =>
            {
                p["DisableRealtimeMonitoring"] = false;
                p["DisableIOAVProtection"] = false;
                p["DisableBehaviorMonitoring"] = false;
                p["DisableScriptScanning"] = false;
                p["PUAProtection"] = (byte)1;
            });
            if (DefenderZapnuty() == true) return;
            System.Threading.Thread.Sleep(500);
        }
    }

    /// <summary>Je nastavení spravované zásadou (GPO)? Pak ho aplikace nepřepíše.</summary>
    private static string? Zasada(string hive, string cesta, string nazev)
    {
        var v = ZalohaService.CtiReg(hive, cesta, nazev);
        return v == null ? null : $"{hive}\\{cesta}\\{nazev} = {v}";
    }

    // ---------- Hromadné akce (vše in-process; každý krok se ověří a zdiagnostikuje) ----------

    /// <summary>Provede pokus, ověří čtením a zapíše výsledek do diagnostiky.</summary>
    private static void Krok(List<Polozka> diag, string nazev, Action proved, Func<bool> jeVStavu, string mechanismus = "")
    {
        try
        {
            proved();
            if (jeVStavu())
                diag.Add(new Polozka(nazev, true, mechanismus == "" ? "OK" : "OK – " + mechanismus));
            else
                diag.Add(new Polozka(nazev, false,
                    "Zapsáno bez chyby, ale systém hlásí stále opačný stav – hodnotu drží nejspíš zásada (GPO) "
                    + "nebo Ochrana proti neoprávněným změnám (Tamper Protection)." + (mechanismus == "" ? "" : " Mechanismus: " + mechanismus)));
        }
        catch (Exception ex)
        {
            diag.Add(new Polozka(nazev, false, Detail(ex) + (mechanismus == "" ? "" : " | mechanismus: " + mechanismus)));
        }
    }

    private static Task<string> VypnoutVse(IProgress<string> l, CancellationToken c) => Task.Run(() =>
    {
        if (!JsemSpravce()) return NeniSpravce();
        var diag = new List<Polozka>();

        l.Report("Vypínám ochranu Defenderu v reálném čase…");
        Krok(diag, "Defender v reálném čase + PUA (vypnout)",
            () => SetMp(p =>
            {
                p["DisableRealtimeMonitoring"] = true;
                p["DisableIOAVProtection"] = true;
                p["DisableBehaviorMonitoring"] = true;
                p["DisableScriptScanning"] = true;
                p["PUAProtection"] = (byte)0;
            }),
            () => DefenderZapnuty() == false, "WMI MSFT_MpPreference.Set");

        l.Report("Vypínám firewall…");
        string mech = "";
        Krok(diag, "Firewall (vypnout)", () => mech = FirewallVse(false), () => FirewallZapnutoProfilu() == 0, "viz mechanismus");
        if (diag.Count > 0 && mech != "") diag[^1] = diag[^1] with { Detail = diag[^1].Detail.Replace("viz mechanismus", mech) };

        l.Report("Vypínám SmartScreen…");
        Krok(diag, "SmartScreen pro aplikace (vypnout)",
            () => RegStr("HKLM", ExplorerKlic, "SmartScreenEnabled", "Off"), () => !SmartScreenZapnuty(), "registr Explorer\\SmartScreenEnabled");

        // Reputační blokování PUA se nastavuje výše ve WMI kroku (PUAProtection=0); ověříme jen výsledek.
        Krok(diag, "Reputační blokování PUA (vypnout)", () => { }, () => PuaZapnuty() == false, "WMI PUAProtection");

        UlozDiag("Vypnout", diag);
        return Shrnuti(diag, true);
    }, c);

    private static Task<string> ZapnoutVse(IProgress<string> l, CancellationToken c) => Task.Run(() =>
    {
        if (!JsemSpravce()) return NeniSpravce();
        var diag = new List<Polozka>();

        l.Report("Zapínám ochranu Defenderu v reálném čase…");
        Krok(diag, "Defender v reálném čase + PUA (zapnout)",
            ZapniDefenderRealtime,
            () => DefenderZapnuty() == true, "WMI MSFT_MpPreference.Set (smazání zásad + opakování)");

        l.Report("Zapínám cloudovou ochranu…");
        Krok(diag, "Cloudová ochrana (zapnout)",
            () => SetMp(p => p["MAPSReporting"] = (byte)2), () => CloudZapnuta() == true, "WMI MSFT_MpPreference.Set");

        // Odesílání vzorků zvlášť – ať je vidět, jestli selže právě tahle hodnota.
        l.Report("Zapínám odesílání vzorků…");
        Krok(diag, "Automatické odesílání vzorků (zapnout)",
            () => SetMp(p =>
            {
                p["SubmitSamplesConsent"] = (byte)1;
                p["DisableBlockAtFirstSeen"] = false;
            }),
            () => VzorkyZapnute() == true, "WMI MSFT_MpPreference.Set (SubmitSamplesConsent=1)");

        l.Report("Zapínám firewall…");
        string mech = "";
        Krok(diag, "Firewall (zapnout)", () => mech = FirewallVse(true), () => FirewallZapnutoProfilu() == 3, "viz mechanismus");
        if (mech != "") diag[^1] = diag[^1] with { Detail = diag[^1].Detail.Replace("viz mechanismus", mech) };

        l.Report("Zapínám SmartScreen…");
        Krok(diag, "SmartScreen pro aplikace (zapnout)",
            () => RegStr("HKLM", ExplorerKlic, "SmartScreenEnabled", "Warn"), () => SmartScreenZapnuty(), "registr Explorer\\SmartScreenEnabled");

        // Reputační blokování PUA se nastavuje výše ve WMI kroku (PUAProtection=1); ověříme jen výsledek.
        Krok(diag, "Reputační blokování PUA (zapnout)", () => { }, () => PuaZapnuty() == true, "WMI PUAProtection");

        Krok(diag, "SmartScreen pro aplikace z Microsoft Store (zapnout)",
            () => RegDword("HKCU", StoreKlic, StoreHodnota, 1),
            () => ZalohaService.CtiReg("HKCU", StoreKlic, StoreHodnota) == "1", "registr EnableWebContentEvaluation");

        UlozDiag("Znovu zapnout", diag);
        return Shrnuti(diag, false);
    }, c);

    private static string NeniSpravce() =>
        "Aplikace neběží jako správce, takže ochrany nejde měnit. Zavřete ji a spusťte znovu "
        + "pravým tlačítkem → „Spustit jako správce“.";

    private static void UlozDiag(string akce, List<Polozka> diag)
    {
        _diag = diag; _diagAkce = akce; _diagCas = DateTime.Now;
    }

    private static string Shrnuti(List<Polozka> diag, bool vypnout)
    {
        var selhalo = diag.Where(p => !p.Ok).Select(p => p.Nazev).ToList();
        if (selhalo.Count == 0)
            return vypnout ? "Všechny ochrany vypnuty." : "Všechny ochrany zase zapnuty.";
        return $"Nepodařilo se u: {string.Join(", ", selhalo)}. Podrobnosti najdeš dole v „Diagnostika poslední akce“ "
             + "(a tlačítkem „Zkopírovat diagnostiku“).";
    }

    private static Task<string> ZkopirovatDiagnostiku(IProgress<string> l, CancellationToken c)
    {
        var text = DiagnostikaText();
        try
        {
            Application.Current.Dispatcher.Invoke(() => Clipboard.SetText(text));
            return Task.FromResult("Diagnostika zkopírována do schránky – vlož ji (Ctrl+V) do zprávy.");
        }
        catch (Exception ex)
        {
            return Task.FromResult("Nepodařilo se zkopírovat: " + Detail(ex));
        }
    }

    private static string DiagnostikaText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Konzolak service app – diagnostika ochran ===");
        sb.AppendLine($"Verze aplikace: {AktualizaceService.VerzeText}");
        sb.AppendLine($"Windows: {Environment.OSVersion.Version} ({ZalohaService.CtiReg("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion")})");
        sb.AppendLine($"Správce: {(JsemSpravce() ? "ano" : "NE")}");
        sb.AppendLine($"Tamper Protection: {(TamperOchrana() ? "ZAPNUTÁ (brání změnám Defenderu)" : "vypnutá")}");
        sb.AppendLine($"Defender realtime: {StavText(DefenderZapnuty())}; cloud(MAPSReporting): {MpPref("MAPSReporting")}; vzorky(SubmitSamplesConsent): {MpPref("SubmitSamplesConsent")}");
        sb.AppendLine($"Firewall zapnutý profilů: {FirewallZapnutoProfilu()}/3; SmartScreenEnabled: {ZalohaService.CtiReg("HKLM", ExplorerKlic, "SmartScreenEnabled") ?? "(nenastaveno)"}; PUA(PUAProtection): {MpPref("PUAProtection")?.ToString() ?? "(nenastaveno)"}");
        var gpoSS = Zasada("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen");
        var gpoSpynet = Zasada("HKLM", @"SOFTWARE\Policies\Microsoft\Windows Defender\Spynet", "SubmitSamplesConsent");
        if (gpoSS != null) sb.AppendLine("Zásada SmartScreen (GPO): " + gpoSS);
        if (gpoSpynet != null) sb.AppendLine("Zásada vzorky (GPO): " + gpoSpynet);
        sb.AppendLine();
        sb.AppendLine(_diagAkce == "" ? "Zatím neproběhla žádná akce." : $"Poslední akce: „{_diagAkce}“ v {_diagCas:HH:mm:ss}");
        foreach (var p in _diag)
            sb.AppendLine($"  [{(p.Ok ? "OK " : "CHYBA")}] {p.Nazev}: {p.Detail}");
        return sb.ToString();
    }

    private static string StavText(bool? b) => b == null ? "?" : b == true ? "zapnuto" : "vypnuto";

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
        if (_diag.Count > 0)
            z.HromadneAkce.Add(new AkceRadku("Zkopírovat diagnostiku", ZkopirovatDiagnostiku));

        // Všechny stavy přečteme JEDNOU a s hlášením průběhu (každé čtení má časový limit v Wmi()/MpPref()).
        log.Report("Zjišťuji oprávnění…");
        var spravce = JsemSpravce();
        log.Report("Čtu stav Defenderu…");
        var def = DefenderZapnuty();
        log.Report("Čtu Ochranu proti neoprávněným změnám…");
        var tamper = TamperOchrana();
        log.Report("Čtu cloudovou ochranu…");
        var cloud = CloudZapnuta();
        log.Report("Čtu odesílání vzorků…");
        var vzorky = VzorkyZapnute();
        log.Report("Čtu firewall…");
        var fwZap = FirewallZapnutoProfilu();
        log.Report("Čtu SmartScreen…");
        var smartScreen = SmartScreenZapnuty();
        log.Report("Čtu reputační ochranu (PUA)…");
        var pua = PuaZapnuty();
        log.Report("Hledám cizí antiviry…");
        var cizi = CizíAntiviry();
        log.Report("Sestavuji přehled…");

        var s = z.PridejSekci("Aktuální stav ochran");
        s.Pridej("Verze aplikace", AktualizaceService.VerzeText, Stav.Info);

        if (!spravce)
            s.Pridej("Oprávnění", "aplikace neběží jako správce", Stav.Varovani,
                "Bez práv správce nejde ochrany měnit. Spusťte aplikaci pravým tlačítkem → „Spustit jako správce“.");

        if (def == null)
            s.Pridej("Windows Defender", "není na tomto PC", Stav.Info);
        else
        {
            s.Pridej("Defender – ochrana v reálném čase", def == true ? "zapnutá" : "VYPNUTÁ",
                def == true ? Stav.Ok : Stav.Varovani);
            if (tamper)
                s.Pridej("Ochrana proti neoprávněným změnám", "ZAPNUTÁ", Stav.Varovani,
                    "Tamper Protection brání aplikaci měnit Defender (realtime, cloud, vzorky). Pokud chcete Defender "
                    + "vypnout, vypněte ji ručně: Zabezpečení Windows → Ochrana před viry a hrozbami → Spravovat nastavení.");

            if (cloud != null)
                s.Pridej("Cloudová ochrana", cloud == true ? "zapnutá" : "VYPNUTÁ",
                    cloud == true ? Stav.Ok : Stav.Varovani, "MAPSReporting – ochrana z cloudu Microsoftu.");
            if (vzorky != null)
                s.Pridej("Automatické odesílání vzorků", vzorky == true ? "zapnuté" : "VYPNUTÉ",
                    vzorky == true ? Stav.Ok : Stav.Varovani, "SubmitSamplesConsent – odesílání podezřelých souborů Microsoftu.");
        }

        s.Pridej("Firewall", fwZap == 0 ? "VYPNUTÝ ve všech profilech" : fwZap == 3 ? "zapnutý" : $"zapnutý v {fwZap} ze 3 profilů",
            fwZap == 0 ? Stav.Varovani : Stav.Ok);

        s.Pridej("SmartScreen pro aplikace", smartScreen ? "zapnutý" : "VYPNUTÝ",
            smartScreen ? Stav.Ok : Stav.Varovani);

        if (pua != null)
            s.Pridej("Ochrana na základě reputace (PUA)", pua == true ? "zapnutá" : "VYPNUTÁ",
                pua == true ? Stav.Ok : Stav.Varovani, "Blokování potenciálně nežádoucích aplikací ve Windows.");

        foreach (var n in cizi)
            s.Pridej(n, "jiný antivirus", Stav.Info,
                "Jiný antivirus než Defender aplikace vypnout neumí. Pozastavte jeho ochranu ručně – ikonou u hodin (pravé tlačítko → pozastavit ochranu).");

        // Diagnostika poslední akce – ať je z jednoho screenshotu vidět přesná příčina selhání.
        if (_diag.Count > 0)
        {
            var d = z.PridejSekci($"Diagnostika poslední akce ({_diagAkce}, {_diagCas:HH:mm:ss})");
            d.Popis = "Co se při poslední akci u každé ochrany skutečně stalo. Když něco selhalo, je tu přesný důvod. "
                    + "Tlačítkem „Zkopírovat diagnostiku“ nahoře to zkopírujete celé.";
            if (tamper)
                d.Pridej("Tamper Protection", "ZAPNUTÁ", Stav.Varovani,
                    "Dokud je zapnutá, žádná aplikace nezmění Defender (realtime, cloud, vzorky). Vypněte ji ručně v Zabezpečení Windows.");
            foreach (var p in _diag)
                d.Pridej(p.Nazev, p.Ok ? "OK" : "CHYBA", p.Ok ? Stav.Ok : Stav.Chyba, p.Detail);
        }

        var vypnuto = def == false || fwZap == 0 || !smartScreen || pua == false
                      || cloud == false || vzorky == false;
        z.Stav = vypnuto ? Stav.Varovani : Stav.Ok;
        z.Shrnuti = vypnuto
            ? "Některé ochrany jsou VYPNUTÉ. Po instalaci je znovu zapněte tlačítkem „Znovu zapnout všechny ochrany“."
            : "Všechny ochrany jsou zapnuté. Tlačítkem výše je dočasně vypnete kvůli instalaci programu.";
        return z;
    }, ct);
}
