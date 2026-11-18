using System.Windows;

namespace RustBuilder;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Updater.Cleanup();
        Theme.Apply(AppSettings.Current.AccentColor);
        Site.LoadSaved();
    }
}
