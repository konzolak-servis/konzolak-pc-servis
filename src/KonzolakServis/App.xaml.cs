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
        if (!HesloOkno.Overit())
        {
            Shutdown();
            return;
        }
        new MainWindow().Show();
    }
}
