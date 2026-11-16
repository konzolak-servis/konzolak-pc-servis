using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using KonzolakServis.Models;
using KonzolakServis.Services;
using Microsoft.Win32;

namespace KonzolakServis;

public partial class MainWindow : Window
{
    private readonly LogService _log = new();
    private CancellationTokenSource? _cts;
    private Zprava? _posledni;
    private List<string>? _zachyceno;   // výstup právě běžící akce (pro zobrazení v aplikaci)

    private static readonly (Kategorie Kat, string Nazev)[] Sekce =
    {
        (Kategorie.Prehled, "Přehled PC"),
        (Kategorie.Diagnostika, "Diagnostika"),
        (Kategorie.Optimalizace, "Optimalizace"),
        (Kategorie.Disky, "Disky"),
        (Kategorie.Opravy, "Opravy systému"),
        (Kategorie.Cisteni, "Čištění"),
        (Kategorie.Sit, "Síť"),
        (Kategorie.HwInfo, "Hardware"),
        (Kategorie.Nastroje, "Nástroje Windows"),
    };

    public MainWindow()
    {
        InitializeComponent();
        Title = $"Konzolák Servis v{AktualizaceService.VerzeText}";
        VerzeInfo.Text = $"Verze {AktualizaceService.VerzeText}";
        _log.Radek += l => Dispatcher.Invoke(() => { Konzole.AppendText(l + Environment.NewLine); Konzole.ScrollToEnd(); });
        DiskService.ZivaMapa += (bunky, text) => Dispatcher.InvokeAsync(() => ZivaMapa(bunky, text));

        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        AdminInfo.Text = admin ? "Spuštěno jako správce ✔" : "⚠ Bez práv správce – opravy nebudou fungovat";

        foreach (var (kat, nazev) in Sekce)
        {
            var rb = new RadioButton { Content = nazev, GroupName = "menu", Style = (Style)FindResource("PolozkaMenu") };
            rb.Checked += (_, _) => Zobraz(kat, nazev);
            Menu.Children.Add(rb);
        }
        ((RadioButton)Menu.Children[0]).IsChecked = true;
        _log.Zapis("Konzolák Servis připraven.");
    }

    private void Zobraz(Kategorie kat, string nazev)
    {
        Nadpis.Text = nazev;
        Akce.Children.Clear();
        _zivy?.Stop();
        var prehled = kat == Kategorie.Prehled;
        AkceScroll.Visibility = prehled ? Visibility.Collapsed : Visibility.Visible;
        if (prehled) { Nadpis.Text = $"Přehled PC – {Environment.MachineName}"; _ = ZobrazPrehledAsync(); return; }
        foreach (var a in ServisniKatalog.Vse.Where(a => a.Kategorie == kat))
        {
            var obsah = new StackPanel();
            obsah.Children.Add(new TextBlock { Text = a.Nazev, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            obsah.Children.Add(new TextBlock { Text = a.Popis, TextWrapping = TextWrapping.Wrap, MaxWidth = kat == Kategorie.Nastroje ? 200 : 240,
                Opacity = 0.75, FontSize = 12, Margin = new Thickness(0, 3, 0, 0) });
            var b = new Button { Content = obsah, Style = (Style)FindResource("TlacitkoAkce"),
                Width = kat == Kategorie.Nastroje ? 230 : 270 };
            b.Click += async (_, _) => await SpustAsync(a);
            Akce.Children.Add(b);
        }
    }

    private async Task SpustAsync(ServisniAkce a)
    {
        if (_cts != null) { MessageBox.Show("Počkej, až doběhne předchozí akce.", "Konzolák Servis"); return; }
        if (a.MeniSystem)
        {
            var text = $"Akce „{a.Nazev}“ provede změny v systému.\n{a.Varovani}\n\nPokračovat?";
            if (MessageBox.Show(text, "Konzolák Servis", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }

        _cts = new CancellationTokenSource();
        _zachyceno = new List<string>();
        Prubeh.Visibility = Visibility.Visible; Zrusit.IsEnabled = true; Akce.IsEnabled = false;
        StavText.Text = $"Probíhá: {a.Nazev}…";
        ZobrazPrubeh(a.Nazev);
        _log.Zapis($"===== {a.Nazev} =====");
        var zachyceno = _zachyceno;
        var progress = new Progress<string>(l => { _log.Zapis(l); zachyceno.Add(l); });
        Zprava zprava;
        try
        {
            var v = await Task.Run(() => a.Spustit(progress, _cts.Token));
            await Task.Delay(50);   // nechat doběhnout poslední řádky z Progress
            _log.Zapis($"VÝSLEDEK [{ZpravaExport.StavText(v.Stav)}]: {v.Shrnuti}");
            zprava = v.Zprava ?? ZVystupu(a.Nazev, v, zachyceno);
            StavText.Text = v.Shrnuti;
        }
        catch (OperationCanceledException)
        {
            _log.Zapis("Akce přerušena uživatelem.");
            zprava = ZVystupu(a.Nazev, new VysledekAkce(Stav.Info, "Akce byla přerušena."), zachyceno);
            StavText.Text = "Přerušeno";
        }
        catch (Exception ex)
        {
            _log.Zapis($"CHYBA: {ex.Message}");
            zprava = ZVystupu(a.Nazev, new VysledekAkce(Stav.Chyba, $"Akce selhala: {ex.Message}"), zachyceno);
            StavText.Text = "Chyba – podrobnosti v záložce Podrobný log";
        }
        finally
        {
            _cts.Dispose(); _cts = null; _zachyceno = null;
            Prubeh.Visibility = Visibility.Collapsed; Zrusit.IsEnabled = false; Akce.IsEnabled = true;
        }
        _posledni = zprava;
        UlozitVysledek.IsEnabled = true;
        Vykresli(zprava);
        ZalozkaVysledek.IsChecked = true;
    }

    /// <summary>Akce bez vlastní zprávy: zobrazíme její výstup (bez technických řádků).</summary>
    private static Zprava ZVystupu(string nazev, VysledekAkce v, List<string> radky)
    {
        var z = new Zprava(nazev) { Stav = v.Stav, Shrnuti = v.Shrnuti };
        var s = z.PridejSekci("Výstup");
        foreach (var r in radky)
        {
            var t = r.Trim();
            if (t.Length == 0 || t.StartsWith("> ") || t.StartsWith("Návratový kód")) continue;
            s.Pridej(t.TrimStart('!', ' '), "", t.StartsWith("!") ? Stav.Varovani : null);
        }
        if (s.Radky.Count == 0) z.Sekce.Remove(s);
        return z;
    }

    // ---------- Úvodní stránka: přehled PC ----------

    private System.Windows.Threading.DispatcherTimer? _zivy;
    private List<KartaPrehledu>? _karty;

    private async Task ZobrazPrehledAsync()
    {
        ZalozkaVysledek.IsChecked = true;
        VysledekPanel.Children.Clear();
        VysledekPanel.Children.Add(new TextBlock { Text = "Načítám údaje o počítači…", Foreground = B("TextTlumeny"), FontSize = 14 });
        try { _karty ??= await Task.Run(PrehledService.Nacti); }
        catch (Exception ex)
        {
            VysledekPanel.Children.Clear();
            VysledekPanel.Children.Add(new TextBlock { Text = $"Údaje se nepodařilo načíst: {ex.Message}", Foreground = B("Chyba") });
            return;
        }
        if (Menu.Children.OfType<RadioButton>().FirstOrDefault()?.IsChecked != true) return;   // mezitím přepnuto jinam

        _posledni = PrehledService.DoZpravy(_karty);
        UlozitVysledek.IsEnabled = true;
        VysledekPanel.Children.Clear();

        // Živé dlaždice: procesor a paměť
        var dlazdice = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        var (cpuText, cpuBar) = Dlazdice(dlazdice, "Vytížení procesoru");
        var (ramText, ramBar) = Dlazdice(dlazdice, "Využití paměti");
        var (upText, _) = Dlazdice(dlazdice, "Běží od spuštění", bezPruhu: true);
        VysledekPanel.Children.Add(dlazdice);

        void Obnov()
        {
            var cpu = PrehledService.CpuProcent();
            var (ramP, pouz, celk) = PrehledService.Pamet();
            cpuText.Text = $"{cpu:0} %"; cpuBar.Value = cpu; cpuBar.Foreground = cpu > 85 ? B("Chyba") : B("Akcent");
            ramText.Text = $"{ramP:0} %  ({pouz:0.0} / {celk:0.0} GB)"; ramBar.Value = ramP; ramBar.Foreground = ramP > 85 ? B("Chyba") : B("Akcent");
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            upText.Text = $"{(int)up.TotalDays} d {up.Hours} h {up.Minutes} min";
        }
        PrehledService.CpuProcent();
        Obnov();
        _zivy = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _zivy.Tick += (_, _) => Obnov();
        _zivy.Start();

        // Karty s hardwarem
        var mrizka = new WrapPanel();
        foreach (var k in _karty) mrizka.Children.Add(Karta(k));
        VysledekPanel.Children.Add(mrizka);

        var obnovit = new Button { Content = "Načíst znovu", Style = (Style)FindResource("TlacitkoMale"), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0) };
        obnovit.Click += async (_, _) => { _karty = null; _zivy?.Stop(); await ZobrazPrehledAsync(); };
        VysledekPanel.Children.Add(obnovit);
        StavText.Text = "Přehled načten. Uložit ho můžeš tlačítkem „Uložit výsledek…“.";
    }

    private (TextBlock Text, ProgressBar Bar) Dlazdice(Panel kam, string nadpis, bool bezPruhu = false)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = nadpis, Foreground = B("TextTlumeny"), FontSize = 12.5 });
        var t = new TextBlock { FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 6) };
        sp.Children.Add(t);
        var bar = new ProgressBar { Height = 6, Maximum = 100, Background = B("Panel"), BorderThickness = new Thickness(0), Foreground = B("Akcent") };
        if (!bezPruhu) sp.Children.Add(bar);
        kam.Children.Add(new Border { Background = B("Pozadi"), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 14),
            Margin = new Thickness(0, 0, 12, 12), Width = 260, Child = sp });
        return (t, bar);
    }

    private UIElement Karta(KartaPrehledu k)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = k.Nadpis.ToUpperInvariant(), Foreground = B("Akcent"), FontSize = 11.5, FontWeight = FontWeights.SemiBold });
        sp.Children.Add(new TextBox { Text = k.Hlavni, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsReadOnly = true,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = B("Text"), Margin = new Thickness(0, 2, 0, 8) });
        foreach (var (p, h, st) in k.Radky) sp.Children.Add(RadekKarty(p, h, st, 120));
        var karta = new Border { Background = B("Pozadi"), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 14),
            Margin = new Thickness(0, 0, 12, 12), Width = 400, Child = sp, VerticalAlignment = VerticalAlignment.Top };

        if (k.Detail.Count > 0)
        {
            var detail = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var d in k.Detail)
            {
                detail.Children.Add(new TextBlock { Text = d.Nadpis, Foreground = B("Akcent"), FontWeight = FontWeights.SemiBold,
                    FontSize = 13, Margin = new Thickness(0, 10, 0, 4) });
                foreach (var (p, h, st) in d.Radky) detail.Children.Add(RadekKarty(p, h, st, 170));
            }
            var tl = new Button { Content = "Podrobnosti ▾", Style = (Style)FindResource("TlacitkoMale"), HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 0) };
            tl.Click += (_, _) =>
            {
                var otevreno = detail.Visibility == Visibility.Visible;
                detail.Visibility = otevreno ? Visibility.Collapsed : Visibility.Visible;
                tl.Content = otevreno ? "Podrobnosti ▾" : "Skrýt podrobnosti ▴";
                karta.Width = otevreno ? 400 : 812;   // rozbalená karta přes dvě kolonky
            };
            sp.Children.Add(tl);
            sp.Children.Add(detail);
        }
        return karta;
    }

    private UIElement RadekKarty(string p, string h, Stav? st, double sirkaPopisu)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(sirkaPopisu) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = p, Foreground = B("TextTlumeny"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
        var hod = new TextBox { Text = h, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = st is Stav.Chyba ? B("Chyba") : st is Stav.Varovani ? B("Varovani") : B("Text") };
        Grid.SetColumn(hod, 1);
        g.Children.Add(hod);
        return g;
    }

    // ---------- Vykreslení výsledku ----------

    private Brush B(string klic) => (Brush)FindResource(klic);

    private Brush BarvaStavu(Stav? s) => s switch
    {
        Stav.Ok => B("Ok"), Stav.Varovani => B("Varovani"), Stav.Chyba => B("Chyba"), Stav.Info => B("TextTlumeny"),
        _ => Brushes.Transparent
    };

    private void ZobrazPrubeh(string nazev)
    {
        VysledekPanel.Children.Clear();
        VysledekPanel.Children.Add(new TextBlock { Text = nazev, FontSize = 20, FontWeight = FontWeights.SemiBold });
        VysledekPanel.Children.Add(new TextBlock { Text = "Probíhá… průběh najdeš v záložce Podrobný log.",
            Foreground = B("TextTlumeny"), Margin = new Thickness(0, 6, 0, 0) });
    }

    /// <summary>Mapa povrchu disku: mřížka barevných čtverečků (zelená/žlutá/červená/šedá).</summary>
    private WrapPanel MapaUI(IReadOnlyList<BunkaMapy> bunky, WrapPanel? existujici)
    {
        var wp = existujici ?? new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        for (var i = 0; i < bunky.Count; i++)
        {
            Rectangle r;
            if (i < wp.Children.Count) r = (Rectangle)wp.Children[i];
            else
            {
                r = new Rectangle { Width = 12, Height = 12, Margin = new Thickness(1), RadiusX = 2, RadiusY = 2 };
                wp.Children.Add(r);
            }
            var barva = BarvaStavu(bunky[i].Stav);
            if (!ReferenceEquals(r.Fill, barva)) r.Fill = barva;
            r.Opacity = bunky[i].Stav == Stav.Info ? 0.35 : 1;
            if (r.ToolTip as string != bunky[i].Popis) r.ToolTip = bunky[i].Popis;
        }
        return wp;
    }

    private WrapPanel? _zivaMapa;
    private TextBlock? _zivaMapaText;

    /// <summary>Živé vykreslování mapy během testu povrchu.</summary>
    private void ZivaMapa(IReadOnlyList<BunkaMapy> bunky, string text)
    {
        if (_cts == null) return;
        if (_zivaMapa == null || !VysledekPanel.Children.Contains(_zivaMapa))
        {
            _zivaMapaText = new TextBlock { FontSize = 15, Margin = new Thickness(0, 12, 0, 0), Foreground = B("Akcent") };
            _zivaMapa = MapaUI(bunky, null);
            VysledekPanel.Children.Add(_zivaMapaText);
            VysledekPanel.Children.Add(new Border { Background = B("Pozadi"), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 6, 0, 0), Child = _zivaMapa });
            ZalozkaVysledek.IsChecked = true;
        }
        else MapaUI(bunky, _zivaMapa);
        _zivaMapaText!.Text = text;
    }

    private void Vykresli(Zprava z)
    {
        VysledekPanel.Children.Clear();
        VysledekPanel.Children.Add(new TextBlock { Text = z.Nadpis, FontSize = 20, FontWeight = FontWeights.SemiBold });
        VysledekPanel.Children.Add(new TextBlock { Text = $"{z.Pocitac} · {z.Cas:d. M. yyyy HH:mm}",
            Foreground = B("TextTlumeny"), Margin = new Thickness(0, 2, 0, 10) });

        VysledekPanel.Children.Add(new Border
        {
            Background = B("Pozadi"), BorderBrush = BarvaStavu(z.Stav), BorderThickness = new Thickness(5, 0, 0, 0),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8),
            Child = new TextBlock { Text = z.Shrnuti, FontSize = 15, TextWrapping = TextWrapping.Wrap }
        });

        if (z.HromadneAkce.Count > 0)
        {
            var hp = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var a in z.HromadneAkce) hp.Children.Add(TlacitkoAkce(a, z));
            VysledekPanel.Children.Add(hp);
        }

        foreach (var s in z.Sekce)
        {
            var tabulka = new StackPanel();
            if (s.Mapa != null) tabulka.Children.Add(MapaUI(s.Mapa, null));
            foreach (var r in s.Radky) tabulka.Children.Add(RadekUI(r, z));
            var obal = new Border { Background = B("Pozadi"), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 4, 12, 4), Child = tabulka, Visibility = s.Sbalena ? Visibility.Collapsed : Visibility.Visible };

            var nadpis = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = B("Akcent"),
                Margin = new Thickness(0, s.Sbalena ? 6 : 14, 0, 6), Text = (s.Sbalena ? "▸ " : "") + s.Nadpis };
            if (s.Sbalena)
            {
                nadpis.Cursor = System.Windows.Input.Cursors.Hand;
                nadpis.MouseLeftButtonUp += (_, _) =>
                {
                    var otevrit = obal.Visibility != Visibility.Visible;
                    obal.Visibility = otevrit ? Visibility.Visible : Visibility.Collapsed;
                    nadpis.Text = (otevrit ? "▾ " : "▸ ") + s.Nadpis;
                };
            }
            VysledekPanel.Children.Add(nadpis);
            if (s.Popis.Length > 0)
                VysledekPanel.Children.Add(new TextBlock { Text = s.Popis, TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
                    Foreground = B("TextTlumeny"), Margin = new Thickness(0, 0, 0, 6) });
            VysledekPanel.Children.Add(obal);
        }
        VysledekScroll.ScrollToTop();
    }

    private UIElement RadekUI(Radek r, Zprava z)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = r.Hodnota.Length > 0 ? new GridLength(2, GridUnitType.Star) : new GridLength(1, GridUnitType.Star) });
        if (r.Hodnota.Length > 0) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });

        if (r.Stav != null)
            g.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = BarvaStavu(r.Stav), VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 5, 0, 0) });

        var levy = new StackPanel();
        levy.Children.Add(new TextBox { Text = r.Popis, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Foreground = B("Text"), TextWrapping = TextWrapping.Wrap, FontSize = 14,
            FontWeight = r.Hodnota.Length > 0 ? FontWeights.SemiBold : FontWeights.Normal });
        if (r.Detail.Length > 0)
            levy.Children.Add(new TextBox { Text = r.Detail, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Foreground = B("TextTlumeny"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 3, 12, 0) });
        if (r.Akce.Count > 0)
        {
            var tl = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var a in r.Akce) tl.Children.Add(TlacitkoAkce(a, z));
            levy.Children.Add(tl);
        }
        Grid.SetColumn(levy, 1);
        g.Children.Add(levy);

        if (r.Hodnota.Length > 0)
        {
            var hodnota = new TextBox { Text = r.Hodnota, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Foreground = B("Text"), TextWrapping = TextWrapping.Wrap, FontSize = 14 };
            Grid.SetColumn(hodnota, 2);
            g.Children.Add(hodnota);
        }
        return g;
    }

    /// <summary>Tlačítko nápravy: provede akci, zapíše do logu a obnoví zobrazení s aktuálním stavem.</summary>
    private Button TlacitkoAkce(AkceRadku a, Zprava z)
    {
        var b = new Button { Content = a.Text, Style = (Style)FindResource(a.Doporucena ? "TlacitkoNaprava" : "TlacitkoMale"),
            Margin = new Thickness(0, 0, 8, 4) };
        b.Click += async (_, _) => await ProvedNapravuAsync(a, z, b);
        return b;
    }

    /// <summary>Výsledek nápravy je chyba, když akce vyhodí výjimku nebo text začíná „CHYBA“.</summary>
    private static bool JeChyba(string t) => t.StartsWith("CHYBA", StringComparison.OrdinalIgnoreCase);

    private async Task ProvedNapravuAsync(AkceRadku a, Zprava z, Button tlacitko)
    {
        if (_cts != null) { MessageBox.Show("Počkej, až doběhne předchozí akce.", "Konzolák Servis"); return; }
        if (a.Potvrzeni != null &&
            MessageBox.Show(a.Potvrzeni, "Konzolák Servis", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _cts = new CancellationTokenSource();
        Prubeh.Visibility = Visibility.Visible; Zrusit.IsEnabled = true; Akce.IsEnabled = false; VysledekPanel.IsEnabled = false;
        var puvodniText = tlacitko.Content;
        tlacitko.Content = "⏳ Probíhá…";
        StavText.Text = $"Probíhá: {a.Text}…";
        _log.Zapis($"--- Náprava: {a.Text} ---");
        var progress = new Progress<string>(l => { _log.Zapis(l); StavText.Text = l; });
        string vysledek; bool chyba;
        try
        {
            vysledek = await Task.Run(() => a.Spustit(progress, _cts.Token));
            chyba = JeChyba(vysledek);
            if (chyba) vysledek = vysledek.Substring(5).TrimStart(':', ' ');
        }
        catch (OperationCanceledException) { vysledek = "Akce byla přerušena."; chyba = true; }
        catch (Exception ex) { vysledek = $"Akce se nezdařila: {ex.Message}"; chyba = true; }

        _log.Zapis((chyba ? "CHYBA: " : "OK: ") + vysledek);
        StavText.Text = vysledek;
        try
        {
            if (!chyba && z.Obnovit != null)
            {
                StavText.Text = "Ověřuji výsledek – spouštím kontrolu znovu…";
                ZobrazPrubeh($"{z.Nadpis} – kontrola po nápravě");
                var nova = await Task.Run(() => z.Obnovit(progress, _cts.Token));
                _posledni = nova;
                Vykresli(nova);
                if (z.Shrnuti != nova.Shrnuti)
                    vysledek += $"\n\nPřed nápravou: {z.Shrnuti}\nPo nápravě: {nova.Shrnuti}";
                else
                    vysledek += $"\n\nKontrola po nápravě: {nova.Shrnuti}";
            }
            else
                tlacitko.Content = chyba ? puvodniText : "✔ Hotovo";
        }
        catch (Exception ex)
        {
            _log.Zapis($"Obnovení stavu selhalo: {ex.Message}");
            Vykresli(z);
            vysledek += $"\n\nNový výsledek se nepodařilo načíst: {ex.Message}";
        }
        finally
        {
            _cts.Dispose(); _cts = null;
            Prubeh.Visibility = Visibility.Collapsed; Zrusit.IsEnabled = false; Akce.IsEnabled = true; VysledekPanel.IsEnabled = true;
        }
        if (!chyba && z.Obnovit == null) tlacitko.IsEnabled = false;
        StavText.Text = vysledek;
        UkazOznameni(a.Text, vysledek, chyba);
    }

    /// <summary>Barevné oznámení nahoře ve výsledku – zelené OK / červená chyba.</summary>
    private void UkazOznameni(string akce, string text, bool chyba)
    {
        var stare = VysledekPanel.Children.OfType<Border>().FirstOrDefault(b => (b.Tag as string) == "oznameni");
        if (stare != null) VysledekPanel.Children.Remove(stare);
        var obsah = new StackPanel();
        obsah.Children.Add(new TextBlock { Text = (chyba ? "✖ " : "✔ ") + akce, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        obsah.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        var banner = new Border
        {
            Tag = "oznameni",
            Background = B("Pozadi"), BorderBrush = chyba ? B("Chyba") : B("Ok"), BorderThickness = new Thickness(5, 1, 1, 1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10),
            Child = obsah
        };
        VysledekPanel.Children.Insert(0, banner);
        VysledekScroll.ScrollToTop();
        if (chyba)
            MessageBox.Show($"{akce}\n\n{text}", "Konzolák Servis", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ---------- Tlačítka ----------

    private void Pohled_Changed(object s, RoutedEventArgs e)
    {
        if (Konzole == null || VysledekScroll == null) return;   // volá se už během InitializeComponent
        var log = ZalozkaLog.IsChecked == true;
        Konzole.Visibility = log ? Visibility.Visible : Visibility.Collapsed;
        VysledekScroll.Visibility = log ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Zrusit_Click(object s, RoutedEventArgs e) => _cts?.Cancel();

    private void UlozVysledek_Click(object s, RoutedEventArgs e)
    {
        if (_posledni == null) return;
        var nazev = Bezpecne($"{_posledni.Nadpis}_{_posledni.Pocitac}_{_posledni.Cas:yyyy-MM-dd_HH-mm}");
        var cesta = ZeptejSeNaSoubor(nazev);
        if (cesta == null) return;
        ZpravaExport.Uloz(_posledni, cesta);
        Ulozeno(cesta);
    }

    private void UlozLog_Click(object s, RoutedEventArgs e)
    {
        var cesta = ZeptejSeNaSoubor(Bezpecne($"Servisni_log_{Environment.MachineName}_{DateTime.Now:yyyy-MM-dd_HH-mm}"));
        if (cesta == null) return;
        _log.Uloz(cesta);
        Ulozeno(cesta);
    }

    private static string? ZeptejSeNaSoubor(string nazev)
    {
        Directory.CreateDirectory(LogService.SlozkaLogu);
        var dlg = new SaveFileDialog
        {
            Title = "Uložit jako",
            FileName = nazev,
            InitialDirectory = LogService.SlozkaLogu,
            Filter = "Webová stránka – dá se otevřít i vytisknout (*.html)|*.html|Prostý text (*.txt)|*.txt",
            AddExtension = true,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private void Ulozeno(string cesta)
    {
        _log.Zapis($"Uloženo: {cesta}");
        StavText.Text = $"Uloženo: {System.IO.Path.GetFileName(cesta)}";
        if (MessageBox.Show($"Soubor byl uložen:\n{cesta}\n\nOtevřít ho?", "Konzolák Servis",
                MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo(cesta) { UseShellExecute = true });
    }

    // ---------- Aktualizace a novinky ----------

    private NovaVerze? _nova;

    private async void Window_Loaded(object s, RoutedEventArgs e) => await ZkontrolujAsync(tiše: true);

    private async void Zkontrolovat_Click(object s, RoutedEventArgs e) => await ZkontrolujAsync(tiše: false);

    private async Task ZkontrolujAsync(bool tiše)
    {
        try
        {
            _nova = await AktualizaceService.ZkontrolujAsync();
        }
        catch (Exception ex)
        {
            _log.Zapis($"Kontrola aktualizací se nezdařila: {ex.Message}");
            if (!tiše) MessageBox.Show("Kontrola aktualizací se nezdařila. Zkontroluj připojení k internetu.", "Konzolák Servis");
            return;
        }
        if (_nova == null && AktualizaceService.PosledniChyba != null)
        {
            _log.Zapis($"Aktualizace: {AktualizaceService.PosledniChyba}");
            VerzeInfo.Text = $"Verze {AktualizaceService.VerzeText} · aktualizace nedostupné";
            VerzeInfo.ToolTip = AktualizaceService.PosledniChyba;
            if (!tiše) MessageBox.Show($"Nové verze teď nejde zkontrolovat.\n\n{AktualizaceService.PosledniChyba}",
                "Konzolák Servis", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_nova != null)
        {
            AktualizaceText.Text = $"K dispozici je nová verze {_nova.Verze.Major}.{_nova.Verze.Minor}.{_nova.Verze.Build}";
            AktualizaceBox.Visibility = Visibility.Visible;
            _log.Zapis($"Dostupná nová verze {_nova.Tag}.");
        }
        else if (!tiše)
            MessageBox.Show($"Používáš nejnovější verzi {AktualizaceService.VerzeText}.", "Konzolák Servis");
    }

    private async void Aktualizovat_Click(object s, RoutedEventArgs e)
    {
        if (_nova == null || _cts != null) return;
        var z = NovinkyZpravou($"Co je nového ve verzi {_nova.Tag}", _nova.Novinky);
        z.Shrnuti = $"Aktualizace z verze {AktualizaceService.VerzeText} na {_nova.Tag.TrimStart('v')}.";
        Vykresli(z); ZalozkaVysledek.IsChecked = true;
        if (MessageBox.Show($"Stáhnout a nainstalovat verzi {_nova.Tag}?\nAplikace se poté sama restartuje.",
                "Aktualizace Konzolák Servis", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _cts = new CancellationTokenSource();
        Aktualizovat.IsEnabled = false; Prubeh.Visibility = Visibility.Visible; Akce.IsEnabled = false;
        StavText.Text = "Stahuji aktualizaci…";
        try
        {
            await AktualizaceService.NainstalujAsync(_nova, new Progress<string>(l => { _log.Zapis(l); StavText.Text = l; }), _cts.Token);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _log.Zapis($"Aktualizace selhala: {ex.Message}");
            MessageBox.Show($"Aktualizace se nezdařila:\n{ex.Message}", "Konzolák Servis", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose(); _cts = null;
            Aktualizovat.IsEnabled = true; Prubeh.Visibility = Visibility.Collapsed; Akce.IsEnabled = true;
        }
    }

    private void Novinky_Click(object s, RoutedEventArgs e)
    {
        var z = NovinkyZpravou("Co je nového", AktualizaceService.NovinkyVAplikaci());
        z.Shrnuti = $"Používáš verzi {AktualizaceService.VerzeText}.";
        _posledni = z; UlozitVysledek.IsEnabled = true;
        Vykresli(z); ZalozkaVysledek.IsChecked = true;
    }

    /// <summary>Převede seznam změn („## verze“ + „- položka“) na zprávu.</summary>
    private static Zprava NovinkyZpravou(string nadpis, string text)
    {
        var z = new Zprava(nadpis) { Stav = Stav.Info };
        SekceZpravy? sekce = null;
        foreach (var radek in text.Replace("\r", "").Split('\n'))
        {
            var t = radek.Trim();
            if (t.StartsWith("#")) sekce = z.PridejSekci("Verze " + t.TrimStart('#', ' '));
            else if (t.StartsWith("- ") || t.StartsWith("* "))
                (sekce ??= z.PridejSekci("Změny")).Pridej(t[2..].Trim(), "", Stav.Ok);
        }
        if (z.Sekce.Count == 0) z.PridejSekci("Změny").Pridej(text.Trim().Length > 0 ? text.Trim() : "Bez popisu změn.");
        return z;
    }

    private static string Bezpecne(string s) =>
        string.Concat(s.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));

    private void OtevritLogy_Click(object s, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LogService.SlozkaLogu);
        Process.Start(new ProcessStartInfo("explorer.exe", LogService.SlozkaLogu) { UseShellExecute = true });
    }
}
