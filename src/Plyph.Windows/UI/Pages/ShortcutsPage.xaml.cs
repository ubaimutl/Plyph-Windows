using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Plyph.Core;
using Plyph.Windows.Shortcuts;
using Windows.System;
using Windows.UI.Core;

namespace Plyph.Windows.UI.Pages;

public sealed partial class ShortcutsPage : Page
{
    private static readonly string[] PalettePositions = { "near-pointer", "monitor-center" };

    private SettingsHostContext? _context;
    private bool _loading;

    public ShortcutsPage()
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
        if (_context == null) return;
        _loading = true;
        try
        {
            var s = _context.Settings;
            var palIdx = Array.IndexOf(PalettePositions, s.GetString("action-palette-position"));
            PalettePosCombo.SelectedIndex = palIdx >= 0 ? palIdx : 0;

            UiaToggle.IsOn = s.GetBoolean("selection-uia-enabled");
            MouseFallbackToggle.IsOn = s.GetBoolean("selection-mouse-fallback");
            ClipboardFallbackToggle.IsOn = s.GetBoolean("clipboard-fallback");
            DebugLoggingToggle.IsOn = s.GetBoolean("debug-logging");

            RefreshAllShortcutButtons();
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshAllShortcutButtons()
    {
        RefreshShortcutCard("ask-shortcut", AskShortcutCard, AskShortcutButton, AskClearButton, "Prompt AI with a question about your selected text.");
        RefreshShortcutCard("correct-shortcut", CorrectShortcutCard, CorrectShortcutButton, CorrectClearButton, "Fix grammar, spelling, and punctuation immediately.");
        RefreshShortcutCard("rewrite-shortcut", RewriteShortcutCard, RewriteShortcutButton, RewriteClearButton, "Improve clarity and natural flow.");
        RefreshShortcutCard("actions-shortcut", ActionsShortcutCard, ActionsShortcutButton, ActionsClearButton, "Show the floating actions menu anywhere.");
    }

    private void RefreshShortcutCard(
        string key,
        CommunityToolkit.WinUI.Controls.SettingsCard card,
        Button recordBtn,
        Button clearBtn,
        string defaultDesc)
    {
        if (_context == null) return;
        var cur = _context.Settings.GetStrv(key).FirstOrDefault();
        recordBtn.Content = GlobalHotkeyManager.FormatAcceleratorForDisplay(cur);
        clearBtn.Visibility = string.IsNullOrEmpty(cur) ? Visibility.Collapsed : Visibility.Visible;

        var conflict = !string.IsNullOrEmpty(cur) && _context.IsShortcutRegistered != null && !_context.IsShortcutRegistered(key);
        card.Description = conflict
            ? "Conflict: Windows or another app already registered this shortcut."
            : defaultDesc;
    }

    private async void ShortcutRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_context == null || sender is not Button btn || btn.Tag is not string settingKey)
            return;

        var title = settingKey switch
        {
            "ask-shortcut" => "Ask",
            "correct-shortcut" => "Correct",
            "rewrite-shortcut" => "Rewrite",
            _ => "Open actions"
        };

        var panel = new StackPanel { Spacing = 8 };
        var promptText = new TextBlock
        {
            Text = "Press a key combination using Ctrl, Alt, or Shift.\nPress Backspace to clear, or Escape to cancel.",
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(promptText);

        var dlg = new ContentDialog
        {
            Title = $"Set {title} shortcut",
            Content = panel,
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            RequestedTheme = ThemeHelper.ResolveElementTheme(_context.Settings)
        };

        dlg.PreviewKeyDown += (_, ke) =>
        {
            static bool IsDown(VirtualKey k) =>
                (InputKeyboardSource.GetKeyStateForCurrentThread(k) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

            if (ke.Key == VirtualKey.Escape)
                return; // ContentDialog handles Escape cleanly

            ke.Handled = true;

            if (ke.Key == VirtualKey.Back)
            {
                _context.Settings.SetStrv(settingKey, Array.Empty<string>());
                _context.OnShortcutsChanged();
                RefreshAllShortcutButtons();
                dlg.Hide();
                return;
            }

            if (ke.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
                or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
                or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
                or VirtualKey.LeftWindows or VirtualKey.RightWindows)
            {
                return;
            }

            var ctrl = IsDown(VirtualKey.Control);
            var alt = IsDown(VirtualKey.Menu);
            var shift = IsDown(VirtualKey.Shift);
            if (!ctrl && !alt && !shift)
                return;

            var parts = new List<string>();
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            parts.Add(ke.Key.ToString());
            var accel = string.Join("+", parts);

            _context.Settings.SetStrv(settingKey, new[] { accel });
            _context.OnShortcutsChanged();
            RefreshAllShortcutButtons();
            dlg.Hide();
        };

        await dlg.ShowAsync();
    }

    private void ShortcutClear_Click(object sender, RoutedEventArgs e)
    {
        if (_context == null || sender is not Button btn || btn.Tag is not string key)
            return;
        _context.Settings.SetStrv(key, Array.Empty<string>());
        _context.OnShortcutsChanged();
        RefreshAllShortcutButtons();
    }

    private void PalettePosCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _context == null || PalettePosCombo.SelectedIndex < 0) return;
        _context.Settings.SetString("action-palette-position", PalettePositions[PalettePosCombo.SelectedIndex]);
    }

    private void UiaToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _context == null) return;
        _context.Settings.SetBoolean("selection-uia-enabled", UiaToggle.IsOn);
    }

    private void MouseFallbackToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _context == null) return;
        _context.Settings.SetBoolean("selection-mouse-fallback", MouseFallbackToggle.IsOn);
    }

    private void ClipboardFallbackToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _context == null) return;
        _context.Settings.SetBoolean("clipboard-fallback", ClipboardFallbackToggle.IsOn);
    }

    private void DebugLoggingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _context == null) return;
        _context.Settings.SetBoolean("debug-logging", DebugLoggingToggle.IsOn);
    }

    private async void ViewLogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_context == null) return;
        var box = new TextBox
        {
            Text = DiagnosticLogStore.GetSnapshot(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            MinWidth = 480,
            MinHeight = 260,
            MaxHeight = 360
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);

        var dlg = new ContentDialog
        {
            Title = "Plyph Request Diagnostics",
            Content = box,
            PrimaryButtonText = "Clear logs",
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
            RequestedTheme = ThemeHelper.ResolveElementTheme(_context.Settings)
        };
        dlg.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            DiagnosticLogStore.Clear();
            box.Text = DiagnosticLogStore.GetSnapshot();
        };
        await dlg.ShowAsync();
    }
}
