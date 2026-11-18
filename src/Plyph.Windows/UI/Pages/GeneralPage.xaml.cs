using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Win32;
using Plyph.Core;

namespace Plyph.Windows.UI.Pages;

public sealed partial class GeneralPage : Page
{
    private static readonly string[] ResultPopupModes = { "auto", "compact", "workspace" };
    private static readonly string[] ThemeValues = { "system", "light", "dark" };

    private static readonly string[] AppearanceValues = ["system", "solid"];

    private SettingsHostContext? _context;
    private bool _loading;

    public GeneralPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is SettingsHostContext ctx)
        {
            _context = ctx;
            LoadState();
        }
    }

    private void LoadState()
    {
        if (_context == null)
            return;
        _loading = true;
        try
        {
            var s = _context.Settings;
            ModeInfoBar.Title = s.Mode == StorageMode.Portable
                ? "Running in Portable (No-Install) Mode"
                : "Running in Installed Mode";
            ModeInfoBar.Message = $"Settings & encrypted data: {s.DataDirectory}";

            ThemeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ThemeValues, s.GetString("app-theme")));

            PopupAppearanceCombo.SelectedIndex = Math.Max(0, Array.IndexOf(AppearanceValues, s.GetString("result-popup-appearance")));

            var isInstalled = s.Mode == StorageMode.Installed;
            StartupToggle.IsEnabled = isInstalled;
            StartupToggle.IsOn = isInstalled && IsStartupEnabled();
            StartupCard.Description = isInstalled
                ? "Start Plyph automatically in the system tray when you sign in."
                : "Available only in Installed mode (Portable mode makes zero registry changes).";

            var previewResults = s.GetBoolean("preview-results");
            PreviewToggle.IsOn = previewResults;
            DismissOutsideToggle.IsOn = s.GetBoolean("result-popup-dismiss-on-outside-click");
            ResultPopupModeCombo.IsEnabled = previewResults;
            DiffDefaultToggle.IsEnabled = previewResults;

            var modeIdx = Array.IndexOf(ResultPopupModes, s.GetString("result-popup-mode"));
            ResultPopupModeCombo.SelectedIndex = modeIdx >= 0 ? modeIdx : 0;

            DiffDefaultToggle.IsOn = s.GetBoolean("preview-show-changes");
            PlainTextToggle.IsOn = s.GetBoolean("prefer-plain-text");

        }
        finally
        {
            _loading = false;
        }
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_context == null) return;
        try
        {
            Directory.CreateDirectory(_context.Settings.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", _context.Settings.DataDirectory) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _context == null || ThemeCombo.SelectedIndex < 0) return;
        var next = ThemeValues[ThemeCombo.SelectedIndex];
        if (_context.Settings.GetString("app-theme") != next)
        {
            _context.Settings.SetString("app-theme", next);
            _context.OnThemeChanged();
        }
    }

    private void PopupAppearanceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _context == null || PopupAppearanceCombo.SelectedIndex < 0) return;
        _context.Settings.SetString("result-popup-appearance", AppearanceValues[PopupAppearanceCombo.SelectedIndex]);
        _context.OnThemeChanged();
    }

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _context == null || _context.Settings.Mode != StorageMode.Installed) return;
        SetStartupEnabled(StartupToggle.IsOn);
    }

    private void DismissOutsideToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _context == null) return;
        _context.Settings.SetBoolean("result-popup-dismiss-on-outside-click", DismissOutsideToggle.IsOn);
    }

    private void PreviewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _context == null) return;
        _context.Settings.SetBoolean("preview-results", PreviewToggle.IsOn);
        ResultPopupModeCombo.IsEnabled = PreviewToggle.IsOn;
        DiffDefaultToggle.IsEnabled = PreviewToggle.IsOn;
    }

    private void ResultPopupModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _context == null || ResultPopupModeCombo.SelectedIndex < 0) return;
        _context.Settings.SetString("result-popup-mode", ResultPopupModes[ResultPopupModeCombo.SelectedIndex]);
    }

    private void DiffDefaultToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _context == null) return;
        _context.Settings.SetBoolean("preview-show-changes", DiffDefaultToggle.IsOn);
    }

    private void PlainTextToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _context == null) return;
        _context.Settings.SetBoolean("prefer-plain-text", PlainTextToggle.IsOn);
    }

    private static bool IsStartupEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
            return key?.GetValue("Plyph") is string s && !string.IsNullOrEmpty(s);
        }
        catch { return false; }
    }

    private void SetStartupEnabled(bool enabled)
    {
        _context?.Settings.SetBoolean("launch-at-startup", enabled);
        if (!OperatingSystem.IsWindows() || _context?.Settings.Mode == StorageMode.Portable)
            return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (enabled)
            {
                var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Plyph.exe");
                key.SetValue("Plyph", $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue("Plyph", throwOnMissingValue: false);
            }
        }
        catch { }
    }
}
