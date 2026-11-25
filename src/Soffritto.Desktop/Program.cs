using Soffritto.Desktop.Infrastructure;
using Velopack;

namespace Soffritto.Desktop;

public static class Program
{
    // Velopack va servito prima di WPF: gli hook di installazione e disinstallazione lanciano
    // l'exe con argomenti propri e si aspettano che esca subito, senza finestra.
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
