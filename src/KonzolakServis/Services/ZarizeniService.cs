using System.Diagnostics;
using System.IO;
using System.Management;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>Zařízení s problémem ovladače (vykřičník ve Správci zařízení) a záloha ovladačů.</summary>
public static class ZarizeniService
{
    private static string Kod(int k) => k switch
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

    private static string Rada(int k) => k switch
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
}
