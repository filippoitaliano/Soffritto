using Soffritto.Desktop.Infrastructure;
using Velopack;

namespace Soffritto.Desktop;

public static class Program
{
    // Velopack must run before WPF: the install and uninstall hooks launch the exe with their own
    // arguments and expect it to exit right away, without a window.
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            .SetLocator(Installation.Locator)
            .OnFirstRun(_ => Installation.IsFirstRun = true)
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
