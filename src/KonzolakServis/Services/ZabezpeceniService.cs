using System.Globalization;
using System.IO;
using System.Management;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>Zabezpečení a licence: antivirus, Defender, firewall, aktualizace, UAC, Secure Boot, TPM, BitLocker, aktivace Windows.</summary>
public static class ZabezpeceniService
{
    private static IEnumerable<ManagementBaseObject> Wmi(string scope, string dotaz)
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(dotaz));
            foreach (var o in s.Get()) list.Add(o);
        }
        catch { /* třída na tomto PC neexistuje */ }
        return list;
    }

    private static string MpCmd => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");

    public static Task<Zprava> ZpravaAsync(IProgress<string> log, CancellationToken ct) => Task.Run(() =>
    {
        var z = new Zprava("Zabezpečení a licence") { Obnovit = ZpravaAsync };
        var problemy = 0;
        void P(Stav? s) { if (s is Stav.Chyba or Stav.Varovani) problemy++; }

        // --- Antivirus ---
        var av = z.PridejSekci("Ochrana proti malwaru");
        var produkty = Wmi(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct").ToList();
        foreach (var o in produkty)
        {
            var st = Convert.ToInt32(o["productState"]);
            var zapnuty = ((st >> 12) & 0xF) == 1;
            var aktualni = ((st >> 4) & 0xF) == 0;
            var s = zapnuty && aktualni ? Stav.Ok : zapnuty ? Stav.Varovani : Stav.Info;
            av.Pridej($"{o["displayName"]}", (zapnuty ? "zapnutý" : "vypnutý") + (aktualni ? ", aktuální" : ", ZASTARALÉ definice"), s);
        }
        var aktivnich = produkty.Count(o => ((Convert.ToInt32(o["productState"]) >> 12) & 0xF) == 1);
        if (aktivnich == 0) { av.Pridej("Žádný aktivní antivirus", "", Stav.Chyba, "Počítač není chráněn. Zapněte Windows Defender nebo nainstalujte antivirus."); problemy++; }
        if (aktivnich > 1) { av.Pridej("Více aktivních antivirů", $"{aktivnich}", Stav.Varovani, "Dva antiviry se navzájem brzdí a mohou způsobovat pády. Ponechte jen jeden."); problemy++; }
        foreach (var r in av.Radky) if (r.Stav == Stav.Varovani && r.Popis != "Více aktivních antivirů") problemy++;

        var mp = Wmi(@"root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpComputerStatus").FirstOrDefault();
        if (mp != null && File.Exists(MpCmd))
        {
            var rt = mp["RealTimeProtectionEnabled"] is true;
            var def = mp["AntivirusSignatureLastUpdated"] is string d ? Datum(d) : "neznámo";
            var sken = mp["QuickScanEndTime"] is string q ? Datum(q) : "nikdy";
            av.Pridej("Windows Defender", rt ? "ochrana v reálném čase zapnutá" : "ochrana v reálném čase vypnutá", rt ? Stav.Ok : Stav.Info,
                $"Definice virů: {def}\nPoslední rychlá kontrola: {sken}",
                new AkceRadku("Aktualizovat definice", async (l, c) =>
                {
                    var rc = await ProcessRunner.RunAsync(MpCmd, "-SignatureUpdate", l, c);
                    return rc == 0 ? "Definice Defenderu aktualizovány." : $"Aktualizace definic selhala (kód {rc}).";
                }),
                new AkceRadku("Rychlá kontrola", async (l, c) =>
                {
                    var rc = await ProcessRunner.RunAsync(MpCmd, "-Scan -ScanType 1", l, c);
                    return rc == 0 ? "Rychlá kontrola dokončena – nic nenalezeno." : $"Kontrola skončila s kódem {rc} – podívejte se do Zabezpečení Windows.";
                }) { Doporucena = sken == "nikdy" });
        }

        // --- Firewall ---
        var fw = z.PridejSekci("Firewall");
        var vypnute = new List<string>();
        foreach (var (k, n) in new[] { ("DomainProfile", "Doménová síť"), ("StandardProfile", "Soukromá síť"), ("PublicProfile", "Veřejná síť") })
        {
            var v = ZalohaService.CtiReg("HKLM", $@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{k}", "EnableFirewall");
            var zap = v != "0";
            if (!zap) vypnute.Add(n);
            fw.Pridej(n, zap ? "zapnutý" : "VYPNUTÝ", zap ? Stav.Ok : Stav.Chyba);
        }
        if (vypnute.Count > 0)
        {
            problemy++;
            fw.Pridej("Doporučení", "", null, "Firewall má být zapnutý ve všech profilech (pokud ho nenahrazuje firewall antiviru).",
                new AkceRadku("Zapnout firewall", async (l, c) =>
                {
                    await ProcessRunner.RunAsync("netsh.exe", "advfirewall set allprofiles state on", l, c);
                    return "Firewall zapnut ve všech profilech.";
                }) { Doporucena = true });
        }

        // --- Systém ---
        var sys = z.PridejSekci("Systém");
        var hotfix = Wmi(@"root\cimv2", "SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering")
            .Select(o => DateTime.TryParse($"{o["InstalledOn"]}", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : DateTime.MinValue)
            .DefaultIfEmpty(DateTime.MinValue).Max();
        var dny = hotfix == DateTime.MinValue ? -1 : (int)(DateTime.Now - hotfix).TotalDays;
        var wuStav = dny < 0 ? Stav.Info : dny <= 45 ? Stav.Ok : dny <= 90 ? Stav.Varovani : Stav.Chyba;
        P(wuStav);
        sys.Pridej("Poslední aktualizace Windows", dny < 0 ? "neznámo" : $"{hotfix:d. M. yyyy} (před {dny} dny)", wuStav,
            "Windows by měl dostávat aktualizace každý měsíc. Starší než 3 měsíce = bezpečnostní riziko.",
            new AkceRadku("Otevřít Windows Update", (_, _) =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:windowsupdate") { UseShellExecute = true });
                return Task.FromResult("Windows Update otevřen.");
            }) { Doporucena = wuStav != Stav.Ok });

        var uac = ZalohaService.CtiReg("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA") != "0";
        if (!uac) problemy++;
        sys.Pridej("Řízení uživatelských účtů (UAC)", uac ? "zapnuto" : "VYPNUTO", uac ? Stav.Ok : Stav.Chyba,
            "UAC se ptá před změnami systému a brání malwaru získat práva správce. Vypnuté UAC je vážné riziko.");

        var sb = ZalohaService.CtiReg("HKLM", @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        sys.Pridej("Secure Boot", sb == "1" ? "zapnuto" : sb == "0" ? "vypnuto" : "nepodporováno (Legacy BIOS)",
            sb == "1" ? Stav.Ok : Stav.Info, "Chrání start systému před rootkity. Windows 11 ho vyžaduje.");

        var tpm = Wmi(@"root\CIMV2\Security\MicrosoftTpm", "SELECT * FROM Win32_Tpm").FirstOrDefault();
        sys.Pridej("TPM čip", tpm == null ? "nenalezen" : $"verze {($"{tpm["SpecVersion"]}".Split(',').FirstOrDefault() ?? "?").Trim()}",
            tpm == null ? Stav.Info : Stav.Ok, "Bezpečnostní čip potřebný pro Windows 11 a BitLocker.");

        foreach (var v in Wmi(@"root\CIMV2\Security\MicrosoftVolumeEncryption", "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume"))
        {
            var zap = Convert.ToInt32(v["ProtectionStatus"]) == 1;
            if (!zap) continue;
            sys.Pridej($"BitLocker {v["DriveLetter"]}", "šifrováno", Stav.Info,
                "POZOR při servisu: před výměnou desky, aktualizací BIOSu nebo reinstalací si nechte od zákazníka obnovovací klíč (account.microsoft.com/devices/recoverykey), jinak budou data nedostupná.");
        }

        // --- Licence ---
        var lic = z.PridejSekci("Licence Windows");
        var prod = Wmi(@"root\cimv2", "SELECT Name, Description, LicenseStatus, PartialProductKey FROM SoftwareLicensingProduct " +
                                      "WHERE PartialProductKey IS NOT NULL AND ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f'").FirstOrDefault();
        if (prod != null)
        {
            var st = Convert.ToInt32(prod["LicenseStatus"]);
            var aktiv = st == 1;
            if (!aktiv) problemy++;
            lic.Pridej("Aktivace", aktiv ? "Windows je aktivován" : Licence(st), aktiv ? Stav.Ok : Stav.Chyba,
                $"Edice: {prod["Name"]}\nTyp licence: {prod["Description"]}\nKonec klíče: …{prod["PartialProductKey"]}");
        }
        var oem = Wmi(@"root\cimv2", "SELECT OA3xOriginalProductKey FROM SoftwareLicensingService").FirstOrDefault()?["OA3xOriginalProductKey"] as string;
        lic.Pridej("OEM klíč v BIOSu", string.IsNullOrWhiteSpace(oem) ? "není (licence není vázaná na desku)" : oem!, Stav.Info,
            string.IsNullOrWhiteSpace(oem) ? "" : "Klíč od výrobce počítače uložený ve firmwaru – Windows ho při reinstalaci použije sám.");

        z.Stav = problemy > 0 ? Stav.Varovani : Stav.Ok;
        z.Shrnuti = problemy > 0 ? $"Nalezeno {problemy} problémů se zabezpečením nebo licencí." : "Zabezpečení i licence jsou v pořádku.";
        return z;
    }, ct);

    private static string Licence(int s) => s switch
    {
        0 => "NEAKTIVOVÁNO (bez licence)", 2 or 3 => "Zkušební / prodloužená lhůta", 4 => "Neplatná licence", 5 => "Oznámení – Windows není aktivován",
        6 => "Prodloužená lhůta", _ => $"Stav {s}"
    };

    private static string Datum(string wmi)
    {
        try { return ManagementDateTimeConverter.ToDateTime(wmi).ToString("d. M. yyyy HH:mm"); }
        catch { return wmi; }
    }
}
