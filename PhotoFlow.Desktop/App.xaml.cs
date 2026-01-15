using System;
using System.IO;
using System.Windows;
using PhotoFlow.Licensing.Services;

namespace PhotoFlow.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Същият път, който ползва LicenseVerifier
        var licensePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PhotoFlow", "Licenses", "photoflow.license.json"
        );

        var licensing = new OfflineLicensingService();

        // Ако НЯМА валиден лиценз -> показваме блокиращия прозорец и приключваме при затваряне.
        if (!licensing.IsValid())
        {
            var w = new LicenseBlockedWindow(licensing.GetStatusText(), licensePath);
            MainWindow = w;

            // Гаранция: като затвори този прозорец -> приложението приключва
            w.Closed += (_, __) => Shutdown();

            w.Show();
            return;
        }

        // Има лиценз -> пускаме нормалното приложение
        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }
}
