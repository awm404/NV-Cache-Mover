using System.Runtime.InteropServices;
using System.Windows;
using NvidiaCacheMover.Services;

namespace NvidiaCacheMover;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0)
        {
            bool attached = !Console.IsOutputRedirected && AttachConsole(-1);
            int exitCode = await RunCliAsync(e.Args);
            if (attached) FreeConsole();
            Shutdown(exitCode);
            return;
        }

        var win = new MainWindow();
        MainWindow = win;
        win.Show();
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        try
        {
            var svc = new CacheMigrationService();
            var items = svc.CreateDefaultItems();
            string targetRoot = svc.DetectInitialTargetRoot(items);

            if (string.Equals(args[0], "--status", StringComparison.OrdinalIgnoreCase))
            {
                await svc.RefreshAllAsync(items, targetRoot);
                Console.WriteLine();
                Console.WriteLine($"Detected Target Root: {targetRoot}");
                foreach (var item in items)
                {
                    Console.WriteLine($"  [{item.StatusText}] {item.DisplayName} ({item.SizeText})");
                }
                return 0;
            }

            if (string.Equals(args[0], "--move", StringComparison.OrdinalIgnoreCase) && args.Length >= 2)
            {
                string dest = args[1];
                Console.WriteLine();
                await svc.MoveAndLinkSelectedAsync(items, dest, Console.WriteLine, new Progress<double>());
                return 0;
            }

            if (string.Equals(args[0], "--clean-ota", StringComparison.OrdinalIgnoreCase))
            {
                await svc.RefreshAllAsync(items, targetRoot);
                var ota = items.First(i => i.Id == "ota_artifacts");
                await svc.CleanOtaArtifactsAsync(ota, targetRoot, Console.WriteLine);
                return 0;
            }

            Console.WriteLine("Usage: NvidiaCacheMover.exe [--status | --move <FolderPath> | --clean-ota]");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }
}
