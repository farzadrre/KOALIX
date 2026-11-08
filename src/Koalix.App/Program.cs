using Velopack;

namespace Koalix.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        StartupLog.Trace($"--- start v{typeof(Program).Assembly.GetName().Version} args=[{string.Join(' ', args)}] os={Environment.OSVersion} base={AppContext.BaseDirectory}");
        try
        {
            // Must run first: handles Velopack install / update / uninstall hooks and exits early when invoked by the installer.
            VelopackApp.Build()
                .OnBeforeUninstallFastCallback(_ => Services.UpdateService.CleanupOnUninstall())
                .Run();
        }
        catch (Exception ex) { StartupLog.Trace("velopack hook failed (ignored): " + ex); }

        try
        {
            var app = new App();
            app.InitializeComponent();
            StartupLog.Trace("resources loaded");
            app.Run();
        }
        catch (Exception ex)
        {
            StartupLog.Fatal("Main", ex);
            Environment.Exit(1);
        }
    }
}
