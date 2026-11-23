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
            Popis = "Kritické události za 7 dní srozumitelně: seskupí je, vysvětlí, řekne jak jsou vážné a co řešit – a co je jen běžný šum.",
            Spustit = async (log, ct) => Vysl(await SpolehlivostService.ZpravaAsync(log, ct)) },
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

        new() {
            Nazev = "Dočasně vypnout ochrany (instalace programů)", Kategorie = Kategorie.Nastroje,
            Popis = "Jedním tlačítkem dočasně vypne Defender, firewall i SmartScreen, když brání instalaci programu. Stejně jednoduše je zase zapne. Používat jen krátce a pak ochrany obnovit.",
            Spustit = async (log, ct) => Vysl(await OchranyService.ZpravaAsync(log, ct)) },

        new() {
            Nazev = "Změnit heslo pro spuštění", Kategorie = Kategorie.Nastroje,
            Popis = "Nastaví vlastní heslo, kterým se aplikace spouští. Uloží se jen do tohoto počítače. Chrání nástroj před spuštěním zákazníkem.",
            Spustit = (log, _) => Task.FromResult(Vysl(Views.HesloOkno.Zmenit())) },

        new() {
            Nazev = "Zámek heslem při spuštění (zapnout/vypnout)", Kategorie = Kategorie.Nastroje,
            Popis = "Zapne nebo vypne, jestli se aplikace spouští na heslo. Výchozí heslo je ve veřejném zdrojáku slabé – buď si nastav vlastní, nebo zámek vypni.",
            Spustit = (log, ct) => Task.Run(() => Vysl(ZamekZprava()), ct) },

        new() {
            Nazev = "Benchmark PC", Kategorie = Kategorie.Diagnostika,
            Popis = "Výkon procesoru (1 jádro / všechna), paměti, grafiky a disku. Skóre, porovnání s obvyklými výsledky a historie testů.",
            Spustit = BenchmarkPcService.SpustAsync },

        // ---------- DISKY ----------
        new() {
            Nazev = "Kvalita a zdraví disků", Kategorie = Kategorie.Disky,
            Popis = "Skóre 0–100 pro každý disk: SMART srozumitelně, teplota, opotřebení, vadné sektory a doporučení.",
            Spustit = async (log, ct) => Vysl(await DiskService.KvalitaAsync(log, ct)) },
        new() {
            Nazev = "Test povrchu (jen čtení)", Kategorie = Kategorie.Disky,
            Popis = "Projde povrch disku a ukáže mapu bloků – vadné a pomalé oblasti. Rychlý nebo úplný režim. Nic nezapisuje.",
            Spustit = DiskService.TestPovrchuAsync },
        new() {
            Nazev = "Rychlost disku (benchmark)", Kategorie = Kategorie.Disky,
            Popis = "Rychlost čtení a zápisu, přístupová doba a porovnání s tím, co je u daného typu disku obvyklé.",
            Spustit = DiskService.BenchmarkAsync },
        new() {
            Nazev = "Kontrola a oprava disků (chkdsk)", Kategorie = Kategorie.Disky,
            Popis = "Zkontroluje souborový systém všech jednotek a čitelně vypíše výsledek. Oprava jen po potvrzení.",
            Spustit = async (log, ct) => Vysl(await DiskService.ChkdskAsync(log, ct)) },
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

    /// <summary>Stránka pro zapnutí/vypnutí zámku heslem při spuštění.</summary>
    private static Zprava ZamekZprava()
    {
        var aktivni = HesloService.ZamekAktivni;
        var z = new Zprava("Zámek heslem při spuštění") { Obnovit = (l, c) => Task.Run(() => ZamekZprava(), c) };
        z.Stav = aktivni ? Stav.Ok : Stav.Varovani;
        z.Shrnuti = aktivni
            ? "Zámek je ZAPNUTÝ – aplikace se spouští na heslo."
            : "Zámek je VYPNUTÝ – aplikace se spustí bez hesla.";

        if (aktivni)
            z.HromadneAkce.Add(new AkceRadku("Vypnout zámek heslem",
                (l, c) => Task.Run(() =>
                {
                    try { HesloService.NastavZamek(false); return "Zámek vypnut – od příštího spuštění se heslo nevyžaduje."; }
                    catch (Exception ex) { return "Nepodařilo se vypnout (potřeba správce?): " + ex.Message; }
                }, c))
            { Potvrzeni = "Opravdu vypnout zámek? Aplikace pak půjde spustit bez hesla – zajistěte si jinou ochranu." });
        else
            z.HromadneAkce.Add(new AkceRadku("Zapnout zámek heslem",
                (l, c) => Task.Run(() =>
                {
                    try { HesloService.NastavZamek(true); return "Zámek zapnut – od příštího spuštění se bude vyžadovat heslo."; }
                    catch (Exception ex) { return "Nepodařilo se zapnout (potřeba správce?): " + ex.Message; }
                }, c))
            { Doporucena = true });

        var s = z.PridejSekci("Stav");
        s.Pridej("Zámek heslem při spuštění", aktivni ? "zapnutý" : "VYPNUTÝ", aktivni ? Stav.Ok : Stav.Varovani,
            "Změna platí od příštího spuštění aplikace. Uloží se jen do tohoto počítače (HKLM).");
        s.Pridej("Vlastní heslo", "Nástroje → Změnit heslo pro spuštění", Stav.Info,
            "Výchozí heslo je ve veřejném zdrojovém kódu, takže je slabé. Pokud zámek necháš zapnutý, nastav si vlastní heslo.");
        return z;
    }

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
