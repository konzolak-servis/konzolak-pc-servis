using System.Globalization;
using System.Text;
using System.Windows;
using KonzolakServis.Views;

namespace KonzolakServis;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Konzolové nástroje Windows píšou v OEM kódování (CP852 pro češtinu)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cz = new CultureInfo("cs-CZ");
        CultureInfo.DefaultThreadCurrentCulture = cz;
        CultureInfo.DefaultThreadCurrentUICulture = cz;
        base.OnStartup(e);

        // Zámek na heslo – bez správného hesla se aplikace nespustí (ochrana proti spuštění zákazníkem).
        // Po dobu dialogu hesla není otevřené žádné hlavní okno; bez OnExplicitShutdown by se aplikace
        // po zavření dialogu sama ukončila (OnLastWindowClose) a hlavní okno by se vůbec neotevřelo.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (Services.HesloService.ZamekAktivni && !HesloOkno.Overit())
        {
            Shutdown();
            return;
        }
        var okno = new MainWindow();
        MainWindow = okno;
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        okno.Show();
    }
}
