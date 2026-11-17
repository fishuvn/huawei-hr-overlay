using Avalonia;
using HuaweiHROverlay.Core;

namespace HuaweiHROverlay;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a is "--version" or "-v"))
        {
            Console.WriteLine($"HuaweiHROverlay {GitHubUpdateService.GetCurrentVersionString()}");
            return 0;
        }

        if (args.Any(a => a is "--help" or "-h"))
        {
            Console.WriteLine($"""
                Huawei HR Overlay v{GitHubUpdateService.GetCurrentVersionString()}
                Cross-platform BLE Heart Rate Broadcast Studio for OBS (Windows, Linux, macOS)

                Usage:
                  HuaweiHROverlay                           Launch desktop Studio GUI
                  HuaweiHROverlay --headless --simulate     Run headless HTTP/WS server with simulated BPM
                  HuaweiHROverlay --headless --device <ID>  Run headless server and connect to BLE watch <ID>
                  HuaweiHROverlay --headless --scan         Scan nearby BLE HR devices in terminal
                  HuaweiHROverlay --version                 Print version
                """);
            return 0;
        }

        if (args.Any(a => a == "--headless"))
        {
            return HeadlessRunner.RunAsync(args).GetAwaiter().GetResult();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
