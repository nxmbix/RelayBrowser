using System.Windows;
using System.Windows.Threading;

namespace Relay.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show($"Setup ran into a problem:\n\n{ex.Exception.Message}", "RELAY Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        base.OnStartup(e);
    }
}
