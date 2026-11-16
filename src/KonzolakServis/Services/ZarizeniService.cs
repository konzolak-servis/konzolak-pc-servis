using System.Diagnostics;
using System.IO;
using System.Management;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>Zařízení s problémem ovladače (vykřičník ve Správci zařízení) a záloha ovladačů.</summary>
public static class ZarizeniService
{
    public static string Kod(int k) => k switch
    {
        1 => "Zařízení není správně nakonfigurované.",
        3 => "Ovladač je poškozený nebo je málo systémových prostředků.",
        10 => "Zařízení nelze spustit – obvykle chybný nebo nevhodný ovladač.",
        12 => "Nedostatek volných prostředků (konflikt).",
        14 => "Zařízení bude fungovat až po restartu.",
        18 => "Ovladač je potřeba přeinstalovat.",
        19 => "Poškozená konfigurace v registru.",
        21 => "Windows zařízení odebírá – restartujte.",
        22 => "Zařízení je zakázané.",
        24 => "Zařízení není přítomné nebo nefunguje správně.",
        28 => "Ovladač není nainstalovaný.",
        29 => "Zařízení je vypnuté ve firmwaru (BIOS/UEFI).",
        31 => "Windows nemůže načíst ovladač.",
        32 => "Služba ovladače je zakázaná.",
        37 => "Ovladač vrátil chybu při inicializaci.",
        39 => "Ovladač je poškozený nebo chybí.",
        43 => "Zařízení nahlásilo problém a bylo zastaveno (časté u grafik a USB).",
        45 => "Zařízení není připojené.",
        48 => "Ovladač je blokovaný kvůli známé nekompatibilitě.",
        52 => "Ovladač není digitálně podepsaný.",
        _ => $"Chyba zařízení (kód {k})."
    };

    public static string Rada(int k) => k switch
    {
        28 or 18 or 39 or 31 => "Stáhněte ovladač od výrobce zařízení / notebooku, nebo zkuste Windows Update → Volitelné aktualizace.",
        22 => "Pokud zařízení potřebujete, povolte ho ve Správci zařízení (pravé tlačítko → Povolit).",
        14 or 21 => "Restartujte počítač.",
        29 => "Povolte zařízení v BIOSu/UEFI.",
        43 => "Odinstalujte zařízení ve Správci zařízení a restartujte; u grafiky čistá instalace ovladače; u USB zkuste jiný port nebo kabel.",
        45 or 24 => "Zařízení je odpojené – pokud už ho nepoužíváte, lze ho ignorovat.",
        52 => "Nainstalujte podepsaný ovladač od výrobce.",
        _ => "Aktualizujte nebo přeinstalujte ovladač (Správce zařízení → Aktualizovat ovladač)."
    };

    public static Task<VysledekAkce> ZpravaAsync(IProgress<string> log, CancellationToken ct) => Task.Run(() =>
    {
        var z = new Zprava("Zařízení a ovladače");
        var s = z.PridejSekci("Zařízení s problémem");
        using (var q = new ManagementObjectSearcher("SELECT Name, PNPClass, ConfigManagerErrorCode, DeviceID FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
            foreach (var o in q.Get())
            {
                var kod = Convert.ToInt32(o["ConfigManagerErrorCode"]);
                var nazev = (o["Name"] as string) ?? "Neznámé zařízení";
                var stav = kod is 22 or 45 ? Stav.Info : Stav.Chyba;
                log.Report($"{nazev}: kód {kod}");
                s.Pridej(nazev, $"kód {kod}", stav,
                    $"Problém: {Kod(kod)}\nŘešení: {Rada(kod)}\nTřída: {o["PNPClass"]} · ID: {o["DeviceID"]}",
                    new AkceRadku("Otevřít Správce zařízení", (_, _) =>
                    {
                        Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
                        return Task.FromResult("Správce zařízení otevřen.");
                    }));
            }
        var chyby = s.Radky.Count(r => r.Stav == Stav.Chyba);
        if (s.Radky.Count == 0) s.Pridej("Všechna zařízení fungují správně.", "", Stav.Ok);
        z.Stav = chyby > 0 ? Stav.Chyba : Stav.Ok;
        z.Shrnuti = chyby > 0 ? $"{chyby} zařízení má problém s ovladačem." : "Žádné zařízení nehlásí problém s ovladačem.";
        return new VysledekAkce(z.Stav, z.Shrnuti) { Zprava = z };
    }, ct);

    public static async Task<VysledekAkce> ZalohaOvladacuAsync(IProgress<string> log, CancellationToken ct)
    {
        var cil = Path.Combine(LogService.SlozkaLogu, "..", $"Zaloha_ovladacu_{Environment.MachineName}_{DateTime.Now:yyyy-MM-dd}");
        cil = Path.GetFullPath(cil);
        Directory.CreateDirectory(cil);
        var rc = await ProcessRunner.RunAsync("pnputil.exe", $"/export-driver * \"{cil}\"", log, ct);
        var pocet = Directory.Exists(cil) ? Directory.GetDirectories(cil).Length : 0;
        var z = new Zprava("Záloha ovladačů")
        {
            Stav = rc == 0 ? Stav.Ok : Stav.Chyba,
            Shrnuti = rc == 0 ? $"Zálohováno {pocet} ovladačů." : $"Záloha selhala (kód {rc})."
        };
        z.PridejSekci("Kam se uložilo").Pridej("Složka", cil, null,
            "Obnovení po přeinstalaci Windows: Správce zařízení → Aktualizovat ovladač → Vyhledat v počítači → vybrat tuto složku (včetně podsložek).\n" +
            "Tip: zkopírujte složku na flashku nebo jiný disk.",
            new AkceRadku("Otevřít složku", (_, _) =>
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{cil}\"") { UseShellExecute = true });
                return Task.FromResult("Složka otevřena.");
            }));
        return new VysledekAkce(z.Stav, z.Shrnuti) { Zprava = z };
    }

    // ---------- Kompletní výpis zařízení (jako Správce zařízení) ----------

    private static readonly Dictionary<string, string> Tridy = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AudioEndpoint"] = "Zvukové vstupy a výstupy", ["Battery"] = "Baterie", ["Biometric"] = "Biometrická zařízení",
        ["Bluetooth"] = "Bluetooth", ["Camera"] = "Kamery", ["Computer"] = "Počítač", ["DiskDrive"] = "Diskové jednotky",
        ["Display"] = "Grafické adaptéry", ["Firmware"] = "Firmware", ["HIDClass"] = "Zařízení standardu HID",
        ["HDC"] = "Řadiče IDE ATA/ATAPI", ["Image"] = "Zobrazovací zařízení", ["Keyboard"] = "Klávesnice",
        ["MEDIA"] = "Řadiče zvuku, videa a her", ["Monitor"] = "Monitory", ["Mouse"] = "Myši a jiná polohovací zařízení",
        ["Net"] = "Síťové adaptéry", ["Ports"] = "Porty (COM a LPT)", ["PrintQueue"] = "Tiskové fronty", ["Printer"] = "Tiskárny",
        ["Processor"] = "Procesory", ["SCSIAdapter"] = "Řadiče úložiště", ["SecurityDevices"] = "Zabezpečovací zařízení",
        ["Sensor"] = "Senzory", ["SmartCardReader"] = "Čtečky čipových karet", ["SoftwareComponent"] = "Softwarové komponenty",
        ["SoftwareDevice"] = "Softwarová zařízení", ["System"] = "Systémová zařízení", ["USB"] = "Řadiče USB",
        ["USBDevice"] = "Zařízení USB", ["WPD"] = "Přenosná zařízení", ["MTD"] = "Paměťová zařízení", ["Extension"] = "Rozšíření ovladačů",
        ["UCM"] = "Správci konektorů USB", ["Media"] = "Řadiče zvuku, videa a her", ["Infrared"] = "Infračervená zařízení",
        ["Modem"] = "Modemy", ["SDHost"] = "Hostitelské adaptéry SD", ["NetService"] = "Síťové služby",
    };

    private static readonly HashSet<string> Skryte = new(StringComparer.OrdinalIgnoreCase) { "Volume", "VolumeSnapshot", "LegacyDriver", "" };

    private sealed record Ovladac(string Poskytovatel, string Verze, string Datum, bool Podepsany, string Inf);

    public static Task<Zprava> VypisAsync(IProgress<string> log, CancellationToken ct) => Task.Run(() =>
    {
        log.Report("Načítám ovladače…");
        var ovladace = new Dictionary<string, Ovladac>(StringComparer.OrdinalIgnoreCase);
        using (var q = new ManagementObjectSearcher("SELECT DeviceID, DriverProviderName, DriverVersion, DriverDate, IsSigned, InfName FROM Win32_PnPSignedDriver"))
            foreach (var o in q.Get())
            {
                var id = o["DeviceID"] as string;
                if (string.IsNullOrEmpty(id)) continue;
                var datum = "";
                try { datum = ManagementDateTimeConverter.ToDateTime((string)o["DriverDate"]).ToString("d. M. yyyy"); } catch { }
                ovladace[id] = new Ovladac($"{o["DriverProviderName"]}", $"{o["DriverVersion"]}", datum, o["IsSigned"] is true, $"{o["InfName"]}");
            }

        log.Report("Načítám zařízení…");
        var zarizeni = new List<(string Trida, string Nazev, string Vyrobce, int Kod, string Id, string Popis)>();
        using (var q = new ManagementObjectSearcher("SELECT Name, PNPClass, Manufacturer, ConfigManagerErrorCode, DeviceID, Description FROM Win32_PnPEntity"))
            foreach (var o in q.Get())
            {
                var trida = $"{o["PNPClass"]}";
                if (Skryte.Contains(trida)) continue;
                zarizeni.Add((trida, (o["Name"] as string) ?? (o["Description"] as string) ?? "Neznámé zařízení",
                    $"{o["Manufacturer"]}", Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0), $"{o["DeviceID"]}", $"{o["Description"]}"));
            }
        log.Report($"Nalezeno {zarizeni.Count} zařízení.");

        var z = new Zprava("Správce zařízení – kompletní výpis") { Obnovit = VypisAsync };
        string Detail((string Trida, string Nazev, string Vyrobce, int Kod, string Id, string Popis) d)
        {
            var radky = new List<string>();
            if (d.Kod != 0) { radky.Add($"Problém: {Kod(d.Kod)}"); radky.Add($"Řešení: {Rada(d.Kod)}"); }
            if (ovladace.TryGetValue(d.Id, out var o))
            {
                radky.Add($"Ovladač: {o.Poskytovatel} · verze {o.Verze}" + (o.Datum.Length > 0 ? $" · {o.Datum}" : ""));
                radky.Add($"Podpis: {(o.Podepsany ? "digitálně podepsaný" : "NEPODEPSANÝ")}" + (o.Inf.Length > 0 ? $" · {o.Inf}" : ""));
            }
            else radky.Add("Ovladač: nenainstalován nebo nelze zjistit");
            radky.Add($"ID: {d.Id}");
            return string.Join("\n", radky);
        }
        AkceRadku Otevrit() => new("Otevřít Správce zařízení", (_, _) =>
        {
            Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
            return Task.FromResult("Správce zařízení otevřen.");
        });

        var problemy = zarizeni.Where(d => d.Kod != 0).ToList();
        if (problemy.Count > 0)
        {
            var sp = z.PridejSekci($"⚠ Zařízení s problémem ({problemy.Count})");
            foreach (var d in problemy)
                sp.Pridej(d.Nazev, NazevTridy(d.Trida), d.Kod is 22 or 45 ? Stav.Info : Stav.Chyba, Detail(d), Otevrit());
        }

        foreach (var g in zarizeni.GroupBy(d => NazevTridy(d.Trida)).OrderBy(g => g.Key, StringComparer.Create(new System.Globalization.CultureInfo("cs-CZ"), true)))
        {
            var chyb = g.Count(d => d.Kod != 0);
            var s = z.PridejSekci($"{g.Key} ({g.Count()})" + (chyb > 0 ? $" – {chyb} s problémem" : ""));
            s.Sbalena = chyb == 0;
            foreach (var d in g.OrderBy(d => d.Nazev))
            {
                var vyr = d.Vyrobce.Length > 0 && !d.Vyrobce.StartsWith("(") ? d.Vyrobce : ovladace.TryGetValue(d.Id, out var o) ? o.Poskytovatel : "";
                var stav = d.Kod == 0 ? (Stav?)null : d.Kod is 22 or 45 ? Stav.Info : Stav.Chyba;
                s.Pridej(d.Nazev, vyr, stav, Detail(d));
            }
        }

        var nepodepsane = ovladace.Values.Count(o => !o.Podepsany);
        z.Stav = problemy.Any(d => d.Kod is not (22 or 45)) ? Stav.Chyba : Stav.Ok;
        z.Shrnuti = $"{zarizeni.Count} zařízení v {zarizeni.Select(d => d.Trida).Distinct().Count()} kategoriích. " +
                    (problemy.Count > 0 ? $"{problemy.Count} s problémem." : "Všechna fungují.") +
                    (nepodepsane > 0 ? $" Nepodepsané ovladače: {nepodepsane}." : "") +
                    " Kategorii rozbalíte kliknutím na její název.";
        return z;
    }, ct);

    private static string NazevTridy(string t) => Tridy.TryGetValue(t, out var n) ? n : t.Length > 0 ? t : "Ostatní zařízení";
}
