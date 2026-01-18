using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using PhotoFlow.Licensing.Services;
using PhotoFlow.Licensing.Trial;

namespace PhotoFlow.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // 0) Splash screen (показва се веднага)
        var splash = new SplashScreen("Assets/splash.png");
        splash.Show(autoClose: false);

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

                // 5 секунди timeout: ако сървърът/мрежата “виси”, продължаваме без да чакаме безкрайно
                var trialTask = trialClient.StartTrialAsync();
                var completed = await Task.WhenAny(trialTask, Task.Delay(TimeSpan.FromSeconds(5)));

                if (completed == trialTask)
                {
                    // Task е приключил в рамките на 5 сек.
                    var env = await trialTask;

                    if (env != null)
                    {
                        TrialLicenseWriter.SaveEnvelopeAsLicenseFile(env);

                        // Презареждаме лиценза след запис
                        licensing = new OfflineLicensingService();
                    }
                }
                else
                {
                    // Timeout -> не правим нищо, ще падне към блокиращия прозорец по-долу
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

            splash.Close(TimeSpan.FromMilliseconds(200));
            return;
        }

        // 4) Има валиден лиценз (paid или trial) -> нормален старт
        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        splash.Close(TimeSpan.FromMilliseconds(200));
    }
}
