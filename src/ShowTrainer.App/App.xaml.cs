using System.Windows;
using ShowTrainer.Core.Infra;

namespace ShowTrainer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, a) =>
        {
            Log.Error("UI exception: " + a.Exception);
            MessageBox.Show(a.Exception.Message, "ShowTrainer error", MessageBoxButton.OK, MessageBoxImage.Error);
            a.Handled = true;
        };
        base.OnStartup(e);
    }
}
