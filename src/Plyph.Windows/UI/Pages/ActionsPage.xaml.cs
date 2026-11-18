using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Plyph.Core;

namespace Plyph.Windows.UI.Pages;

public sealed partial class ActionsPage : Page
{
    private SettingsHostContext? _context;
    private List<string> _runPromptProviderIds = new();
    private bool _loading;

    public ActionsPage()
    {
        InitializeComponent();
        RenderActionLibrary();
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

    private FontIcon MakeIcon(string glyph)
    {
        var ff = Application.Current.Resources["SymbolThemeFontFamily"] as FontFamily;
        var icon = new FontIcon { Glyph = glyph };
        if (ff != null)
            icon.FontFamily = ff;
        return icon;
    }

    private void LoadState()
    {
        if (_context == null) return;
        _loading = true;
        try
        {
            var s = _context.Settings;

            CorrectCtxToggle.IsOn = s.GetBoolean("correct-use-personal-preferences");
            RewriteCtxToggle.IsOn = s.GetBoolean("rewrite-use-personal-preferences");
            RunPromptCtxToggle.IsOn = s.GetBoolean("prompt-use-personal-preferences");
            AskCtxToggle.IsOn = s.GetBoolean("ask-use-personal-preferences");

            CorrectPromptBox.Text = s.GetString("prompt-correct");
            RewritePromptBox.Text = s.GetString("prompt-rewrite");
            RunPromptBox.Text = s.GetString("prompt-run");

            var choices = ProvidersStore.ListProviders(s);
            _runPromptProviderIds = new List<string> { "" };
            _runPromptProviderIds.AddRange(choices.Select(c => c.Id));
            var names = new List<string> { "Use active provider" };
            names.AddRange(ProvidersStore.ProviderLabels(choices));
            var curProv = s.GetString("prompt-run-provider");
            if (!_runPromptProviderIds.Contains(curProv))
            {
                _runPromptProviderIds.Add(curProv);
                names.Add("Unavailable provider");
            }
            RunPromptProviderCombo.Items.Clear();
            foreach (var n in names)
                RunPromptProviderCombo.Items.Add(n);
            RunPromptProviderCombo.SelectedIndex = Math.Max(0, _runPromptProviderIds.IndexOf(curProv));

            RunPromptModelBox.Text = s.GetString("prompt-run-model");
            RunPromptModelBox.IsEnabled = !string.IsNullOrEmpty(curProv);
            RunPromptInputLimitBox.Value = s.GetInt64("prompt-run-input-limit");
            RunPromptOutputLimitBox.Value = s.GetInt64("prompt-run-output-limit");


            RenderCustomActions();
        }
        finally
        {
            _loading = false;
        }
    }

    private void RenderActionLibrary()
    {
        foreach (var template in ActionLibrary.Templates)
        {
            var add = new Button { Content = "Use template" };
            AutomationProperties.SetName(add, $"Use {template.Name} template");
            add.Click += async (_, _) => await ShowEditActionDialogAsync(null, template);

            ActionLibraryStack.Children.Add(new SettingsCard
            {
                Header = template.Name,
                Description = template.Description,
                HeaderIcon = ActionIcons.Create(template.Icon),
                Content = add
            });
        }
    }

    private void RenderCustomActions()
    {
        if (_context == null) return;
        CustomActionsStack.Children.Clear();
        var settings = _context.Settings;
        var stored = ActionsStore.ReadActions(settings);

        for (var i = 0; i < stored.Count; i++)
        {
            var idx = i;
            var action = stored[i];

            var toggle = new ToggleSwitch { IsOn = action.Enabled, OnContent = "", OffContent = "", MinWidth = 0 };
            toggle.Toggled += (_, _) =>
            {
                var all = ActionsStore.ReadActions(settings);
                var target = all.FirstOrDefault(a => a.Id == action.Id);
                if (target != null)
                {
                    target.Enabled = toggle.IsOn;
                    ActionsStore.WriteActions(settings, all);
                }
            };

            var upBtn = new Button { Content = MakeIcon("\uE74A"), IsEnabled = idx > 0 };
            AutomationProperties.SetName(upBtn, $"Move {action.Name} up");
            upBtn.Click += (_, _) => MoveCustomAction(action.Id, -1);

            var downBtn = new Button { Content = MakeIcon("\uE74B"), IsEnabled = idx < stored.Count - 1 };
            AutomationProperties.SetName(downBtn, $"Move {action.Name} down");
            downBtn.Click += (_, _) => MoveCustomAction(action.Id, 1);

            var editBtn = new Button { Content = "Edit" };
            editBtn.Click += async (_, _) => await ShowEditActionDialogAsync(action);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(toggle);
            row.Children.Add(upBtn);
            row.Children.Add(downBtn);
            row.Children.Add(editBtn);

            var compactPrompt = action.Prompt.Replace("\r", " ").Replace("\n", " ").Trim();
            if (compactPrompt.Length > 72) compactPrompt = compactPrompt[..72] + "…";
            var sub = $"{(action.InputMode == "prompt" ? "Run as prompt" : "Transform")} · {compactPrompt}";

            CustomActionsStack.Children.Add(new SettingsCard
            {
                Header = action.Name,
                Description = sub,
                HeaderIcon = ActionIcons.Create(action.Icon),
                Content = row
            });
        }

        var addBtn = new Button
        {
            Content = "+ New custom action",
            Style = Application.Current.Resources["AccentButtonStyle"] as Style,
            Margin = new Thickness(0, 4, 0, 0)
        };
        addBtn.Click += async (_, _) => await ShowEditActionDialogAsync(null);
        CustomActionsStack.Children.Add(addBtn);
    }

    private void MoveCustomAction(string id, int offset)
    {
        if (_context == null) return;
        var actions = ActionsStore.ReadActions(_context.Settings);
        var idx = actions.FindIndex(a => a.Id == id);
        var target = idx + offset;
        if (idx < 0 || target < 0 || target >= actions.Count) return;
        (actions[idx], actions[target]) = (actions[target], actions[idx]);
        ActionsStore.WriteActions(_context.Settings, actions);
        RenderCustomActions();
    }

    private async Task ShowEditActionDialogAsync(PlyphAction? existing, ActionTemplate? template = null)
    {
        if (_context == null) return;
        var settings = _context.Settings;

        var formStack = new StackPanel { Spacing = 10, MinWidth = 440 };
        var nameBox = new TextBox { Header = "Name", Text = existing?.Name ?? template?.Name ?? "" };
        var promptBox = new TextBox
        {
            Header = "Prompt / Instructions",
            Text = existing?.Prompt ?? template?.Prompt ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 110
        };
        ScrollViewer.SetVerticalScrollBarVisibility(promptBox, ScrollBarVisibility.Auto);
        var enabledToggle = new ToggleSwitch { Header = "Show in actions menu", IsOn = existing?.Enabled ?? true };

        var iconCombo = new ComboBox { Header = "Action icon", HorizontalAlignment = HorizontalAlignment.Stretch };
        var iconKey = existing?.Icon ?? template?.Icon ?? "custom";
        foreach (var choice in ActionIcons.Choices)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(ActionIcons.Create(choice.Key));
            row.Children.Add(new TextBlock { Text = choice.Label });
            var item = new ComboBoxItem { Content = row, Tag = choice.Key };
            AutomationProperties.SetName(item, choice.Label);
            iconCombo.Items.Add(item);
            if (choice.Key == iconKey)
                iconCombo.SelectedItem = item;
        }
        if (iconCombo.SelectedIndex < 0)
            iconCombo.SelectedIndex = 0;

        formStack.Children.Add(nameBox);
        formStack.Children.Add(iconCombo);
        formStack.Children.Add(promptBox);
        formStack.Children.Add(enabledToggle);

        var modeCombo = new ComboBox { Header = "Input mode", HorizontalAlignment = HorizontalAlignment.Stretch };
        modeCombo.Items.Add("Transform text");
        modeCombo.Items.Add("Run as prompt");
        modeCombo.SelectedIndex = existing?.InputMode == "prompt" ? 1 : 0;

        var personalToggle = new ToggleSwitch { Header = "Use personal context", IsOn = existing?.UsePersonalPreferences ?? (template == null) };

        var choices = ProvidersStore.ListProviders(settings);
        var provCombo = new ComboBox { Header = "Provider override", HorizontalAlignment = HorizontalAlignment.Stretch };
        provCombo.Items.Add("Use active provider");
        foreach (var label in ProvidersStore.ProviderLabels(choices))
            provCombo.Items.Add(label);
        provCombo.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Id == existing?.Provider) + 1);

        var modelBox = new TextBox
        {
            Header = "Model override",
            Text = existing?.Model ?? "",
            IsEnabled = provCombo.SelectedIndex > 0
        };
        provCombo.SelectionChanged += (_, _) => modelBox.IsEnabled = provCombo.SelectedIndex > 0;

        var inLimBox = new NumberBox
        {
            Header = "Input token limit (0 = Auto)",
            Minimum = 0,
            Maximum = 1_000_000,
            SmallChange = 1000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = existing?.InputLimit ?? 0
        };
        var outLimBox = new NumberBox
        {
            Header = "Output token limit (0 = Auto)",
            Minimum = 0,
            Maximum = 1_000_000,
            SmallChange = 500,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = existing?.OutputLimit ?? 0
        };

        var advStack = new StackPanel { Spacing = 8 };
        advStack.Children.Add(modeCombo);
        advStack.Children.Add(personalToggle);
        advStack.Children.Add(provCombo);
        advStack.Children.Add(modelBox);
        advStack.Children.Add(inLimBox);
        advStack.Children.Add(outLimBox);

        formStack.Children.Add(new Expander
        {
            Header = "Advanced options",
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = advStack
        });

        var errBar = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false, IsClosable = false };
        formStack.Children.Add(errBar);

        var scroll = new ScrollViewer
        {
            Content = formStack,
            MaxHeight = 460,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var dlg = new ContentDialog
        {
            Title = existing != null ? "Edit action" : template != null ? "Add library action" : "New custom action",
            Content = scroll,
            PrimaryButtonText = template == null ? "Save" : "Add action",
            CloseButtonText = "Cancel",
            SecondaryButtonText = existing != null ? "Delete" : null,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ThemeHelper.ResolveElementTheme(settings)
        };

        dlg.SecondaryButtonClick += (_, _) =>
        {
            if (existing != null)
            {
                ActionsStore.WriteActions(settings, ActionsStore.ReadActions(settings).Where(a => a.Id != existing.Id).ToList());
                RenderCustomActions();
            }
        };

        dlg.PrimaryButtonClick += (_, args) =>
        {
            var actName = nameBox.Text.Trim();
            var actPrompt = promptBox.Text.Trim();
            var inputModeVal = modeCombo.SelectedIndex == 1 ? "prompt" : "transform";

            if (string.IsNullOrEmpty(actName) || (inputModeVal == "transform" && string.IsNullOrEmpty(actPrompt)))
            {
                args.Cancel = true;
                errBar.Message = "Enter a name and instructions before saving.";
                errBar.IsOpen = true;
                return;
            }

            var provId = provCombo.SelectedIndex > 0 ? choices[provCombo.SelectedIndex - 1].Id : "";
            var next = new PlyphAction
            {
                Id = existing?.Id ?? Guid.NewGuid().ToString("D"),
                Name = actName,
                Prompt = actPrompt,
                Icon = (iconCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "custom",
                Enabled = enabledToggle.IsOn,
                Provider = provId,
                Model = !string.IsNullOrEmpty(provId) ? modelBox.Text.Trim() : "",
                InputMode = inputModeVal,
                UsePersonalPreferences = personalToggle.IsOn,
                InputLimit = double.IsNaN(inLimBox.Value) ? 0 : (long)Math.Round(inLimBox.Value),
                OutputLimit = double.IsNaN(outLimBox.Value) ? 0 : (long)Math.Round(outLimBox.Value)
            };

            var list = ActionsStore.ReadActions(settings);
            var idx = list.FindIndex(a => a.Id == next.Id);
            if (idx >= 0) list[idx] = next; else list.Add(next);
            ActionsStore.WriteActions(settings, list);
            RenderCustomActions();
        };

        await dlg.ShowAsync();
    }

    private void BuiltInCtxToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _context == null || sender is not ToggleSwitch ts || ts.Tag is not string mode) return;
        _context.Settings.SetBoolean($"{mode}-use-personal-preferences", ts.IsOn);
    }

    private void BuiltInPromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _context == null || sender is not TextBox tb || tb.Tag is not string key) return;
        _context.Settings.SetString(key, tb.Text);
    }

    private void RunPromptProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _context == null) return;
        var idx = RunPromptProviderCombo.SelectedIndex;
        if (idx >= 0 && idx < _runPromptProviderIds.Count)
        {
            var id = _runPromptProviderIds[idx];
            _context.Settings.SetString("prompt-run-provider", id);
            RunPromptModelBox.IsEnabled = !string.IsNullOrEmpty(id);
        }
    }

    private void RunPromptModelBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && _context != null)
            _context.Settings.SetString("prompt-run-model", RunPromptModelBox.Text);
    }

    private void RunPromptInputLimitBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_loading && _context != null)
            _context.Settings.SetInt64("prompt-run-input-limit", double.IsNaN(args.NewValue) ? 0 : (long)Math.Round(args.NewValue));
    }

    private void RunPromptOutputLimitBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_loading && _context != null)
            _context.Settings.SetInt64("prompt-run-output-limit", double.IsNaN(args.NewValue) ? 0 : (long)Math.Round(args.NewValue));
    }
}
