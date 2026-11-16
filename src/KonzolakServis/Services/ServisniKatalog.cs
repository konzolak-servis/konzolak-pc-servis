using System.IO;
using System.Text;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>Seznam všech akcí aplikace. Novou funkci přidáš jedním záznamem.</summary>
public static class ServisniKatalog
{
    /// <summary>Systémový disk – nemusí to být vždy C:.</summary>
    private static string Sys => Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";

    private static VysledekAkce Vysl(Zprava z) => new(z.Stav, z.Shrnuti) { Zprava = z };

    private static VysledekAkce Z(int rc, string ok, string chyba) =>
        rc == 0 ? new(Stav.Ok, ok) : new(Stav.Chyba, $"{chyba} (kód {rc})");

    public static IReadOnlyList<ServisniAkce> Vse { get; } = new List<ServisniAkce>
    {
        // ---------- DIAGNOSTIKA ----------
        new() {
            Nazev = "Kompletní diagnostika (perfmon)", Kategorie = Kategorie.Diagnostika,
            Popis = "Spustí perfmon /report, počká na zprávu a vypíše nalezené chyby a varování.",
            Spustit = async (log, ct) => {
                var z = await PerfmonReportService.VytvorAsync(log, ct);
                var zprava = PerfmonReportService.DoZpravy(z);
                return new(zprava.Stav, zprava.Shrnuti) { Zprava = zprava };
            }},
        new() {
            Nazev = "Index spolehlivosti a pády", Kategorie = Kategorie.Diagnostika,
            Popis = "Kritické události za posledních 7 dní (pády aplikací, BSOD, chyby ovladačů).",
            Spustit = async (log, ct) => Z(await ProcessRunner.PowerShellAsync(
                "Get-WinEvent -FilterHashtable @{LogName='System','Application';Level=1,2;StartTime=(Get-Date).AddDays(-7)} -MaxEvents 40 " +
                "| ForEach-Object { '{0:dd.MM. HH:mm}  {1}  {2}' -f $_.TimeCreated,$_.ProviderName,($_.Message -split \"`n\")[0] }",
                log, ct), "Výpis událostí hotov", "Události nelze načíst") },
        new() {
            Nazev = "Zpráva o baterii", Kategorie = Kategorie.Diagnostika,
            Popis = "U notebooků: zdraví baterie, kapacita a počet cyklů.",
            Spustit = BatteryService.ZpravaAsync },

        // ---------- OPTIMALIZACE ----------
        new() {
            Nazev = "Programy po spuštění", Kategorie = Kategorie.Optimalizace,
            Popis = "Co se spouští s Windows: popis, doporučení, vliv na start. Vypnout / zapnout jedním klikem.",
            Spustit = async (log, ct) => Vysl(await StartupService.ZpravaAsync(log, ct)) },
        new() {
            Nazev = "Služby Windows", Kategorie = Kategorie.Optimalizace,
            Popis = "Zbytečné služby na pozadí (telemetrie, Xbox, fax…) s vysvětlením. Vše vratné.",
            Spustit = async (log, ct) => Vysl(await SluzbyService.ZpravaAsync(log, ct)) },
        new() {
            Nazev = "Výkon a soukromí", Kategorie = Kategorie.Optimalizace,
            Popis = "Plán napájení, herní nahrávání, vizuální efekty, telemetrie, reklamy ve Windows…",
            Spustit = async (log, ct) => Vysl(await VykonService.ZpravaAsync(log, ct)) },

        new() {
            Nazev = "Zabezpečení a licence", Kategorie = Kategorie.Diagnostika,
            Popis = "Antivirus, firewall, aktualizace, UAC, Secure Boot, TPM, BitLocker, aktivace Windows.",
            Spustit = async (log, ct) => Vysl(await ZabezpeceniService.ZpravaAsync(log, ct)) },
        new() {
            Nazev = "Zařízení a ovladače", Kategorie = Kategorie.Diagnostika,
            Popis = "Zařízení s vykřičníkem ve Správci zařízení – co je špatně a jak to opravit.",
            Spustit = ZarizeniService.ZpravaAsync },

        // ---------- DISKY ----------
        new() {
            Nazev = "Zdraví disků (SMART)", Kategorie = Kategorie.Disky,
            Popis = "Stav, teplota, opotřebení a chyby čtení všech fyzických disků.",
            Spustit = async (log, ct) => Z(await ProcessRunner.PowerShellAsync(
                "Get-PhysicalDisk | ForEach-Object { $r = $_ | Get-StorageReliabilityCounter; " +
                "'{0} ({1}) – stav: {2}, teplota: {3} °C, opotřebení: {4} %, chyby čtení: {5}, hodin v provozu: {6}' -f " +
                "$_.FriendlyName,$_.MediaType,$_.HealthStatus,$r.Temperature,$r.Wear,$r.ReadErrorsTotal,$r.PowerOnHours }",
                log, ct), "SMART načten", "SMART nelze načíst") },
        new() {
            Nazev = "Kontrola systémového disku (online)", Kategorie = Kategorie.Disky,
            Popis = "chkdsk /scan – kontrola souborového systému bez restartu.",
            Spustit = async (log, ct) => Z(await ProcessRunner.RunAsync("chkdsk.exe", $"{Sys} /scan", log, ct),
                "Disk bez chyb", "Nalezeny chyby – doporučena oprava") },
        new() {
            Nazev = "Oprava systémového disku při restartu", Kategorie = Kategorie.Disky, MeniSystem = true,
            Varovani = "Oprava proběhne při příštím restartu a může trvat i desítky minut.",
            Popis = "Označí disk ke kontrole a opravě (chkdsk /f) při dalším startu.",
            Spustit = async (log, ct) => {
                // fsutil místo „echo A| chkdsk“ – funguje v každém jazyce Windows (A/Y/J…)
                var rc = await ProcessRunner.RunAsync("fsutil.exe", $"dirty set {Sys}", log, ct);
                return rc == 0 ? new(Stav.Info, "Oprava naplánována na příští restart")
                               : new(Stav.Chyba, $"Opravu se nepodařilo naplánovat (kód {rc})");
            }},
        new() {
            Nazev = "Optimalizace / defragmentace", Kategorie = Kategorie.Disky, MeniSystem = true,
            Popis = "SSD: TRIM, HDD: defragmentace. Windows sám pozná typ disku.",
            Spustit = async (log, ct) => Z(await ProcessRunner.RunAsync("defrag.exe", "/C /O /U /V", log, ct),
                "Disky optimalizovány", "Optimalizace selhala") },

        // ---------- OPRAVY ----------
        new() {
            Nazev = "Vytvořit bod obnovení", Kategorie = Kategorie.Opravy, MeniSystem = true,
            Popis = "Záloha stavu systému před zásahy.",
            Spustit = async (log, ct) => Z(await ProcessRunner.PowerShellAsync(
                "Enable-ComputerRestore -Drive $env:SystemDrive; Checkpoint-Computer -Description 'Konzolák Servis' -RestorePointType MODIFY_SETTINGS",
                log, ct), "Bod obnovení vytvořen", "Bod obnovení se nepodařilo vytvořit") },
        new() {
            Nazev = "Oprava obrazu Windows (DISM)", Kategorie = Kategorie.Opravy, MeniSystem = true,
            Popis = "DISM /RestoreHealth – opraví úložiště součástí. Spouštěj před SFC.",
            Spustit = async (log, ct) => Z(await ProcessRunner.RunAsync("dism.exe",
                "/Online /Cleanup-Image /RestoreHealth", log, ct), "Obraz Windows v pořádku", "DISM hlásí chybu") },
        new() {
            Nazev = "Kontrola systémových souborů (SFC)", Kategorie = Kategorie.Opravy, MeniSystem = true,
            Popis = "sfc /scannow – najde a opraví poškozené soubory Windows.",
            Spustit = async (log, ct) => Z(await ProcessRunner.RunAsync("sfc.exe", "/scannow", log, ct,
                Encoding.Unicode), "Systémové soubory v pořádku", "SFC našel problémy") },
        new() {
            Nazev = "Oprava Windows Update", Kategorie = Kategorie.Opravy, MeniSystem = true,
            Popis = "Zastaví služby, vyčistí mezipaměť aktualizací a služby znovu spustí.",
            Spustit = async (log, ct) => Z(await ProcessRunner.PowerShellAsync(
                "'wuauserv','bits','cryptsvc' | Stop-Service -Force -ErrorAction SilentlyContinue; " +
                "Remove-Item \"$env:windir\\SoftwareDistribution\\Download\\*\" -Recurse -Force -ErrorAction SilentlyContinue; " +
                "'wuauserv','bits','cryptsvc' | Start-Service; 'Mezipaměť aktualizací vyčištěna.'",
                log, ct), "Windows Update resetován", "Reset se nezdařil") },

        new() {
            Nazev = "Záloha ovladačů", Kategorie = Kategorie.Opravy,
            Popis = "Uloží všechny nainstalované ovladače do složky – hodí se před reinstalací Windows.",
            Spustit = ZarizeniService.ZalohaOvladacuAsync },

        // ---------- ČIŠTĚNÍ ----------
        new() {
            Nazev = "Vyčistit dočasné soubory", Kategorie = Kategorie.Cisteni, MeniSystem = true,
            Popis = "%TEMP%, Windows\\Temp, Prefetch – soubory používané jinými programy přeskočí.",
            Spustit = (log, ct) => Task.Run(() => {
                long celkem = 0;
                foreach (var dir in new[] { Path.GetTempPath(),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") })
                    celkem += SmazObsah(dir, log, ct);
                return new VysledekAkce(Stav.Ok, $"Uvolněno {celkem / 1024d / 1024:0.#} MB");
            }, ct) },
        new() {
            Nazev = "Vyprázdnit koš", Kategorie = Kategorie.Cisteni, MeniSystem = true,
            Popis = "Trvale smaže obsah koše na všech discích.",
            Spustit = async (log, ct) => Z(await ProcessRunner.PowerShellAsync(
                "Clear-RecycleBin -Force -ErrorAction SilentlyContinue; 'Koš vyprázdněn.'", log, ct), "Koš prázdný", "Koš nelze vyprázdnit") },
        new() {
            Nazev = "Vyčištění součástí Windows", Kategorie = Kategorie.Cisteni, MeniSystem = true,
            Popis = "DISM /StartComponentCleanup – odstraní staré verze aktualizací.",
            Spustit = async (log, ct) => Z(await ProcessRunner.RunAsync("dism.exe",
                "/Online /Cleanup-Image /StartComponentCleanup", log, ct), "Součásti vyčištěny", "Čištění selhalo") },

        // ---------- SÍŤ ----------
        new() {
            Nazev = "Test připojení", Kategorie = Kategorie.Sit,
            Popis = "Ping na bránu, DNS a internet.",
            Spustit = async (log, ct) => Z(await ProcessRunner.PowerShellAsync(
                "$gw=(Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Select -First 1).NextHop; " +
                "foreach($h in @($gw,'1.1.1.1','seznam.cz')){ $r=Test-Connection $h -Count 2 -ErrorAction SilentlyContinue; " +
                "if($r){'{0}: OK, {1} ms' -f $h,[int]($r|Measure-Object ResponseTime -Average).Average}else{'{0}: NEDOSTUPNÉ' -f $h} }",
                log, ct), "Test hotov", "Test selhal") },
        new() {
            Nazev = "Reset sítě (DNS + Winsock)", Kategorie = Kategorie.Sit, MeniSystem = true,
            Varovani = "Po resetu Winsock je potřeba restart.",
            Popis = "Vyprázdní DNS mezipaměť, obnoví IP a resetuje Winsock.",
            Spustit = async (log, ct) => {
                await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", log, ct);
                await ProcessRunner.RunAsync("ipconfig.exe", "/renew", log, ct);
                return Z(await ProcessRunner.RunAsync("netsh.exe", "winsock reset", log, ct),
                    "Síť resetována – restartuj PC", "Reset sítě selhal");
            }},

        new() {
            Nazev = "Test rychlosti internetu", Kategorie = Kategorie.Sit,
            Popis = "Stahování, odesílání, ping, kolísání a síla Wi-Fi. Zdarma, servery Cloudflare.",
            Spustit = async (log, ct) => Vysl(await RychlostSiteService.ZpravaAsync(log, ct)) },

        // ---------- HW INFO ----------
        new() {
            Nazev = "Výpis zařízení (Správce zařízení)", Kategorie = Kategorie.HwInfo,
            Popis = "Všechna zařízení po kategoriích: výrobce, ovladač (verze, datum, podpis), stav a problémy.",
            Spustit = async (log, ct) => Vysl(await ZarizeniService.VypisAsync(log, ct)) },
        new() {
            Nazev = "Přehled hardwaru", Kategorie = Kategorie.HwInfo,
            Popis = "Systém, procesor, paměť, deska, BIOS, grafika, disky, volné místo, síť.",
            Spustit = (log, _) => HardwareInfoService.ZpravaAsync(log) },
    }.Concat(NastrojeService.Vse()).ToList();

    private static long SmazObsah(string dir, IProgress<string> log, CancellationToken ct)
    {
        long uvolneno = 0; int chyb = 0;
        if (!Directory.Exists(dir)) return 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            ct.ThrowIfCancellationRequested();
            try { var len = new FileInfo(f).Length; File.Delete(f); uvolneno += len; } catch { chyb++; }
        }
        foreach (var d in Directory.EnumerateDirectories(dir))
            try { Directory.Delete(d, true); } catch { }
        log.Report($"{dir}: uvolněno {uvolneno / 1024d / 1024:0.#} MB, přeskočeno {chyb} souborů v použití");
        return uvolneno;
    }
}
