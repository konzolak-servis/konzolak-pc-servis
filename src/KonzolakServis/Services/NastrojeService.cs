using System.Diagnostics;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>Rychlé spuštění vestavěných nástrojů Windows (God Mode, správce zařízení, registry…).</summary>
public static class NastrojeService
{
    public static ServisniAkce Nastroj(string nazev, string popis, string exe, string args = "") => new()
    {
        Nazev = nazev, Popis = popis, Kategorie = Kategorie.Nastroje,
        Spustit = (log, _) =>
        {
            log.Report($"> {exe} {args}");
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            var z = new Zprava(nazev) { Stav = Stav.Ok, Shrnuti = $"Otevřeno: {nazev}" };
            z.PridejSekci("K čemu to je").Pridej(popis);
            return Task.FromResult(new VysledekAkce(Stav.Ok, z.Shrnuti) { Zprava = z });
        }
    };

    public static IEnumerable<ServisniAkce> Vse() => new[]
    {
        Nastroj("God Mode (všechna nastavení)", "Skrytá složka Windows se všemi ~200 položkami Ovládacích panelů na jednom místě.",
            "explorer.exe", "shell:::{ED7BA470-8E54-465E-825C-99712043E01C}"),
        Nastroj("Správce zařízení", "Ovladače, neznámá zařízení, vypnutí / odinstalace zařízení.", "devmgmt.msc"),
        Nastroj("Správa disků", "Oddíly, písmena jednotek, inicializace nového disku.", "diskmgmt.msc"),
        Nastroj("Správa počítače", "Disky, služby, události, uživatelé a sdílené složky v jednom okně.", "compmgmt.msc"),
        Nastroj("Služby", "Všechny služby Windows – spuštění, zastavení, typ spouštění.", "services.msc"),
        Nastroj("Konfigurace systému (msconfig)", "Nouzový režim, diagnostické spuštění, možnosti zavádění.", "msconfig.exe"),
        Nastroj("Editor registru", "Pokročilé úpravy registru. Opatrně – změny nejdou vrátit.", "regedit.exe"),
        Nastroj("Prohlížeč událostí", "Podrobné protokoly chyb systému a aplikací.", "eventvwr.msc"),
        Nastroj("Monitor spolehlivosti", "Časová osa pádů, chyb a instalací s indexem stability 1–10.", "perfmon.exe", "/rel"),
        Nastroj("Monitor prostředků", "Co právě vytěžuje procesor, disk, síť a paměť – po procesech.", "resmon.exe"),
        Nastroj("Sledování výkonu (perfmon)", "Grafy výkonu, sady sběrných dat a sestavy.", "perfmon.exe"),
        Nastroj("Správce úloh", "Procesy, výkon, aplikace po spuštění.", "taskmgr.exe"),
        Nastroj("Plánovač úloh", "Úlohy spouštěné na pozadí (aktualizace, údržba, malware se tu rád schovává).", "taskschd.msc"),
        Nastroj("Programy a funkce", "Odinstalace programů, volitelné součásti Windows.", "appwiz.cpl"),
        Nastroj("Informace o systému", "Kompletní souhrn hardwaru a softwaru (msinfo32).", "msinfo32.exe"),
        Nastroj("Vlastnosti systému", "Název PC, virtuální paměť, ochrana systému, vzdálená plocha.", "sysdm.cpl"),
        Nastroj("Obnovení systému", "Návrat k dřívějšímu bodu obnovení.", "rstrui.exe"),
        Nastroj("Síťová připojení", "Adaptéry, IP adresy, sdílení připojení.", "ncpa.cpl"),
        Nastroj("Možnosti napájení", "Plány napájení a pokročilé nastavení.", "powercfg.cpl"),
        Nastroj("Uživatelské účty (netplwiz)", "Automatické přihlášení, správa účtů.", "netplwiz.exe"),
        Nastroj("Firewall – pokročilé", "Pravidla firewallu pro příchozí a odchozí provoz.", "wf.msc"),
        Nastroj("Zabezpečení Windows", "Defender, firewall, ochrana proti ransomwaru.", "windowsdefender:"),
        Nastroj("Windows Update", "Kontrola a instalace aktualizací.", "ms-settings:windowsupdate"),
        Nastroj("Diagnostika paměti RAM", "Test operační paměti při příštím restartu (mdsched).", "mdsched.exe"),
        Nastroj("Diagnostika DirectX", "Informace o grafice a zvuku, export pro hry (dxdiag).", "dxdiag.exe"),
        Nastroj("Vyčištění disku", "Klasický nástroj pro mazání dočasných a systémových souborů.", "cleanmgr.exe"),
        Nastroj("Ovládací panely", "Klasické Ovládací panely.", "control.exe"),
        Nastroj("Příkazový řádek (správce)", "cmd s právy správce.", "cmd.exe"),
        Nastroj("PowerShell (správce)", "PowerShell s právy správce.", "powershell.exe"),
        new ServisniAkce
        {
            Nazev = "Restart do BIOSu / UEFI", Kategorie = Kategorie.Nastroje, MeniSystem = true,
            Popis = "Restartuje počítač rovnou do nastavení firmwaru (bez mačkání F2/Del).",
            Varovani = "Počítač se OKAMŽITĚ restartuje. Ulož si rozdělanou práci.",
            Spustit = async (log, ct) =>
            {
                var rc = await ProcessRunner.RunAsync("shutdown.exe", "/r /fw /t 5", log, ct);
                return rc == 0 ? new VysledekAkce(Stav.Info, "Restart do BIOSu za 5 sekund…")
                               : new VysledekAkce(Stav.Chyba, "Nepodařilo se – starší PC bez UEFI to nepodporuje.");
            }
        },
        new ServisniAkce
        {
            Nazev = "Restart do pokročilého spuštění", Kategorie = Kategorie.Nastroje, MeniSystem = true,
            Popis = "Nabídka obnovení Windows: nouzový režim, oprava spuštění, příkazový řádek, obnovení.",
            Varovani = "Počítač se OKAMŽITĚ restartuje. Ulož si rozdělanou práci.",
            Spustit = async (log, ct) =>
            {
                var rc = await ProcessRunner.RunAsync("shutdown.exe", "/r /o /t 5", log, ct);
                return rc == 0 ? new VysledekAkce(Stav.Info, "Restart do pokročilého spuštění za 5 sekund…")
                               : new VysledekAkce(Stav.Chyba, "Restart se nepodařil (kód " + rc + ").");
            }
        },
    };
}
