## 0.18
- Oprava: „Znovu zapnout všechny ochrany" teď skutečně zapne „blokování potenciálně nežádoucích aplikací" v Ochraně na základě reputace. Dřív se nastavovala jiná (antivirová) volba PUA, takže varování ve Windows nezmizelo. Nově se přepíná správný přepínač SmartScreen (SmartScreenPuaEnabled).
- V přehledu ochran je nově vidět i verze aplikace (pro kontrolu, že máte aktuální build).
- Oprava: aplikace má na hlavním panelu (taskbaru) i v oknech správnou ikonu Konzolák (dřív se u přihlašovacího okna zobrazovala obecná ikona).

## 0.17
- Vypnutí ochran: „Znovu zapnout všechny ochrany" nyní obnoví i Ochranu na základě reputace (blokování potenciálně nežádoucích aplikací – PUA), ne jen Defender, firewall a SmartScreen. Vypnutí a zapnutí je symetrické. V přehledu přibyl stav PUA.

## 0.16
- Oprava: akce využívající PowerShell (např. vypnutí ochran, čištění, opravy) fungují i když je aplikace spuštěná ze složky na OneDrive (Plocha, Dokumenty). Dřív hlásily „Přístup byl odepřen".
- Sjednocen název v oknech a hláškách na „Konzolak service app".

## 0.15
- Oprava: po zadání správného hesla se aplikace spustí (hlavní okno se nyní správně otevře).

## 0.14
- Spuštění aplikace je nově chráněné heslem – bez správného hesla se nástroj nespustí (ochrana proti tomu, aby ho spustil sám zákazník). V Nástrojích → „Změnit heslo pro spuštění" si můžeš nastavit vlastní heslo, které se uloží jen do daného počítače.
- Aplikace se přejmenovala na „Konzolak service app" (název okna i souboru .exe).
- Po spuštění nové verze se automaticky ukáže info stránka s přehledem změn. Kontrola aktualizací probíhá po startu na pozadí.

## 0.13
- Nástroje → Dočasně vypnout ochrany (instalace programů): jedním tlačítkem dočasně vypne ochranu Defenderu v reálném čase, firewall ve všech profilech i SmartScreen pro aplikace, když brání instalaci důvěryhodného programu. Dalším tlačítkem se stejně snadno vše zase zapne.
- Před vypnutím srozumitelné české varování, že jde o dočasné bezpečnostní riziko, a doporučení ochrany po instalaci hned obnovit.
- Přehled aktuálního stavu všech ochran. Pokud je zapnutá Ochrana proti neoprávněným změnám (Tamper Protection), aplikace to pozná a poradí, jak ji ručně vypnout. Jiné antiviry než Defender aplikace nevypíná, ale upozorní na ně a poradí je pozastavit ručně.

## 0.12
- Benchmark PC: notebooky se dvěma grafikami (např. Intel Arc + NVIDIA RTX) – každá grafika se testuje zvlášť s vynucenou volbou výkonné / úsporné karty (nastavení Windows se po testu vrátí). U výsledku je vždy uvedeno, která karta byla testována, propustnost její video paměti a porovnání s obvyklými výsledky pro každou kartu.
- Odstraněno zavádějící „herní“ skóre WinSAT (novější Windows ho vždy hlásí jako 9,9).
- Pruhy u skóre a rychlostí jsou v barvách aplikace (zelená / žlutá / červená podle hodnocení, jinak zlatá), i v uloženém HTML.

## 0.11
- Nový Benchmark PC (Diagnostika): vlastní test procesoru na jednom jádře i všech jádrech + test Windows WinSAT (procesor, paměť RAM, grafika, disk) se skóre 1,0–9,9 a hodnocením slabé / průměrné / dobré / výborné.
- Porovnání s obvyklými výsledky podle hardwaru (třída procesoru, typ paměti DDR3/4/5, typ disku HDD/SSD/NVMe, integrovaná nebo samostatná grafika) s možnými příčinami, když je výsledek pod očekáváním. Tlačítko „Porovnat na internetu“ pro konkrétní model.
- Historie testů: každý test se ukládá do Dokumenty\Konzolák Servis\Historie (jeden soubor na test), v aplikaci porovnání s minulým testem a tlačítko pro otevření složky.
- Doporučení podle nejslabší části počítače.

## 0.10
- Test rychlosti internetu: správně pozná, přes který adaptér test skutečně běží (podle směrování Windows). Notebook připojený kabelem už neukazuje Wi-Fi; síla Wi-Fi signálu se zobrazí jen při testu přes Wi-Fi.
- Sekce Připojení: IP adresa, brána, DNS servery, DHCP/statická IP, MAC adresa a kompletní výpis ipconfig /all (rozbalovací).
- Úvodní stránka: karta Síť ukazuje skutečně používané připojení.

## 0.9
- Disky → Kvalita a zdraví disků: skóre 0–100 pro každý disk (výborný / dobrý / opotřebený / špatný / kritický) s vysvětlením, proč skóre kleslo.
- SMART srozumitelně: přemapované, nestabilní a neopravitelné sektory, chyby kabelu, teplota, opotřebení SSD, doba provozu, předpověď selhání. Kompletní tabulka atributů SMART s českými názvy a vysvětlením.
- Doporučení ke každému disku (zálohovat, vyměnit kabel, chladit, vyměnit HDD za SSD…).
- Test povrchu (jen čtení): rychlý nebo úplný režim, živá mapa bloků během testu (zelená / žlutá pomalé / červená vadné), seznam vadných míst. Test lze kdykoli zastavit.
- Rychlost disku: sekvenční čtení na 5 místech disku, přístupová doba, zápis do dočasného souboru, porovnání s obvyklou rychlostí daného typu disku.
- Kontrola a oprava disků (chkdsk): všechny jednotky naráz, čitelný výsledek, vadné sektory, tlačítka opravy (/f, /r, oprava při restartu) – vždy s potvrzením.
- Mapa povrchu se ukládá i do HTML/TXT výsledku.

## 0.8
- Hardware → Výpis zařízení: všechna zařízení po kategoriích jako ve Správci zařízení (Baterie, Bluetooth, Klávesnice, Monitory, Síťové adaptéry…), kategorie se rozbalují kliknutím.
- U každého zařízení výrobce, ovladač (poskytovatel, verze, datum), digitální podpis, INF soubor a ID zařízení.
- Zařízení s problémem jsou nahoře, s vysvětlením chyby, postupem a tlačítkem Správce zařízení.
- Výpis jde uložit do HTML/TXT (uloží se kompletně, včetně sbalených kategorií).

## 0.7
- Karty na úvodní stránce mají tlačítko „Podrobnosti“ s kompletním výpisem.
- Paměť RAM do detailu: každý modul zvlášť (slot, kanál, výrobce, part number, sériové číslo, kapacita, typ DDR, jmenovitý a aktuální takt, napětí, ECC), počet volných slotů, maximální podporovaná kapacita, režim single/dual-channel, kontrola XMP/EXPO a stejných modulů + doporučení.
- Procesor: aktivní jádra, aktuální takt, cache L2/L3, napětí, virtualizace v BIOSu, ID procesoru.
- Grafika: výrobce, čip, ovladač s upozorněním na starý ovladač, barevná hloubka, max. obnovovací frekvence.
- Nová karta Monitory: model, úhlopříčka, sériové číslo, rok výroby.
- Disky: teplota, opotřebení SSD, hodiny v provozu, počet zapnutí, chyby čtení/zápisu, sériové číslo, firmware.
- Deska a BIOS: revize, sériové číslo / Service Tag, typ skříně, stáří BIOSu, TPM, Secure Boot.
- Systém: edice, přesné sestavení, ID produktu, doména / pracovní skupina.
- Uložený přehled (HTML/TXT) obsahuje i všechny podrobnosti.

## 0.6
- Nová úvodní stránka „Přehled PC“: systém, procesor, grafická karta (včetně skutečné velikosti paměti), operační paměť (typ DDR, takt, obsazené sloty, upozornění na single-channel), základní deska a BIOS/UEFI, disky (SSD/HDD, NVMe/SATA, stav), volné místo na oddílech, síť, baterie a teploty (kde je deska poskytuje).
- Živé vytížení procesoru a paměti a doba běhu od posledního restartu (upozornění, když PC běží déle než 14 dní).
- Přehled jde uložit tlačítkem „Uložit výsledek…“ do HTML nebo TXT.

## 0.5
- Každé tlačítko nápravy teď ukáže výsledek: zelené nebo červené oznámení nahoře, u chyby i okno s vysvětlením.
- Po nápravě se příslušná kontrola sama spustí znovu a ukáže nový výsledek se srovnáním „před nápravou / po nápravě“ (u Kompletní diagnostiky se celá diagnostika provede znovu).
- Restart služby ověří, že služba opravdu běží; u služeb, které se spouštějí jen na vyžádání (např. MapsBroker), vysvětlí, že samovolné ukončení je normální.
- Základní kontroly systému: u neúspěšné kontroly důvod (provázaný s nalezeným problémem), co to znamená, jak to ověřit a tlačítka nápravy (restart služby, otevření Služeb, Prohlížeče událostí, Správce zařízení…).
- Nový test rychlosti internetu: stahování, odesílání, ping, kolísání, ztráta paketů, síla Wi-Fi a doporučení. Zdarma, bez účtu.
- Kontrola aktualizací řekne, proč nejsou nové verze dostupné.

## 0.4
- Nová sekce Nástroje Windows: God Mode a rychlé spuštění 30 systémových nástrojů (Správce zařízení, Správa disků, msconfig, registry, Prohlížeč událostí, Monitor spolehlivosti, Monitor prostředků, Plánovač úloh, test RAM, dxdiag, příkazový řádek jako správce…).
- Restart do BIOSu/UEFI a do pokročilého spuštění (nouzový režim, oprava spuštění).
- Diagnostika – Zabezpečení a licence: antivirus a jeho definice, Windows Defender (aktualizace definic, rychlá kontrola), firewall (zapnutí jedním klikem), stáří aktualizací Windows, UAC, Secure Boot, TPM, BitLocker (upozornění na obnovovací klíč), aktivace Windows a OEM klíč z BIOSu.
- Diagnostika – Zařízení a ovladače: zařízení s vykřičníkem, vysvětlení kódu chyby a doporučený postup.
- Opravy – Záloha ovladačů do složky (pro obnovu po reinstalaci Windows).

## 0.3
- Nová sekce Optimalizace s tlačítky nápravy přímo u každé položky.
- Programy po spuštění: vypnutí / zapnutí jedním klikem (stejně jako ve Správci úloh), u každého programu popis, doporučení a vliv na rychlost startu. Tlačítko „Vypnout doporučené“.
- Služby Windows: telemetrie, Xbox, fax, vzdálený registr, indexování, SysMain a další – vysvětlení, doporučený režim a tlačítko „Vrátit původní“.
- Výkon a soukromí: plán napájení podle typu PC (notebook / stolní), hibernace, nahrávání her na pozadí, herní režim, vizuální efekty, rychlé spuštění, telemetrie, reklamní ID, tipy a reklamy ve Windows.
- Všechny změny jsou vratné – původní hodnoty se ukládají do zálohy.
- Diagnostika: u havarované služby tlačítko „Restartovat službu“.

## 0.2
- Výsledky se zobrazují přímo v aplikaci, přehledně a česky (záložky Výsledek / Podrobný log).
- Tlačítko „Uložit výsledek…“ – uložení do HTML (v barvách Konzolák, s logem, dá se vytisknout) nebo TXT.
- Tlačítko „Uložit celý log…“ s volbou místa a formátu.
- Zpráva o baterii: zdraví baterie v %, původní a aktuální kapacita, počet nabíjecích cyklů.
- Přehled hardwaru jako přehledná tabulka, barevné hodnocení volného místa na discích.
- Kompletní diagnostika (perfmon): nalezené problémy, základní kontroly systému a vytížení prostředků.
- Číslo verze v názvu souboru a v titulku okna.
- Automatická kontrola nové verze při spuštění a tlačítko „Aktualizovat“.
- Ikona aplikace v barvách Konzolák.
- Oprava: tlačítka ve spodní liště se už nepřekrývají s textem.

## 0.1
- První verze: diagnostika přes perfmon /report, disky (SMART, chkdsk, optimalizace), opravy (DISM, SFC, Windows Update), čištění, síť, přehled hardwaru a ukládání logů.
