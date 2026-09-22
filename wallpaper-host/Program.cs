using System.Diagnostics;
using System.Net;

namespace Spotifly.WallpaperHost;

internal static class Program
{
    private const string MutexName = "Local\\Spotifly.WallpaperHost.Singleton";

    [STAThread]
    private static async Task Main(string[] args)
    {
        bool launchClient = args.Any(arg => arg.Equals("--launch-client", StringComparison.OrdinalIgnoreCase));
        using var singleton = new Mutex(true, MutexName, out bool ownsMutex);

        if (!ownsMutex)
        {
            if (launchClient)
            {
                LaunchSpotifly();
            }
            return;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        var catalog = new WallpaperCatalog();
        catalog.Refresh();

        await using var audio = new AudioSpectrumService();
        await using var scenes = new SceneCaptureService();
        await using var workshop = new SteamWorkshopService();
        await using var server = new LocalServer(catalog, audio, scenes, workshop, LaunchSpotifly);
        try
        {
            await server.StartAsync(shutdown.Token);
        }
        catch
        {
            if (launchClient)
            {
                LaunchSpotifly();
            }
            return;
        }

        Process? client = null;
        if (launchClient)
        {
            client = LaunchSpotifly();
        }

        try
        {
            await KeepAliveAsync(client, shutdown);
        }
        finally
        {
            shutdown.Cancel();
        }
    }

    private static Process? LaunchSpotifly()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "Spotifly.exe");
        if (!File.Exists(executable))
        {
            return null;
        }

        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true
            });
        }
        catch
        {
            return null;
        }
    }

    private static async Task KeepAliveAsync(Process? launchedClient, CancellationTokenSource shutdown)
    {
        bool clientWasSeen = launchedClient is not null;
        DateTime? noClientSince = null;

        while (!shutdown.IsCancellationRequested)
        {
            bool running = IsSpotiflyRunning(launchedClient);
            clientWasSeen |= running;

            if (clientWasSeen && !running)
            {
                noClientSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - noClientSince > TimeSpan.FromSeconds(15))
                {
                    shutdown.Cancel();
                    break;
                }
            }
            else
            {
                noClientSince = null;
            }

            try
            {
                await Task.Delay(1000, shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static bool IsSpotiflyRunning(Process? launchedClient)
    {
        try
        {
            if (launchedClient is { HasExited: false })
            {
                return true;
            }
        }
        catch
        {
        }

        try
        {
            Process[] processes = Process.GetProcessesByName("Spotifly");
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            return false;
        }
    }
}
