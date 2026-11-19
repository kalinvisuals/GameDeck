using GameDeck.Core.Localization;
using GameDeck.App.Tray;
using GameDeck.App.Web;
using GameDeck.Core.Audio;
using GameDeck.Core.Config;
using GameDeck.Core.Diagnostics;
using GameDeck.Core.Security;
using Microsoft.AspNetCore.Builder;

namespace GameDeck.App;

internal static class Program
{
    [STAThread]
    /// <param name="args">--open: also open the dashboard in the browser and the Audio setup window.</param>
    private static void Main(string[] args)
    {
        // One instance per data folder: a dev/test copy with its own GAMEDECK_DATA can run alongside.
        var dataKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(ConfigStore.DefaultDataDirectory().ToLowerInvariant())))[..16];
        using var mutex = new Mutex(true, $@"Local\GameDeck.{dataKey}", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show(L.T("app.alreadyRunning"), "GameDeck",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        var dataDir = ConfigStore.DefaultDataDirectory();
        Directory.CreateDirectory(dataDir);
        Log.Initialize(dataDir);
        Log.Info($"GameDeck {GameDeckService.Version} starting, data: {dataDir}");

        ConfigStore store;
        try
        {
            store = new ConfigStore(dataDir);
            L.Language = store.Read(c => c.Ui.Language);
        }
        catch (Exception ex)
        {
            Fatal(L.T("app.settingsFailed"), ex);
            return;
        }

        // Core Audio objects are created on the thread pool (MTA), keeping COM away from the UI thread.
        var engine = Task.Run(() => new AudioEngine()).GetAwaiter().GetResult();
        var service = new GameDeckService(store, engine, new PairingService());
        Task.Run(service.Start).GetAwaiter().GetResult();

        WebApplication web;
        try
        {
            // Dev/testing: GAMEDECK_LOOPBACK_ONLY=1 keeps the server off the network entirely.
            var loopbackOnly = Environment.GetEnvironmentVariable("GAMEDECK_LOOPBACK_ONLY") == "1";
            web = DashboardServer.Build(service, loopbackOnly ? System.Net.IPAddress.Loopback : null);
            web.StartAsync().GetAwaiter().GetResult();
            Log.Info($"Dashboard listening on port {service.Port}");
            service.Updates.Start();
        }
        catch (Exception ex)
        {
            Task.Run(engine.Dispose).Wait();
            Fatal(L.T("app.serverFailed", service.Port), ex);
            return;
        }

        Application.Run(new TrayContext(service, openOnStart: args.Contains("--open")));

        Log.Info("Shutting down");
        service.Updates.Dispose();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            web.StopAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch { /* exiting anyway */ }
        Task.Run(engine.Dispose).Wait(TimeSpan.FromSeconds(3));
    }

    private static void Fatal(string message, Exception ex)
    {
        Log.Error(message, ex);
        MessageBox.Show($"{message}\n\n{ex.Message}", "GameDeck", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
