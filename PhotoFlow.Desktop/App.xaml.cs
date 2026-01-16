using System;
using System.IO;
using System.Windows;
using PhotoFlow.Licensing.Services;
using PhotoFlow.Licensing.Trial;

namespace PhotoFlow.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)

    {
        base.OnStartup(e);

        var licensePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PhotoFlow", "Licenses", "photoflow.license.json"
        );

        // 1) Проверка на лиценз (платен или вече съществуващ trial)
        var licensing = new OfflineLicensingService();

        // 2) Ако няма валиден лиценз -> опитай да вземеш TRIAL от сървъра и да го запишеш
        if (!licensing.IsValid())
        {
            try
            {
                var trialClient = new TrialClient();
                var env = await trialClient.StartTrialAsync();


                if (env != null)
                {
                    TrialLicenseWriter.SaveEnvelopeAsLicenseFile(env);

                    // Презареждаме лиценза след запис
                    licensing = new OfflineLicensingService();
                }
            }
            catch
            {
                // Няма интернет / сървърът не е наличен / TLS проблем и т.н.
                // Оставяме да падне към блокиращия прозорец.
            }
        }

        // 3) Ако пак няма валиден лиценз -> блокиращ екран и край
        if (!licensing.IsValid())
        {
            var w = new LicenseBlockedWindow(licensing.GetStatusText(), licensePath);
            MainWindow = w;
            w.Show();
            return;
        }

        // 4) Има валиден лиценз (paid или trial) -> нормален старт
        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }
}
