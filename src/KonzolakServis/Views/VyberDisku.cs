using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KonzolakServis.Services;

namespace KonzolakServis.Views;

/// <summary>Malé okno pro výběr disku (a režimu testu) před testem povrchu nebo měřením rychlosti.</summary>
public static class VyberDisku
{
    /// <summary>Lze volat z libovolného vlákna – okno se otevře na UI vlákně. Null = uživatel zrušil.</summary>
    public static (InfoDisku Disk, bool Uplny)? Zobraz(string nadpis, bool sRezimem)
    {
        var disky = DiskService.SeznamDisku();
        return Application.Current.Dispatcher.Invoke(() => Okno(nadpis, sRezimem, disky));
    }

    private static (InfoDisku, bool)? Okno(string nadpis, bool sRezimem, List<InfoDisku> disky)
    {
        Brush B(string k) => Application.Current.TryFindResource(k) as Brush ?? Brushes.Gray;
        if (disky.Count == 0)
        {
            MessageBox.Show("Nenalezen žádný disk.", "Konzolak service app", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var w = new Window
        {
            Title = nadpis, Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = B("Pozadi"), Foreground = B("Text"),
            Owner = Application.Current.MainWindow, ShowInTaskbar = false, Icon = Ikona.Aplikace
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = nadpis, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = B("Akcent") });
        panel.Children.Add(new TextBlock { Text = "Vyberte disk:", Margin = new Thickness(0, 12, 0, 4) });
        var seznam = new ComboBox { ItemsSource = disky.Select(d => d.Popisek).ToList(), SelectedIndex = 0, Padding = new Thickness(6, 4, 6, 4) };
        panel.Children.Add(seznam);

        RadioButton? uplny = null;
        if (sRezimem)
        {
            panel.Children.Add(new TextBlock { Text = "Režim:", Margin = new Thickness(0, 14, 0, 4) });
            var rychly = new RadioButton { Content = "Rychlý – vzorky z celého disku (1–3 minuty)", IsChecked = true, Foreground = B("Text"), GroupName = "rezim" };
            uplny = new RadioButton { Content = "Úplný – přečte každý sektor (desítky minut až hodiny)", Foreground = B("Text"), GroupName = "rezim", Margin = new Thickness(0, 4, 0, 0) };
            panel.Children.Add(rychly);
            panel.Children.Add(uplny);
        }
        panel.Children.Add(new TextBlock
        {
            Text = sRezimem
                ? "Test pouze čte – data na disku se nemění. Lze ho kdykoli zastavit tlačítkem Zastavit."
                : "Čtení probíhá přímo z disku, zápis jen do dočasného souboru (1 GB), který se po testu smaže. Data se nemění.",
            TextWrapping = TextWrapping.Wrap, Foreground = B("TextTlumeny"), FontSize = 12.5, Margin = new Thickness(0, 14, 0, 0)
        });

        var tlacitka = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var spustit = new Button { Content = "Spustit", IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var zrusit = new Button { Content = "Zrušit", IsCancel = true };
        if (Application.Current.TryFindResource("TlacitkoNaprava") is Style sn) spustit.Style = sn;
        if (Application.Current.TryFindResource("TlacitkoMale") is Style sm) zrusit.Style = sm;
        spustit.Click += (_, _) => w.DialogResult = true;
        tlacitka.Children.Add(spustit);
        tlacitka.Children.Add(zrusit);
        panel.Children.Add(tlacitka);
        w.Content = panel;

        if (w.ShowDialog() != true || seznam.SelectedIndex < 0) return null;
        return (disky[seznam.SelectedIndex], uplny?.IsChecked == true);
    }
}
