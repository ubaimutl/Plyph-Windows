using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Plyph.Core;
using WinRT;

namespace Plyph.Windows;

public static class Program
{
    private const string MutexName = @"Local\dev.ubai.Plyph.SingleInstance";
    private const string SettingsSignalName = @"Local\dev.ubai.Plyph.OpenSettings";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        using var openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SettingsSignalName);

        var openSettings = args.Any(a =>
            string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/settings", StringComparison.OrdinalIgnoreCase));

        if (!createdNew)
        {
            // Ask the existing background tray instance to open Settings
            openSignal.Set();
            return;
        }

        ComWrappersSupport.InitializeComWrappers();

#if PLYPH_PORTABLE
        var forcePortable = true;
#else
        var forcePortable = false;
#endif
        var (mode, dataDir) = StorageModeResolver.Resolve(args, forcePortable: forcePortable);
        var settings = new PlyphSettings(mode, dataDir, persistToDisk: true);
        var secretBackend = new WindowsSecretBackend(mode, dataDir);

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App(settings, secretBackend, openSettings, openSignal);
        });
    }
}
