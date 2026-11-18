using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Plyph.Core;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Plyph.Windows.UI.Pages;

public sealed partial class ToolbarPage : Page
{
    private static readonly string[] Layouts = ["button", "bar", "icons"];
    private static readonly string[] Positions = ["selection", "cursor"];
    private static readonly double[] Scales = [0.85, 1.0, 1.15, 1.25, 1.5];
    private static readonly string[] FeedbackPositions =
    [
        "top-left", "top-center", "top-right",
        "center-left", "center", "center-right",
        "bottom-left", "bottom-center", "bottom-right"
    ];

    private SettingsHostContext? _context;
    private IPlyphSettings? Settings => _context?.Settings;

    public ToolbarPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not SettingsHostContext context) return;

        _context = null;
        var settings = context.Settings;
        ShowFloatingToggle.IsOn = settings.GetBoolean("show-floating-button");
        ToolbarLayoutCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Layouts, settings.GetString("floating-toolbar-layout")));
        ToolbarOrientationCombo.SelectedIndex = settings.GetString("floating-toolbar-orientation") == "vertical" ? 1 : 0;
        ToolbarPositionCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Positions, settings.GetString("floating-toolbar-position")));
        var scale = settings.GetDouble("floating-toolbar-scale");
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1.0;
        ScaleCombo.SelectedIndex = Array.IndexOf(Scales, Scales.MinBy(s => Math.Abs(s - scale)));
        ToolbarAccentToggle.IsOn = settings.GetBoolean("toolbar-accent-icon");
        FeedbackToggle.IsOn = settings.GetBoolean("pointer-feedback");
        var position = Array.IndexOf(FeedbackPositions, settings.GetString("feedback-position"));
        FeedbackPosCombo.SelectedIndex = position >= 0 ? position : 7;
        _context = context;

        UpdateControlAvailability();
        RenderQuickActions();
        RenderExcludedApps();
    }

    private void UpdateControlAvailability()
    {
        ToolbarLayoutCombo.IsEnabled = ToolbarOrientationCombo.IsEnabled = ToolbarPositionCombo.IsEnabled =
            ScaleCombo.IsEnabled = ToolbarAccentToggle.IsEnabled = ShowFloatingToggle.IsOn;
        FeedbackPosCombo.IsEnabled = FeedbackToggle.IsOn;
    }

    private void ShowFloatingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (Settings is not { } settings) return;
        settings.SetBoolean("show-floating-button", ShowFloatingToggle.IsOn);
        UpdateControlAvailability();
    }

    private void ToolbarLayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToolbarLayoutCombo.SelectedIndex >= 0)
            Settings?.SetString("floating-toolbar-layout", Layouts[ToolbarLayoutCombo.SelectedIndex]);
    }

    private void ToolbarPositionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToolbarPositionCombo.SelectedIndex >= 0)
            Settings?.SetString("floating-toolbar-position", Positions[ToolbarPositionCombo.SelectedIndex]);
    }

    private void ToolbarOrientationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToolbarOrientationCombo.SelectedIndex >= 0)
            Settings?.SetString("floating-toolbar-orientation", ToolbarOrientationCombo.SelectedIndex == 1 ? "vertical" : "horizontal");
    }

    private void ToolbarAccentToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_context is not { } context) return;
        context.Settings.SetBoolean("toolbar-accent-icon", ToolbarAccentToggle.IsOn);
        context.OnThemeChanged();
    }

    private void ScaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScaleCombo.SelectedIndex >= 0)
            Settings?.SetDouble("floating-toolbar-scale", Scales[ScaleCombo.SelectedIndex]);
    }

    private void FeedbackToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (Settings is not { } settings) return;
        settings.SetBoolean("pointer-feedback", FeedbackToggle.IsOn);
        UpdateControlAvailability();
    }

    private void FeedbackPosCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FeedbackPosCombo.SelectedIndex >= 0)
            Settings?.SetString("feedback-position", FeedbackPositions[FeedbackPosCombo.SelectedIndex]);
    }

    private void RenderQuickActions()
    {
        if (Settings is not { } settings) return;
        QuickActionsStack.Children.Clear();

        var allActions = ActionsStore.GetAvailableActions(settings, includeDisabled: true);
        var pinned = ActionsStore.GetQuickActionIds(settings)
            .Select(id => allActions.FirstOrDefault(action => action.Id == id) ??
                allActions.FirstOrDefault(action => action.Mode == id))
            .OfType<PlyphAction>()
            .DistinctBy(action => action.Id)
            .ToList();

        void SavePinned(IEnumerable<PlyphAction> actions)
        {
            settings.SetString("quick-actions", JsonSerializer.Serialize(actions.Select(action => action.Id)));
            RenderQuickActions();
        }

        void MoveAction(int index, int offset)
        {
            var target = index + offset;
            (pinned[index], pinned[target]) = (pinned[target], pinned[index]);
            SavePinned(pinned);
        }

        for (var index = 0; index < pinned.Count; index++)
        {
            var position = index;
            var action = pinned[index];
            var up = new Button { Content = ActionIcons.CreateGlyph("\uE74A"), IsEnabled = index > 0 };
            AutomationProperties.SetName(up, $"Move {action.Name} up");
            up.Click += (_, _) => MoveAction(position, -1);

            var down = new Button { Content = ActionIcons.CreateGlyph("\uE74B"), IsEnabled = index < pinned.Count - 1 };
            AutomationProperties.SetName(down, $"Move {action.Name} down");
            down.Click += (_, _) => MoveAction(position, 1);

            var remove = new Button { Content = "Remove" };
            AutomationProperties.SetName(remove, $"Unpin {action.Name}");
            remove.Click += (_, _) => SavePinned(pinned.Where(item => item.Id != action.Id));

            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            controls.Children.Add(up);
            controls.Children.Add(down);
            controls.Children.Add(remove);
            QuickActionsStack.Children.Add(new SettingsCard
            {
                Header = action.Name,
                Description = action.Enabled ? "" : "Disabled in Actions & Prompts",
                HeaderIcon = ActionIcons.Create(action.Icon),
                Content = controls
            });
        }

        var available = allActions.Where(action => action.Enabled && !pinned.Contains(action)).ToList();
        if (pinned.Count == 4 || available.Count == 0) return;

        var add = new ComboBox { PlaceholderText = "Choose action", MinWidth = 160, MaxWidth = 220 };
        foreach (var action in available)
            add.Items.Add(action.Name);
        add.SelectionChanged += (_, _) =>
        {
            if (add.SelectedIndex >= 0)
                SavePinned(pinned.Append(available[add.SelectedIndex]));
        };
        QuickActionsStack.Children.Add(new SettingsCard { Header = "Pin an action", Content = add });
    }

    private void RenderExcludedApps()
    {
        if (Settings is not { } settings) return;
        ExcludedAppsStack.Children.Clear();

        var rules = AppExclusions.Read(settings);
        if (rules.Count == 0)
        {
            ExcludedAppsStack.Children.Add(new TextBlock
            {
                Text = "No excluded apps",
                Style = Application.Current.Resources["PlyphCardDescriptionStyle"] as Style
            });
        }

        foreach (var rule in rules)
        {
            var name = AppExclusions.DisplayName(rule);
            var remove = new Button { Content = "Remove" };
            AutomationProperties.SetName(remove, $"Remove exclusion for {name}");
            remove.Click += (_, _) =>
            {
                settings.SetString("excluded-apps", string.Join(", ", AppExclusions.Read(settings).Where(item => item != rule)));
                ExclusionStatus.IsOpen = false;
                RenderExcludedApps();
            };
            ExcludedAppsStack.Children.Add(new SettingsCard
            {
                Header = name,
                HeaderIcon = new SymbolIcon(Symbol.BlockContact),
                Content = remove
            });
        }
    }

    private void AddExclusion(string rule)
    {
        if (Settings is not { } settings) return;
        var rules = AppExclusions.Read(settings);
        var processName = AppExclusions.DisplayName(rule);
        if (rules.Any(item => AppExclusions.Matches(item, processName, null)))
        {
            ShowExclusionStatus($"{AppExclusions.DisplayName(rule)} is already excluded.");
            return;
        }

        rules.Add(rule);
        settings.SetString("excluded-apps", string.Join(", ", rules));
        ExclusionStatus.IsOpen = false;
        RenderExcludedApps();
    }

    private async void BrowseForApp_Click(object sender, RoutedEventArgs e)
    {
        if (_context is not { } context) return;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, context.WindowHandle);
            if (await picker.PickSingleFileAsync() is { } file)
                AddExclusion(AppExclusions.ForProcess(file.Name));
        }
        catch (Exception)
        {
            ShowExclusionStatus("The file picker could not open. You can choose a running app instead.");
        }
    }

    private async void ChooseRunningApp_Click(object sender, RoutedEventArgs e)
    {
        if (Settings is not { } settings || sender is not Button button) return;

        button.IsEnabled = false;
        try
        {
            var excluded = AppExclusions.Read(settings);
            var apps = (await Task.Run(GetRunningApps))
                .Where(app => !excluded.Any(rule => AppExclusions.Matches(rule, app.ProcessName, null)))
                .ToList();
            if (XamlRoot != null)
                await ShowRunningAppPickerAsync(apps);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            ShowExclusionStatus("The app chooser could not open. Try again or browse for an executable.");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task ShowRunningAppPickerAsync(List<RunningApp> apps)
    {
        var search = new TextBox { PlaceholderText = "Search running apps" };
        AutomationProperties.SetName(search, "Search running apps");
        var results = new ListView { MaxHeight = 280 };
        AutomationProperties.SetName(results, "Running apps");
        var empty = new TextBlock();
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(search);
        panel.Children.Add(results);
        panel.Children.Add(empty);

        var dialog = new ContentDialog
        {
            Title = "Choose a running app",
            Content = panel,
            PrimaryButtonText = "Exclude",
            CloseButtonText = "Cancel",
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot
        };

        void FilterApps()
        {
            var query = search.Text.Trim();
            results.Items.Clear();
            foreach (var app in apps.Where(app => app.Label.Contains(query, StringComparison.OrdinalIgnoreCase)))
                results.Items.Add(new ListViewItem { Content = app.Label, Tag = app });
            empty.Text = apps.Count == 0 ? "No available apps. Open an app or browse for its executable." : "No matching apps.";
            empty.Visibility = results.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        search.TextChanged += (_, _) => FilterApps();
        results.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = results.SelectedItem != null;
        FilterApps();

        if (await dialog.ShowAsync() == ContentDialogResult.Primary &&
            results.SelectedItem is ListViewItem { Tag: RunningApp app })
            AddExclusion(AppExclusions.ForProcess(app.ProcessName));
    }

    private static List<RunningApp> GetRunningApps()
    {
        var apps = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero) continue;
                    var name = process.ProcessName;
                    string? product = null;
                    try { product = process.MainModule?.FileVersionInfo.FileDescription; }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                    var label = string.IsNullOrWhiteSpace(product) ? $"{name}.exe" : $"{product} ({name}.exe)";
                    apps.TryAdd(name, new RunningApp(name, label));
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return apps.Values.OrderBy(app => app.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void ShowExclusionStatus(string message)
    {
        ExclusionStatus.Message = message;
        ExclusionStatus.IsOpen = true;
    }

    private sealed record RunningApp(string ProcessName, string Label);
}
