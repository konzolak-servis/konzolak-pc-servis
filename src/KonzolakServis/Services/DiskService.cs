using System.IO;
using System.Management;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>Fyzický disk: co o něm víme z Windows (WMI) a které jednotky (C:, D:…) na něm leží.</summary>
public sealed record InfoDisku(int Cislo, string Nazev, string Typ, bool Ssd, string Rozhrani, long Velikost,
    int Zdravi, string Provozni, string Pnp, string Serial, string Firmware, int Otacky, List<char> Pismena)
{
    public string Popisek => $"Disk {Cislo}: {Nazev} ({DiskService.Gb(Velikost)}, {Typ})"
        + (Pismena.Count > 0 ? " – " + string.Join(", ", Pismena.Select(p => p + ":")) : "");
}

/// <summary>Spolehlivostní čítače Windows (fungují i pro NVMe, kde klasický SMART přes WMI není).</summary>
public sealed record Citace(int Teplota, int TeplotaMax, int Opotrebeni, long Hodiny, long Zapnuti,
    long ChybyCteni, long NeopravCteni, long ChybyZapisu, long NeopravZapisu);

/// <summary>Jeden atribut SMART (SATA disky): ID, aktuální hodnota, nejhorší, práh výrobce a surová hodnota.</summary>
public sealed record AtributSmart(int Id, int Hodnota, int Nejhorsi, int Prah, long Raw);

public static partial class DiskService
{
    internal static string Gb(double b) =>
        b >= 1e12 ? $"{b / 1e12:0.##} TB" : b >= 1e9 ? $"{b / 1e9:0.#} GB" : $"{b / 1e6:0} MB";

    private static string S(object? o) => o?.ToString()?.Trim() ?? "";
    private static long L(object? o) { try { return o == null ? 0 : Convert.ToInt64(o); } catch { return 0; } }

    private static List<ManagementBaseObject> Wmi(string dotaz, string scope = @"root\cimv2")
    {
        var l = new List<ManagementBaseObject>();
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(dotaz));
            foreach (var o in s.Get()) l.Add(o);
        }
        catch { }
        return l;
    }

    private const string Storage = @"root\Microsoft\Windows\Storage";

    // ================== Seznam disků ==================

    public static List<InfoDisku> SeznamDisku()
    {
        var msft = Wmi("SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus, OperationalStatus, SerialNumber, FirmwareVersion, SpindleSpeed FROM MSFT_PhysicalDisk", Storage)
            .GroupBy(d => S(d["DeviceId"])).ToDictionary(g => g.Key, g => g.First());
        var pismena = new Dictionary<int, List<char>>();
        foreach (var p in Wmi("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", Storage))
        {
            var c = PismenoZ(p["DriveLetter"]);
            if (c == '\0') continue;
            var n = (int)L(p["DiskNumber"]);
            if (!pismena.TryGetValue(n, out var seznam)) pismena[n] = seznam = new List<char>();
            seznam.Add(c);
        }

        var vysledek = new List<InfoDisku>();
        foreach (var d in Wmi("SELECT Index, Model, Size, PNPDeviceID, SerialNumber, FirmwareRevision, InterfaceType FROM Win32_DiskDrive"))
        {
            var n = (int)L(d["Index"]);
            msft.TryGetValue(n.ToString(), out var m);
            var media = (int)L(m?["MediaType"]);
            var bus = (int)L(m?["BusType"]) switch
            {
                17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 9 => "iSCSI", 12 => "SD", 13 => "MMC",
                3 => "ATA", 6 => "Fibre Channel", 14 => "virtuální", 15 => "Storage Spaces", _ => S(d["InterfaceType"])
            };
            var ssd = media == 4 || bus == "NVMe";
            var typ = ssd ? (bus == "NVMe" ? "SSD NVMe" : $"SSD {bus}".Trim()) : media == 3 ? $"HDD {bus}".Trim() : bus.Length > 0 ? bus : "neznámý typ";
            var provozni = m?["OperationalStatus"] is ushort[] os
                ? string.Join(", ", os.Select(ProvozniStav).Where(x => x.Length > 0)) : "";
            var nazev = S(m?["FriendlyName"]);
            vysledek.Add(new InfoDisku(n, nazev.Length > 0 ? nazev : S(d["Model"]), typ, ssd, bus,
                m != null && L(m["Size"]) > 0 ? L(m["Size"]) : L(d["Size"]),
                m != null ? (int)L(m["HealthStatus"]) : 5, provozni, S(d["PNPDeviceID"]),
                S(m?["SerialNumber"]) is { Length: > 0 } sn ? sn : S(d["SerialNumber"]),
                S(m?["FirmwareVersion"]) is { Length: > 0 } fw ? fw : S(d["FirmwareRevision"]),
                (int)Math.Clamp(L(m?["SpindleSpeed"]), 0, 100000),
                pismena.TryGetValue(n, out var pl) ? pl.OrderBy(x => x).ToList() : new List<char>()));
        }
        return vysledek.OrderBy(x => x.Cislo).ToList();
    }

    private static char PismenoZ(object? o)
    {
        try
        {
            var c = o switch { char ch => ch, ushort u => (char)u, string s when s.Length > 0 => s[0], _ => '\0' };
            return char.IsLetter(c) ? char.ToUpperInvariant(c) : '\0';
        }
        catch { return '\0'; }
    }

    private static string ProvozniStav(ushort s) => s switch
    {
        2 => "", 0xD010 => "přechodná chyba", 3 => "degradovaný", 5 => "předpověď selhání", 6 => "chyba", 10 => "zastavený",
        11 => "v servisu", 13 => "ztracená komunikace", 15 => "nedostupný", 0xD00D => "mimo provoz",
        0xD011 => "nespolehlivý", 0xD012 => "nesprávný firmware", _ => ""
    };

    // ================== SMART a čítače ==================

    public static async Task<Dictionary<int, Citace>> CitaceAsync(CancellationToken ct)
    {
        var radky = new List<string>();
        var v = new Dictionary<int, Citace>();
        try
        {
            await ProcessRunner.PowerShellAsync(
                "Get-PhysicalDisk | ForEach-Object { $r = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue; " +
                "'K;{0};{1};{2};{3};{4};{5};{6};{7};{8};{9}' -f $_.DeviceId,$r.Temperature,$r.TemperatureMax,$r.Wear,$r.PowerOnHours," +
                "$r.StartStopCycleCount,$r.ReadErrorsTotal,$r.ReadErrorsUncorrected,$r.WriteErrorsTotal,$r.WriteErrorsUncorrected }",
                new Sber(radky), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        foreach (var r in radky)
        {
            var c = r.Split(';');
            if (c.Length != 11 || c[0] != "K" || !int.TryParse(c[1], out var id)) continue;
            int I(string x, int def = 0) => int.TryParse(x, out var q) ? q : def;
            long N(string x) => long.TryParse(x, out var q) ? q : 0;
            v[id] = new Citace(I(c[2]), I(c[3]), c[4].Length > 0 ? I(c[4], -1) : -1, N(c[5]), N(c[6]), N(c[7]), N(c[8]), N(c[9]), N(c[10]));
        }
        return v;
    }

    /// <summary>Klasické SMART atributy (SATA/ATA) z root\wmi – klíčem je PNP ID disku.</summary>
    public static (Dictionary<string, List<AtributSmart>> Atributy, Dictionary<string, bool> Predikce) SmartAtributy()
    {
        var atr = new Dictionary<string, List<AtributSmart>>(StringComparer.OrdinalIgnoreCase);
        var pred = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var prahy = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Wmi("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds", @"root\wmi"))
        {
            if (t["VendorSpecific"] is not byte[] b) continue;
            var mapa = new Dictionary<int, int>();
            for (var i = 2; i + 12 <= b.Length; i += 12)
                if (b[i] != 0) mapa[b[i]] = b[i + 1];
            prahy[S(t["InstanceName"])] = mapa;
        }
        foreach (var d in Wmi("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData", @"root\wmi"))
        {
            if (d["VendorSpecific"] is not byte[] b) continue;
            var inst = S(d["InstanceName"]);
            prahy.TryGetValue(inst, out var pr);
            var l = new List<AtributSmart>();
            for (var i = 2; i + 12 <= b.Length; i += 12)
            {
                int id = b[i];
                if (id == 0) continue;
                long raw = 0;
                for (var k = 5; k >= 0; k--) raw = (raw << 8) | b[i + 5 + k];
                l.Add(new AtributSmart(id, b[i + 3], b[i + 4], pr != null && pr.TryGetValue(id, out var p) ? p : 0, raw));
            }
            atr[inst] = l;
        }
        foreach (var s in Wmi("SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus", @"root\wmi"))
            pred[S(s["InstanceName"])] = s["PredictFailure"] is bool pf && pf;
        return (atr, pred);
    }

    /// <summary>InstanceName v root\wmi = PNP ID disku + „_0“.</summary>
    private static T? PodlePnp<T>(Dictionary<string, T> d, string pnp)
    {
        if (pnp.Length == 0) return default;
        foreach (var (k, v) in d)
            if (k.StartsWith(pnp, StringComparison.OrdinalIgnoreCase)) return v;
        return default;
    }

    private static readonly Dictionary<int, (string Nazev, string Vysvetleni)> NazvyAtributu = new()
    {
        [1] = ("Chybovost čtení", "U disků Seagate je vysoká surová hodnota normální – rozhoduje normalizovaná hodnota vůči prahu."),
        [2] = ("Výkon průchodnosti", ""),
        [3] = ("Doba roztočení", "Jak dlouho trvá roztočit plotny."),
        [4] = ("Počet startů a zastavení", ""),
        [5] = ("Přemapované sektory", "Vadné sektory, které disk nahradil rezervou. Jakékoli číslo nad 0 znamená, že povrch se začal poškozovat – pokud roste, disk brzy selže."),
        [7] = ("Chybovost vyhledávání", "U Seagate je vysoká surová hodnota normální."),
        [8] = ("Výkon vyhledávání", ""),
        [9] = ("Hodiny v provozu", ""),
        [10] = ("Opakované roztáčení", "Disk se nedokázal napoprvé roztočit – mechanická závada nebo slabý zdroj."),
        [11] = ("Opakovaná kalibrace", ""),
        [12] = ("Počet zapnutí", ""),
        [170] = ("Rezervní bloky (SSD)", ""),
        [171] = ("Chyby programování (SSD)", ""),
        [172] = ("Chyby mazání (SSD)", ""),
        [173] = ("Vyrovnání opotřebení (SSD)", ""),
        [174] = ("Neočekávaná ztráta napájení", "Počítač byl vypnut tlačítkem nebo vypadl proud."),
        [177] = ("Vyrovnání opotřebení (SSD)", "Normalizovaná hodnota ukazuje zbývající životnost buněk."),
        [179] = ("Použité rezervní bloky (SSD)", ""),
        [181] = ("Chyby programování (SSD)", ""),
        [182] = ("Chyby mazání (SSD)", ""),
        [183] = ("Snížení rychlosti SATA", "Řadič musel snížit rychlost rozhraní – často vadný kabel."),
        [184] = ("Chyby end-to-end", "Data se poškodila uvnitř disku (cache) – vážná závada elektroniky."),
        [187] = ("Neopravitelné chyby", "Chyby, které disk nedokázal opravit ani pomocí ECC. Nad 0 = riziko ztráty dat."),
        [188] = ("Vypršení příkazů", "Disk nestihl odpovědět – vadné napájení, kabel nebo disk."),
        [189] = ("Zápisy s vysokou hlavou", ""),
        [190] = ("Teplota proudění vzduchu", ""),
        [191] = ("Otřesy (G-sensor)", "Počet zaznamenaných nárazů – u notebooků upozorňuje na pády."),
        [192] = ("Nouzová parkování hlav", "Vypnutí bez řádného ukončení."),
        [193] = ("Cykly parkování hlav", ""),
        [194] = ("Teplota", ""),
        [195] = ("Opravené chyby ECC", ""),
        [196] = ("Události přemapování", "Kolikrát disk přesouval data z vadných míst."),
        [197] = ("Nestabilní (čekající) sektory", "Sektory, které nejdou přečíst a čekají na přemapování. Nad 0 = část dat může být nečitelná."),
        [198] = ("Neopravitelné sektory", "Sektory, které se při offline testu nepodařilo přečíst. Nad 0 = vážné poškození povrchu."),
        [199] = ("Chyby přenosu (CRC)", "Chyby na kabelu mezi diskem a deskou. Pokud roste, vyměňte SATA kabel – disk sám je nejspíš v pořádku."),
        [200] = ("Chyby zápisu", ""),
        [202] = ("Zbývající životnost (SSD)", ""),
        [220] = ("Posun ploten", ""),
        [222] = ("Hodiny s nahranými hlavami", ""),
        [225] = ("Cykly hlav", ""),
        [230] = ("Stav hlav / životnost", ""),
        [231] = ("Životnost SSD", "Normalizovaná hodnota = zbývající životnost v %."),
        [232] = ("Dostupná rezerva (SSD)", ""),
        [233] = ("Opotřebení média (SSD)", "Normalizovaná hodnota klesá od 100 k 0 s opotřebením paměti."),
        [234] = ("Celkem přečteno/zapsáno", ""),
        [235] = ("Ztráty napájení (SSD)", ""),
        [240] = ("Hodiny létání hlav", ""),
        [241] = ("Celkem zapsáno", "Množství zapsaných dat za život disku."),
        [242] = ("Celkem přečteno", ""),
    };

    /// <summary>Atributy, u kterých surová hodnota nad 0 znamená poškození.</summary>
    private static readonly int[] Kriticke = { 5, 10, 184, 187, 196, 197, 198 };

    // ================== Hodnocení kvality ==================

    private sealed record Hodnoceni(int Skore, string Stupen, Stav Stav, List<(Stav Stav, string Text)> Doporuceni, List<string> Duvody);

    private static Hodnoceni Ohodnot(InfoDisku d, Citace? c, List<AtributSmart>? atr, bool? predikce)
    {
        var skore = 100;
        var strop = 100;
        var dop = new List<(Stav, string)>();
        var duvody = new List<string>();
        long Raw(int id) => atr?.FirstOrDefault(a => a.Id == id)?.Raw ?? 0;

        if (d.Zdravi == 2) { strop = 15; duvody.Add("Windows hlásí PORUCHU disku"); }
        else if (d.Zdravi == 1) { strop = 55; duvody.Add("Windows hlásí varování o stavu disku"); }
        if (predikce == true) { strop = Math.Min(strop, 15); duvody.Add("disk sám předpovídá blížící se selhání (SMART)"); }

        var realok = Raw(5);
        if (realok > 0)
        {
            skore -= realok >= 100 ? 50 : realok >= 10 ? 30 : 15;
            duvody.Add($"{realok} přemapovaných sektorů");
        }
        var cekajici = Raw(197);
        if (cekajici > 0) { skore -= cekajici >= 10 ? 40 : 25; duvody.Add($"{cekajici} nestabilních sektorů"); }
        var neopr = Raw(198);
        if (neopr > 0) { skore -= 25; duvody.Add($"{neopr} neopravitelných sektorů"); }
        if (Raw(187) > 0) { skore -= 15; duvody.Add($"{Raw(187)} neopravitelných chyb"); }
        if (Raw(184) > 0) { skore -= 25; duvody.Add("chyby uvnitř disku (end-to-end)"); }
        if (Raw(10) > 0) { skore -= 15; duvody.Add("problémy s roztočením"); }
        var crc = Raw(199);
        if (crc > 0) { skore -= 5; duvody.Add($"{crc} chyb přenosu po kabelu"); }
        foreach (var a in atr ?? new List<AtributSmart>())
            if (a.Prah > 0 && a.Hodnota > 0 && a.Hodnota <= a.Prah)
            {
                strop = Math.Min(strop, 20);
                duvody.Add($"atribut „{NazevAtributu(a.Id)}“ je pod prahem výrobce");
            }

        if (c != null)
        {
            if (c.NeopravCteni > 0) { skore -= 20; duvody.Add($"{c.NeopravCteni} neopravitelných chyb čtení"); }
            if (c.NeopravZapisu > 0) { skore -= 20; duvody.Add($"{c.NeopravZapisu} neopravitelných chyb zápisu"); }
            if (c.Opotrebeni >= 0)
            {
                if (c.Opotrebeni >= 100) { strop = Math.Min(strop, 25); duvody.Add("SSD je na konci životnosti"); }
                else if (c.Opotrebeni >= 90) { skore -= 40; duvody.Add($"opotřebení {c.Opotrebeni} %"); }
                else if (c.Opotrebeni >= 70) { skore -= 20; duvody.Add($"opotřebení {c.Opotrebeni} %"); }
                else if (c.Opotrebeni >= 50) { skore -= 10; duvody.Add($"opotřebení {c.Opotrebeni} %"); }
            }
            var limit = d.Ssd ? 70 : 55;
            if (c.Teplota >= limit + 10) { skore -= 15; duvody.Add($"vysoká teplota {c.Teplota} °C"); }
            else if (c.Teplota >= limit) { skore -= 5; duvody.Add($"zvýšená teplota {c.Teplota} °C"); }
            if (!d.Ssd && c.Hodiny > 40000) { skore -= 10; duvody.Add($"vysoké stáří ({c.Hodiny:N0} h provozu)"); }
        }

        skore = Math.Clamp(Math.Min(skore, strop), 0, 100);
        var (stupen, stav) = skore switch
        {
            >= 90 => ("výborný", Stav.Ok),
            >= 75 => ("dobrý", Stav.Ok),
            >= 50 => ("opotřebený – sledujte", Stav.Varovani),
            >= 25 => ("špatný – zálohujte data", Stav.Chyba),
            _ => ("kritický – hrozí ztráta dat", Stav.Chyba)
        };

        // --- doporučení ---
        if (skore < 50)
            dop.Add((Stav.Chyba, "Okamžitě zálohujte důležitá data (dokumenty, fotky) na jiný disk nebo do cloudu a naplánujte výměnu disku."));
        else if (skore < 75)
            dop.Add((Stav.Varovani, "Pravidelně zálohujte a stav disku zkontrolujte znovu za pár týdnů. Pokud se hodnoty zhoršují, disk vyměňte."));
        if (realok > 0 || cekajici > 0 || neopr > 0)
            dop.Add((Stav.Varovani, "Spusťte „Test povrchu“ (úplný režim) – ukáže, kolik oblastí disku nejde přečíst."));
        if (crc > 0)
            dop.Add((Stav.Info, "Chyby přenosu: vyměňte nebo přepojte SATA kabel (a zkuste jiný port na desce)."));
        if (c != null && c.Teplota >= (d.Ssd ? 70 : 55))
            dop.Add((Stav.Varovani, d.Ssd
                ? "SSD se přehřívá – zkontrolujte proudění vzduchu, u NVMe zvažte chladič. Při přehřátí se zpomaluje."
                : "HDD je příliš teplý – vyčistěte prach, zkontrolujte ventilátory. Teplo zkracuje životnost disku."));
        if (c is { Opotrebeni: >= 70 })
            dop.Add((Stav.Varovani, "SSD má spotřebovanou většinu životnosti zápisů – počítejte s jeho výměnou."));
        if (!d.Ssd && d.Pismena.Contains((Environment.GetEnvironmentVariable("SystemDrive") ?? "C:")[0]))
            dop.Add((Stav.Info, "Systém běží z klasického HDD – výměna za SSD zrychlí start a práci počítače několikanásobně."));
        if (dop.Count == 0)
            dop.Add((Stav.Ok, "Disk je v pořádku. Stačí běžné zálohování a kontrola jednou za čas."));

        return new Hodnoceni(skore, stupen, stav, dop, duvody);
    }

    private static string NazevAtributu(int id) => NazvyAtributu.TryGetValue(id, out var n) ? n.Nazev : $"Atribut výrobce {id}";

    private static string HodnotaAtributu(AtributSmart a) => a.Id switch
    {
        194 or 190 => $"{a.Raw & 0xFF} °C",
        9 => $"{a.Raw & 0xFFFFFFFF:N0} h",
        241 or 242 => $"{(a.Raw * 512) / 1e12:0.##} TB (pokud disk počítá v sektorech 512 B)",
        _ => $"{a.Raw:N0}"
    };

    public static async Task<Zprava> KvalitaAsync(IProgress<string> log, CancellationToken ct)
    {
        log.Report("Načítám seznam disků…");
        var disky = SeznamDisku();
        log.Report("Načítám spolehlivostní čítače (SMART)…");
        var citace = await CitaceAsync(ct);
        var (atributy, predikce) = SmartAtributy();
        log.Report($"Nalezeno disků: {disky.Count}");

        var z = new Zprava("Kvalita a zdraví disků") { Obnovit = KvalitaAsync };
        var souhrn = new List<string>();
        var nejhorsi = Stav.Ok;

        foreach (var d in disky)
        {
            citace.TryGetValue(d.Cislo, out var c);
            var atr = PodlePnp(atributy, d.Pnp);
            bool? pf = d.Pnp.Length > 0 && predikce.Keys.Any(k => k.StartsWith(d.Pnp, StringComparison.OrdinalIgnoreCase))
                ? PodlePnp(predikce, d.Pnp) : null;
            var h = Ohodnot(d, c, atr, pf);
            if (h.Stav > nejhorsi && h.Stav != Stav.Info) nejhorsi = h.Stav;
            souhrn.Add($"Disk {d.Cislo}: {h.Skore}/100 ({h.Stupen.Split(' ')[0]})");

            var s = z.PridejSekci($"Disk {d.Cislo}: {d.Nazev}");
            s.Popis = $"{d.Typ} · {Gb(d.Velikost)}" + (d.Pismena.Count > 0 ? " · jednotky " + string.Join(", ", d.Pismena.Select(x => x + ":")) : "");
            s.Pridej("Celkové hodnocení kvality", $"{h.Skore} / 100 – {h.Stupen}", h.Stav,
                h.Duvody.Count > 0 ? "Snížení skóre: " + string.Join("; ", h.Duvody) + "."
                                   : "Žádné známky poškození ani opotřebení.");
            s.Pridej("Stav podle Windows", d.Zdravi switch { 0 => "v pořádku", 1 => "VAROVÁNÍ", 2 => "PORUCHA", _ => "neznámý" },
                d.Zdravi switch { 0 => Stav.Ok, 1 => Stav.Varovani, 2 => Stav.Chyba, _ => Stav.Info },
                d.Provozni.Length > 0 ? "Provozní stav: " + d.Provozni : "");
            if (pf != null)
                s.Pridej("Předpověď selhání (SMART)", pf.Value ? "DISK HLÁSÍ BLÍŽÍCÍ SE SELHÁNÍ" : "nehlásí problém",
                    pf.Value ? Stav.Chyba : Stav.Ok);
            if (c != null)
            {
                if (c.Teplota > 0)
                {
                    var limit = d.Ssd ? 70 : 55;
                    s.Pridej("Teplota", $"{c.Teplota} °C" + (c.TeplotaMax > 0 ? $" (nejvyšší naměřená {c.TeplotaMax} °C)" : ""),
                        c.Teplota >= limit + 10 ? Stav.Chyba : c.Teplota >= limit ? Stav.Varovani : Stav.Ok,
                        d.Ssd ? "SSD: normálně 30–60 °C, nad 70 °C se zpomaluje." : "HDD: ideálně 25–45 °C, nad 55 °C se zkracuje životnost.");
                }
                if (c.Opotrebeni >= 0 && d.Ssd)
                    s.Pridej("Opotřebení SSD", $"{c.Opotrebeni} % spotřebováno · zbývá ≈ {Math.Max(0, 100 - c.Opotrebeni)} %",
                        c.Opotrebeni >= 90 ? Stav.Chyba : c.Opotrebeni >= 70 ? Stav.Varovani : Stav.Ok,
                        "Kolik z výrobcem garantovaného množství zápisů už disk spotřeboval.");
                if (c.Hodiny > 0)
                    s.Pridej("Doba provozu", $"{c.Hodiny:N0} h (≈ {c.Hodiny / 24.0 / 365:0.#} roku nepřetržitě)");
                if (c.Zapnuti > 0) s.Pridej("Počet zapnutí", $"{c.Zapnuti:N0}");
                s.Pridej("Neopravitelné chyby čtení / zápisu", $"{c.NeopravCteni} / {c.NeopravZapisu}",
                    c.NeopravCteni + c.NeopravZapisu > 0 ? Stav.Chyba : Stav.Ok,
                    c.ChybyCteni + c.ChybyZapisu > 0 ? $"Opravené chyby (běžné, disk si je opravil sám): čtení {c.ChybyCteni}, zápis {c.ChybyZapisu}." : "");
            }
            if (atr != null)
                foreach (var id in Kriticke.Append(199))
                    if (atr.FirstOrDefault(q => q.Id == id) is { } a)
                        s.Pridej(NazevAtributu(id), $"{a.Raw:N0}", a.Raw > 0 ? (id == 199 ? Stav.Varovani : Stav.Chyba) : Stav.Ok,
                            NazvyAtributu[id].Vysvetleni);
            if (c == null && atr == null)
                s.Pridej("Údaje SMART", "nedostupné", Stav.Info,
                    "Disk (nebo USB řadič / RAID) údaje o zdraví Windows nesděluje. Použijte Test povrchu.");
            s.Pridej("Model / sériové číslo / firmware", $"{d.Nazev} · {d.Serial} · {d.Firmware}".Trim(' ', '·'));
            if (d.Otacky > 0) s.Pridej("Otáčky", $"{d.Otacky} ot./min");

            var dop = z.PridejSekci($"Doporučení – disk {d.Cislo}");
            foreach (var (st, text) in h.Doporuceni) dop.Pridej(text, "", st);

            if (atr is { Count: > 0 })
            {
                var sa = z.PridejSekci($"Všechny atributy SMART – disk {d.Cislo}");
                sa.Sbalena = true;
                sa.Popis = "Hodnota / nejhorší / práh = normalizované hodnoty výrobce (čím vyšší, tím lépe; pod prahem = porucha). Surová hodnota = skutečné číslo.";
                foreach (var a in atr)
                {
                    var spatny = a.Prah > 0 && a.Hodnota > 0 && a.Hodnota <= a.Prah;
                    var krit = Kriticke.Contains(a.Id) && a.Raw > 0;
                    sa.Pridej($"{a.Id:X2}  {NazevAtributu(a.Id)}", $"{HodnotaAtributu(a)}  ·  {a.Hodnota} / {a.Nejhorsi} / {a.Prah}",
                        spatny || krit ? Stav.Chyba : a.Id == 199 && a.Raw > 0 ? Stav.Varovani : null,
                        NazvyAtributu.TryGetValue(a.Id, out var n) ? n.Vysvetleni : "");
                }
            }
        }

        if (disky.Count == 0)
        {
            z.Stav = Stav.Varovani;
            z.Shrnuti = "Nepodařilo se načíst žádný disk.";
            return z;
        }
        var vys = z.PridejSekci("Jak číst hodnocení");
        vys.Sbalena = true;
        vys.Pridej("90–100 výborný · 75–89 dobrý", "", Stav.Ok, "Disk je zdravý.");
        vys.Pridej("50–74 opotřebený", "", Stav.Varovani, "Disk funguje, ale má první známky opotřebení nebo poškození. Zálohujte a sledujte.");
        vys.Pridej("0–49 špatný / kritický", "", Stav.Chyba, "Vysoké riziko ztráty dat. Zálohujte hned a disk vyměňte.");
        vys.Pridej("Skóre počítá Konzolák Servis z údajů SMART, chyb a teploty. Testy jsou jen čtecí, data na disku se nemění.");

        z.Stav = nejhorsi;
        z.Shrnuti = string.Join(" · ", souhrn);
        return z;
    }

    internal sealed class Sber(List<string> l, IProgress<string>? dal = null) : IProgress<string>
    {
        public void Report(string v) { lock (l) l.Add(v); dal?.Report(v); }
    }
}
