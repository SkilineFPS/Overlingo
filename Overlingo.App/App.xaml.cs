using System.IO;
using System.Windows;
using Overlingo.Core;

namespace Overlingo.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            AppLog.Error($"Neošetřená chyba: {e.Exception}");
            MessageBox.Show(Loc.T($"Neočekávaná chyba:\n{e.Exception.Message}\n\nDetail je v záložce Logy / v {AppLog.LogDir}"), "Overlingo", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        try
        {
            var avail = Loc.Available();
            var code = Loc.Resolve(AppSettings.Load().UiLang, System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, avail);
            Loc.Set(code);
        }
        catch { }
        UiLocalizer.Init();
        TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Error($"Chyba na pozadí: {e.Exception.GetBaseException().Message}"); e.SetObserved(); };
    }
}
