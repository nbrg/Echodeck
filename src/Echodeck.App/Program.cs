using Echodeck.App.Services;
using Velopack;

namespace Echodeck.App;

/// <summary>
/// Entry point. Velopack must run first: when the installer, an update or the uninstaller
/// launches Echodeck with special arguments, it handles them here and exits before any UI.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            // Don't leave a "start with Windows" entry pointing at a deleted app.
            .OnBeforeUninstallFastCallback(_ =>
            {
                try { StartupRegistration.SetEnabled(false); } catch { /* best effort */ }
            })
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
