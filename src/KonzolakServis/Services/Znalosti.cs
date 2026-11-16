namespace KonzolakServis.Services;

public enum Doporuceni { Ponechat, Vypnout, PodlePotreby, Nezname }

/// <summary>Co o položce víme: popis pro zákazníka, doporučení a vliv na výkon.</summary>
public sealed record Info(string Nazev, string CoToJe, Doporuceni Doporuceni, string Vliv, string Proc);

/// <summary>Znalostní báze IT technika – programy po spuštění. Hledá se podle názvu nebo cesty (malá písmena).</summary>
public static class Znalosti
{
    private static readonly (string[] Klice, Info Info)[] Programy =
    {
        (new[] { "securityhealth" }, new("Zabezpečení Windows", "Ikona a hlášení Windows Defenderu v oznamovací oblasti.",
            Doporuceni.Ponechat, "Nízký", "Součást ochrany systému – nevypínat.")),
        (new[] { "rtkaud", "realtek", "rthdvcpl", "waves maxxaudio", "maxxaudio" }, new("Zvukový ovladač (Realtek / Waves)", "Ovládací panel a služby zvukové karty.",
            Doporuceni.Ponechat, "Nízký", "Bez něj nemusí fungovat přepínání sluchátek, mikrofon nebo zvukové efekty.")),
        (new[] { "synaptics", "elan", "syntp", "etdctrl", "precision touchpad" }, new("Ovladač touchpadu", "Gesta a nastavení touchpadu u notebooku.",
            Doporuceni.Ponechat, "Nízký", "Po vypnutí mohou přestat fungovat gesta.")),
        (new[] { "igfx", "intel graphics", "igcc", "intel(r) graphics" }, new("Intel grafika", "Ovládací centrum integrované grafiky Intel.",
            Doporuceni.PodlePotreby, "Nízký", "Ovladač funguje i bez něj, vypnutím přijdete jen o rychlé klávesy a ikonu.")),
        (new[] { "nvidia", "nvbackend", "nvcontainer" }, new("NVIDIA", "Doplňky grafické karty NVIDIA (GeForce Experience / aplikace NVIDIA, overlay).",
            Doporuceni.PodlePotreby, "Střední", "Ovladač grafiky běží i bez nich; vypnout, pokud nepoužíváte overlay a automatické aktualizace ovladačů.")),
        (new[] { "radeon", "amd software", "cncmd", "amdrsserv" }, new("AMD Radeon Software", "Ovládací panel a overlay grafiky AMD.",
            Doporuceni.PodlePotreby, "Střední", "Ovladač funguje i bez něj; vypnout, pokud nepoužíváte overlay a nahrávání.")),
        (new[] { "onedrive" }, new("Microsoft OneDrive", "Synchronizace souborů s cloudem Microsoftu.",
            Doporuceni.PodlePotreby, "Střední", "Pokud zákazník OneDrive používá (zálohy Plochy a Dokumentů), ponechat. Jinak vypnout – při startu synchronizuje a zatěžuje disk i síť.")),
        (new[] { "teams" }, new("Microsoft Teams", "Aplikace pro schůzky a chat.",
            Doporuceni.Vypnout, "Vysoký", "Patří k nejnáročnějším programům při startu. Spustí se, až ho uživatel otevře.")),
        (new[] { "skype" }, new("Skype", "Starší aplikace pro hovory (ukončena Microsoftem).",
            Doporuceni.Vypnout, "Střední", "Skype už není podporován, při startu zbytečně zatěžuje systém.")),
        (new[] { "spotify" }, new("Spotify", "Hudební přehrávač.",
            Doporuceni.Vypnout, "Střední", "Nemusí běžet hned po startu, otevře se ručně.")),
        (new[] { "discord" }, new("Discord", "Chat a hlasová komunikace (hlavně hráči).",
            Doporuceni.Vypnout, "Vysoký", "Náročný na paměť; stačí spouštět ručně.")),
        (new[] { "steam" }, new("Steam", "Herní obchod a knihovna her.",
            Doporuceni.Vypnout, "Vysoký", "Při startu kontroluje aktualizace her a zatěžuje disk. Hry fungují i po ručním spuštění.")),
        (new[] { "epicgames", "epic games", "eoslauncher" }, new("Epic Games Launcher", "Herní obchod Epic.",
            Doporuceni.Vypnout, "Vysoký", "Náročný launcher, stačí spouštět ručně.")),
        (new[] { "battle.net", "battlenet" }, new("Battle.net", "Herní launcher Blizzard.", Doporuceni.Vypnout, "Střední", "Stačí spouštět ručně.")),
        (new[] { "eadesktop", "ea desktop", "origin" }, new("EA app / Origin", "Herní launcher EA.", Doporuceni.Vypnout, "Střední", "Stačí spouštět ručně.")),
        (new[] { "ubisoft", "upc.exe", "uplay" }, new("Ubisoft Connect", "Herní launcher Ubisoft.", Doporuceni.Vypnout, "Střední", "Stačí spouštět ručně.")),
        (new[] { "gog galaxy", "galaxyclient" }, new("GOG Galaxy", "Herní launcher GOG.", Doporuceni.Vypnout, "Střední", "Stačí spouštět ručně.")),
        (new[] { "riotclient", "riot client", "vanguard" }, new("Riot Client", "Launcher her Riot (LoL, Valorant).", Doporuceni.PodlePotreby, "Střední", "Klient vypnout lze; anti-cheat Vanguard je potřeba pro hraní Valorantu.")),
        (new[] { "wallpaper engine", "wallpaper32", "wallpaper64" }, new("Wallpaper Engine", "Animované tapety.", Doporuceni.PodlePotreby, "Střední", "Spotřebovává CPU/GPU neustále; ponechat jen pokud o něj zákazník stojí.")),
        (new[] { "zoom" }, new("Zoom", "Videokonference.", Doporuceni.Vypnout, "Střední", "Stačí spustit před schůzkou.")),
        (new[] { "dropbox" }, new("Dropbox", "Synchronizace souborů.", Doporuceni.PodlePotreby, "Střední", "Ponechat, pokud se používá k synchronizaci; jinak vypnout.")),
        (new[] { "googledrivesync", "google drive", "googledrivefs" }, new("Google Disk", "Synchronizace souborů s Google Diskem.", Doporuceni.PodlePotreby, "Střední", "Ponechat, pokud se používá.")),
        (new[] { "icloud" }, new("iCloud", "Synchronizace s Apple iCloud.", Doporuceni.PodlePotreby, "Střední", "Ponechat, pokud se používá (fotky z iPhonu).")),
        (new[] { "ituneshelper" }, new("iTunes Helper", "Pomocník iTunes pro připojení iPhonu.", Doporuceni.Vypnout, "Nízký", "iTunes ho spustí samo při otevření.")),
        (new[] { "adobegcinvoker", "adobe genuine", "agcinvoker" }, new("Adobe Genuine Software", "Kontrola licence Adobe.", Doporuceni.Vypnout, "Nízký", "Pro běh programů Adobe není potřeba při startu.")),
        (new[] { "adobearm", "acrotray", "adobe acrobat update", "adobe updater" }, new("Aktualizace Adobe", "Kontrola aktualizací Adobe Readeru/Acrobatu.", Doporuceni.Vypnout, "Nízký", "Aktualizace se zkontrolují i při spuštění programu.")),
        (new[] { "creative cloud", "adobe desktop service", "ccxprocess", "coresync" }, new("Adobe Creative Cloud", "Správce aplikací Adobe a synchronizace.", Doporuceni.PodlePotreby, "Vysoký", "Velmi náročný; vypnout, pokud se Adobe aplikace nepoužívají denně.")),
        (new[] { "googleupdate", "google update" }, new("Google Update", "Aktualizace Chrome a dalších aplikací Google.", Doporuceni.Ponechat, "Nízký", "Udržuje prohlížeč aktuální a bezpečný.")),
        (new[] { "msedge", "microsoft edge" }, new("Microsoft Edge – předspuštění", "Edge se načítá do paměti už při startu, aby se rychleji otevřel.", Doporuceni.Vypnout, "Střední", "Zbytečně zabírá paměť, pokud zákazník používá jiný prohlížeč.")),
        (new[] { "opera" }, new("Opera – asistent prohlížeče", "Předspuštění a aktualizace prohlížeče Opera.", Doporuceni.Vypnout, "Střední", "Prohlížeč funguje i bez něj.")),
        (new[] { "brave" }, new("Brave", "Prohlížeč Brave.", Doporuceni.Vypnout, "Nízký", "Není nutné při startu.")),
        (new[] { "jusched", "java update" }, new("Java Update Scheduler", "Kontrola aktualizací Javy.", Doporuceni.Vypnout, "Nízký", "Javu je lepší aktualizovat ručně nebo odinstalovat, pokud není potřeba.")),
        (new[] { "ccleaner" }, new("CCleaner", "Čisticí nástroj se sledováním na pozadí.", Doporuceni.Vypnout, "Střední", "Sledování na pozadí není potřeba, při startu zbytečně běží.")),
        (new[] { "avast", "avgui", "avg antivirus", "eset security", "egui", "ekrn", "kaspersky", "avp.exe", "bitdefender", "norton", "mcafee", "malwarebytes", "mbam" }, new("Antivirus", "Ochrana proti malwaru.",
            Doporuceni.Ponechat, "Střední", "Bezpečnostní software nevypínat. Mít nainstalovaný jen jeden antivirus – dva se navzájem brzdí.")),
        (new[] { "razer", "synapse" }, new("Razer Synapse", "Nastavení periferií Razer (RGB, makra).", Doporuceni.PodlePotreby, "Střední", "Bez něj mohou nefungovat makra a podsvícení.")),
        (new[] { "lghub", "logitech g hub", "lcore", "logi options", "logioptions" }, new("Logitech G HUB / Options", "Nastavení myší a klávesnic Logitech.", Doporuceni.PodlePotreby, "Střední", "Ponechat, pokud se používají vlastní tlačítka nebo makra.")),
        (new[] { "icue", "corsair" }, new("Corsair iCUE", "RGB a ventilátory Corsair.", Doporuceni.PodlePotreby, "Vysoký", "Náročný; bez něj se nepoužije vlastní RGB a křivky ventilátorů.")),
        (new[] { "armoury", "asus", "aura" }, new("ASUS Armoury Crate / Aura", "Nástroje ASUS pro desku, RGB a profily výkonu.", Doporuceni.PodlePotreby, "Střední", "U notebooků ASUS řídí profily výkonu a ventilátory – tam ponechat.")),
        (new[] { "msi center", "dragon center", "msi.centralserver" }, new("MSI Center", "Nástroje MSI.", Doporuceni.PodlePotreby, "Střední", "U notebooků MSI řídí výkon a ventilátory – tam ponechat.")),
        (new[] { "lenovo vantage", "lenovo" }, new("Lenovo Vantage / nástroje Lenovo", "Aktualizace a nastavení notebooku Lenovo.", Doporuceni.Ponechat, "Nízký", "Řídí nabíjení baterie a režimy výkonu.")),
        (new[] { "hp ", "hpwuschd", "hp support", "hpmsg" }, new("Nástroje HP", "Aktualizace a podpora HP.", Doporuceni.PodlePotreby, "Nízký", "Upozorňovače na aktualizace lze vypnout.")),
        (new[] { "dell" }, new("Nástroje Dell", "Aktualizace a podpora Dell.", Doporuceni.PodlePotreby, "Nízký", "SupportAssist lze vypnout, pokud se ovladače aktualizují jinak.")),
        (new[] { "docker" }, new("Docker Desktop", "Kontejnery pro vývojáře.", Doporuceni.Vypnout, "Vysoký", "Spouští virtuální stroj a zabírá hodně RAM; spouštět ručně.")),
        (new[] { "vmware", "virtualbox" }, new("Virtualizace (VMware / VirtualBox)", "Pomocné nástroje virtualizace.", Doporuceni.Vypnout, "Nízký", "Není potřeba, dokud neběží virtuální stroj.")),
        (new[] { "laragon", "xampp", "wamp" }, new("Lokální webový server", "Vývojové prostředí (Apache/MySQL/PHP).", Doporuceni.Vypnout, "Střední", "Spouštět ručně, když se vyvíjí.")),
        (new[] { "obs64", "obs32", "obs studio" }, new("OBS Studio", "Nahrávání a streamování.", Doporuceni.Vypnout, "Střední", "Stačí spustit ručně.")),
        (new[] { "qbittorrent", "utorrent", "bittorrent" }, new("Torrent klient", "Stahování přes BitTorrent.", Doporuceni.Vypnout, "Vysoký", "Na pozadí vytěžuje síť i disk.")),
        (new[] { "whatsapp" }, new("WhatsApp", "Chat.", Doporuceni.PodlePotreby, "Nízký", "Ponechat, pokud chce zákazník dostávat zprávy hned.")),
        (new[] { "messenger" }, new("Messenger", "Chat Facebooku.", Doporuceni.Vypnout, "Nízký", "Stačí spouštět ručně.")),
        (new[] { "cortana" }, new("Cortana", "Hlasový asistent (v ČR prakticky nevyužitelný).", Doporuceni.Vypnout, "Nízký", "V češtině nefunguje.")),
        (new[] { "phone link", "yourphone", "phoneexperiencehost" }, new("Propojení s telefonem", "Propojení Windows s mobilem.", Doporuceni.PodlePotreby, "Nízký", "Vypnout, pokud se nepoužívá.")),
    };

    public static Info Program(string nazev, string prikaz)
    {
        var hledej = $"{nazev} {prikaz}".ToLowerInvariant();
        foreach (var (klice, info) in Programy)
            if (klice.Any(hledej.Contains)) return info;
        return new Info(nazev, "Program třetí strany, který není v databázi Konzolák.", Doporuceni.Nezname, "neurčeno",
            "Pokud program neznáte nebo ho nepoužíváte každý den, můžete ho vypnout – kdykoli jde znovu zapnout. Neznámé programy ze složek Temp nebo AppData prověřte antivirem.");
    }

    public static string Text(Doporuceni d) => d switch
    {
        Doporuceni.Ponechat => "Ponechat zapnuté",
        Doporuceni.Vypnout => "Doporučujeme vypnout",
        Doporuceni.PodlePotreby => "Podle potřeby uživatele",
        _ => "Posuďte podle situace"
    };
}
