using System;
using System.IO;
using System.Windows;
using PhotoFlow.Licensing.Services;
using PhotoFlow.Licensing.Trial;

namespace PhotoFlow.Desktop;

public partial class App : Application
{
    // Полезно за watermark-и и UI (можеш да го четеш от всякъде: App.IsTrial, App.TrialDaysLeft)
    public static bool IsTrial { get; private set; }
    public static int TrialDaysLeft { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 0) Splash screen (показва се веднага)
        var splash = new SplashScreen("Assets/splash.png");
        splash.Show(autoClose: false);

        base.OnStartup(e);

        var licensePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PhotoFlow", "Licenses", "photoflow.license.json"
        );

        // 1) Проверка на платен лиценз
        var licensing = new OfflineLicensingService();

        IsTrial = false;
        TrialDaysLeft = 0;

        // 2) Ако няма валиден платен лиценз -> offline trial (14 дни)
        if (!licensing.IsValid())
        {
            try
            {
                var st = OfflineTrialStore.LoadOrCreate(trialDays: 14);
                var now = DateTimeOffset.UtcNow;

                if (now <= st.ExpiresUtc)
                {
                    IsTrial = true;
                    TrialDaysLeft = Math.Max(0, (int)Math.Ceiling((st.ExpiresUtc - now).TotalDays));
                }
            }
            catch
            {
                // Trial файл повреден или засечен clock rollback -> третираме като изтекъл trial
                IsTrial = false;
                TrialDaysLeft = 0;
            }
        }

        // 3) Ако няма валиден платен лиценз И няма активен trial -> блокиращ екран
        if (!licensing.IsValid() && !IsTrial)
        {
            var status = licensing.GetStatusText();

            // По желание можеш да добавиш по-ясно съобщение:
            // status += "\nTrial expired or unavailable.";

            var w = new LicenseBlockedWindow(status, licensePath);
            MainWindow = w;
            w.Show();

            splash.Close(TimeSpan.FromMilliseconds(200));
            return;
        }

        // 4) Има валиден платен лиценз ИЛИ активен trial -> нормален старт
        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        splash.Close(TimeSpan.FromMilliseconds(200));
    }
}
