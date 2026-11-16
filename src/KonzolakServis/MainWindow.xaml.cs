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
        (Kategorie.Diagnostika, "Diagnostika"),
        (Kategorie.Optimalizace, "Optimalizace"),
        (Kategorie.Disky, "Disky"),
        (Kategorie.Opravy, "Opravy systému"),
        (Kategorie.Cisteni, "Čištění"),
        (Kategorie.Sit, "Síť"),
        (Kategorie.HwInfo, "Hardware"),
    };

    public MainWindow()
    {
        InitializeComponent();
        Title = $"Konzolák Servis v{AktualizaceService.VerzeText}";
        VerzeInfo.Text = $"Verze {AktualizaceService.VerzeText}";
        _log.Radek += l => Dispatcher.Invoke(() => { Konzole.AppendText(l + Environment.NewLine); Konzole.ScrollToEnd(); });

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
        foreach (var a in ServisniKatalog.Vse.Where(a => a.Kategorie == kat))
        {
            var obsah = new StackPanel();
            obsah.Children.Add(new TextBlock { Text = a.Nazev, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            obsah.Children.Add(new TextBlock { Text = a.Popis, TextWrapping = TextWrapping.Wrap, MaxWidth = 240,
                Opacity = 0.75, FontSize = 12, Margin = new Thickness(0, 3, 0, 0) });
            var b = new Button { Content = obsah, Style = (Style)FindResource("TlacitkoAkce"), Width = 270 };
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
            VysledekPanel.Children.Add(new TextBlock { Text = s.Nadpis, FontSize = 16, FontWeight = FontWeights.SemiBold,
                Foreground = B("Akcent"), Margin = new Thickness(0, 14, 0, 6) });
            if (s.Popis.Length > 0)
                VysledekPanel.Children.Add(new TextBlock { Text = s.Popis, TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
                    Foreground = B("TextTlumeny"), Margin = new Thickness(0, 0, 0, 6) });
            var tabulka = new StackPanel();
            foreach (var r in s.Radky) tabulka.Children.Add(RadekUI(r, z));
            VysledekPanel.Children.Add(new Border { Background = B("Pozadi"), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 4, 12, 4), Child = tabulka });
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
        b.Click += async (_, _) => await ProvedNapravuAsync(a, z);
        return b;
    }

    private async Task ProvedNapravuAsync(AkceRadku a, Zprava z)
    {
        if (_cts != null) { MessageBox.Show("Počkej, až doběhne předchozí akce.", "Konzolák Servis"); return; }
        if (a.Potvrzeni != null &&
            MessageBox.Show(a.Potvrzeni, "Konzolák Servis", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _cts = new CancellationTokenSource();
        Prubeh.Visibility = Visibility.Visible; Zrusit.IsEnabled = true; Akce.IsEnabled = false; VysledekPanel.IsEnabled = false;
        StavText.Text = $"Probíhá: {a.Text}…";
        _log.Zapis($"--- Náprava: {a.Text} ---");
        var progress = new Progress<string>(_log.Zapis);
        try
        {
            var vysledek = await Task.Run(() => a.Spustit(progress, _cts.Token));
            _log.Zapis(vysledek);
            StavText.Text = vysledek;
            if (z.Obnovit != null)
            {
                var nova = await Task.Run(() => z.Obnovit(progress, _cts.Token));
                _posledni = nova;
                Vykresli(nova);
            }
        }
        catch (Exception ex)
        {
            _log.Zapis($"CHYBA: {ex.Message}");
            StavText.Text = "Náprava selhala – podrobnosti v záložce Podrobný log";
            MessageBox.Show($"Akce se nezdařila:\n{ex.Message}", "Konzolák Servis", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cts.Dispose(); _cts = null;
            Prubeh.Visibility = Visibility.Collapsed; Zrusit.IsEnabled = false; Akce.IsEnabled = true; VysledekPanel.IsEnabled = true;
        }
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
