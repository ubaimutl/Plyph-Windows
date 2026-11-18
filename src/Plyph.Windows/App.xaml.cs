using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Plyph.Core;
using Plyph.Windows.UI;

namespace Plyph.Windows;

public partial class App : Application
{
    private readonly IPlyphSettings _settings;
    private readonly ISecretBackend _secretBackend;
    private readonly bool _openSettingsOnStart;
    private readonly EventWaitHandle? _settingsSignal;
    private PlyphAppController? _controller;

    public App(
        IPlyphSettings settings,
        ISecretBackend secretBackend,
        bool openSettingsOnStart,
        EventWaitHandle? settingsSignal)
    {
        _settings = settings;
        _secretBackend = secretBackend;
        _openSettingsOnStart = openSettingsOnStart;
        _settingsSignal = settingsSignal;

        if (!ThemeHelper.IsHighContrastEnabled())
            RequestedTheme = ThemeHelper.ShouldUseDark(settings) ? ApplicationTheme.Dark : ApplicationTheme.Light;

        UnhandledException += (_, e) =>
        {
            try
            {
                var logPath = Path.Combine(Path.GetTempPath(), "plyph-crash.log");
                File.WriteAllText(logPath, $"[{DateTimeOffset.Now:O}] {e.Message}\n{e.Exception}\n");
            }
            catch
            {
            }
        };

        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _controller = new PlyphAppController(_settings, _secretBackend, _openSettingsOnStart);

        if (_settingsSignal != null)
        {
            var dq = DispatcherQueue.GetForCurrentThread();
            _ = Task.Run(() =>
            {
                while (true)
                {
                    try
                    {
                        _settingsSignal.WaitOne();
                        dq.TryEnqueue(() => _controller?.OpenSettings());
                    }
                    catch
                    {
                        break;
                    }
                }
            });
        }
    }
}
