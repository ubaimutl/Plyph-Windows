using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Plyph.Core;

namespace Plyph.Windows.UI.Pages;

public sealed partial class ProvidersPage : Page
{
    private SettingsHostContext? _context;
    private CancellationTokenSource _cts = new();
    private ChatGptClient? _chatGptClient;
    private List<string> _activeProviderIds = new();
    private bool _loading;

    public ProvidersPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is SettingsHostContext ctx)
        {
            _context = ctx;
            RefreshProviderSelector();
            RenderDynamicProvider();
            RenderSavedProviders();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        _chatGptClient?.Destroy();
        _chatGptClient = null;
        ModelsClient.AbortModelRequests();
    }

    private FontIcon MakeIcon(string glyph)
    {
        var ff = Application.Current.Resources["SymbolThemeFontFamily"] as FontFamily;
        var icon = new FontIcon { Glyph = glyph, FontSize = 16 };
        if (ff != null)
            icon.FontFamily = ff;
        return icon;
    }

    private Border CreateFullWidthCard(string? iconGlyph, string header, string description, UIElement body)
    {
        var headerRow = new Grid { ColumnSpacing = 14 };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (!string.IsNullOrEmpty(iconGlyph))
        {
            var icon = MakeIcon(iconGlyph);
            icon.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(icon, 0);
            headerRow.Children.Add(icon);
        }

        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = header,
            Style = Application.Current.Resources["BodyStrongTextBlockStyle"] as Style,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrEmpty(description))
        {
            textStack.Children.Add(new TextBlock
            {
                Text = description,
                Style = Application.Current.Resources["PlyphCardDescriptionStyle"] as Style
            });
        }
        Grid.SetColumn(textStack, 1);
        headerRow.Children.Add(textStack);

        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(headerRow);
        root.Children.Add(body);

        return new Border
        {
            Style = Application.Current.Resources["PlyphFullWidthCardStyle"] as Style,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = root
        };
    }

    private void RefreshProviderSelector()
    {
        if (_context == null) return;
        _loading = true;
        try
        {
            var choices = ProvidersStore.ListProviders(_context.Settings);
            var labels = ProvidersStore.ProviderLabels(choices).ToList();
            _activeProviderIds = choices.Select(c => c.Id).ToList();

            var current = _context.Settings.GetString("provider");
            if (!_activeProviderIds.Contains(current))
            {
                _activeProviderIds.Add(current);
                labels.Add("Unavailable provider");
            }

            ActiveProviderCombo.Items.Clear();
            foreach (var name in labels)
                ActiveProviderCombo.Items.Add(name);
            ActiveProviderCombo.SelectedIndex = Math.Max(0, _activeProviderIds.IndexOf(current));
        }
        finally
        {
            _loading = false;
        }
    }

    private void ActiveProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _context == null) return;
        var idx = ActiveProviderCombo.SelectedIndex;
        if (idx >= 0 && idx < _activeProviderIds.Count)
        {
            _context.Settings.SetString("provider", _activeProviderIds[idx]);
            RenderDynamicProvider();
        }
    }

    private void RenderDynamicProvider()
    {
        if (_context == null) return;
        _chatGptClient?.Destroy();
        _chatGptClient = null;
        ModelsClient.AbortModelRequests();
        DynamicProviderStack.Children.Clear();

        var settings = _context.Settings;
        var provider = settings.GetString("provider");

        ProviderProfile info;
        try
        {
            info = ProvidersStore.ResolveProvider(settings, provider);
        }
        catch (Exception ex)
        {
            DynamicProviderStack.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Error,
                IsOpen = true,
                IsClosable = false,
                Title = "Provider error",
                Message = ex.Message
            });
            return;
        }

        var credSubtitle = settings.Mode == StorageMode.Portable
            ? "Encrypted locally inside Portable data folder."
            : "Stored securely in Windows Credential Manager.";

        if (provider == "chatgpt")
        {
            BuildChatGptSection();
        }
        else if (provider == "ollama")
        {
            var urlBox = new TextBox
            {
                Text = settings.GetString("ollama-url"),
                PlaceholderText = "http://127.0.0.1:11434",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            urlBox.TextChanged += (_, _) => settings.SetString("ollama-url", urlBox.Text);
            DynamicProviderStack.Children.Add(CreateFullWidthCard(
                "\uE774",
                "Ollama server URL",
                "Runs locally on your PC or LAN.",
                urlBox));
        }
        else if (info.Url != null)
        {
            var editBtn = new Button { Content = "Edit connection" };
            editBtn.Click += async (_, _) => await ShowEditProviderDialogAsync(info);
            DynamicProviderStack.Children.Add(new SettingsCard
            {
                Header = info.Name,
                Description = $"Endpoint: {info.Url}",
                HeaderIcon = MakeIcon("\uE71B"),
                Content = editBtn
            });
        }
        else if (provider == "cloudflare")
        {
            var accBox = new TextBox
            {
                Text = settings.GetString("cloudflare-account-id"),
                PlaceholderText = "Cloudflare Account ID",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            accBox.TextChanged += (_, _) => settings.SetString("cloudflare-account-id", accBox.Text);
            DynamicProviderStack.Children.Add(CreateFullWidthCard(
                "\uE77B",
                "Cloudflare Account ID",
                "Found in your Cloudflare Workers AI dashboard.",
                accBox));
            DynamicProviderStack.Children.Add(CreateSecretCard(provider, "API token", credSubtitle));
        }
        else
        {
            DynamicProviderStack.Children.Add(CreateSecretCard(provider, "API key", credSubtitle));
        }

        DynamicProviderStack.Children.Add(CreateModelPickerCard(provider));

        if (provider == "chatgpt")
        {
            var efforts = new[] { "auto", "low", "medium", "high" };
            var effCombo = new ComboBox { MinWidth = 150 };
            foreach (var lbl in new[] { "Auto", "Low", "Medium", "High" })
                effCombo.Items.Add(lbl);
            effCombo.SelectedIndex = Math.Max(0, Array.IndexOf(efforts, settings.GetString("chatgpt-thinking-effort")));
            effCombo.SelectionChanged += (_, _) =>
            {
                if (effCombo.SelectedIndex >= 0)
                    settings.SetString("chatgpt-thinking-effort", efforts[effCombo.SelectedIndex]);
            };

            var advExpander = new SettingsExpander
            {
                Header = "Advanced ChatGPT Options",
                Description = "Optional reasoning depth setting.",
                HeaderIcon = MakeIcon("\uE9E9"),
                IsExpanded = false
            };
            advExpander.Items.Add(new SettingsCard
            {
                Header = "Thinking effort",
                Description = "Higher effort can produce deeper reasoning but takes longer.",
                Content = effCombo
            });
            DynamicProviderStack.Children.Add(advExpander);
        }
    }

    private Border CreateSecretCard(string provider, string title, string subtitle)
    {
        var settings = _context!.Settings;
        var backend = _context.SecretBackend;
        var dq = DispatcherQueue;

        var passBox = new PasswordBox
        {
            PlaceholderText = "Paste secret here…",
            PasswordRevealMode = PasswordRevealMode.Peek,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var saveBtn = new Button
        {
            Content = "Save",
            Style = Application.Current.Resources["AccentButtonStyle"] as Style
        };

        var panel = new Grid { ColumnSpacing = 8 };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(passBox, 0);
        Grid.SetColumn(saveBtn, 1);
        panel.Children.Add(passBox);
        panel.Children.Add(saveBtn);

        _ = Task.Run(async () =>
        {
            try
            {
                var saved = await SecretsStore.GetApiKeyAsync(settings, backend, provider, _cts.Token, interactive: false);
                dq.TryEnqueue(() => passBox.Password = saved);
            }
            catch
            {
            }
        });

        saveBtn.Click += async (_, _) =>
        {
            saveBtn.IsEnabled = false;
            try
            {
                await SecretsStore.SetApiKeyAsync(settings, backend, provider, passBox.Password, _cts.Token);
                saveBtn.Content = "Saved";
                await Task.Delay(1200);
            }
            catch (Exception ex)
            {
                var err = new ContentDialog
                {
                    Title = "Could not save secret",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot = XamlRoot,
                    RequestedTheme = ThemeHelper.ResolveElementTheme(settings)
                };
                await err.ShowAsync();
            }
            finally
            {
                saveBtn.Content = "Save";
                saveBtn.IsEnabled = true;
            }
        };

        return CreateFullWidthCard("\uE72E", title, subtitle, panel);
    }

    private StackPanel CreateModelPickerCard(string provider)
    {
        var settings = _context!.Settings;
        var backend = _context.SecretBackend;

        var container = new StackPanel { Spacing = 6 };
        var modelCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var refreshBtn = new Button { Content = "Refresh" };
        var customBtn = new Button
        {
            Content = "Custom…",
            Visibility = provider == "chatgpt" ? Visibility.Collapsed : Visibility.Visible
        };

        var controlsRow = new Grid { ColumnSpacing = 8 };
        controlsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controlsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controlsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(modelCombo, 0);
        Grid.SetColumn(refreshBtn, 1);
        Grid.SetColumn(customBtn, 2);
        controlsRow.Children.Add(modelCombo);
        controlsRow.Children.Add(refreshBtn);
        controlsRow.Children.Add(customBtn);

        var card = CreateFullWidthCard(
            "\uE99A",
            "Model",
            "Choose or refresh available models from this provider.",
            controlsRow);

        var statusBar = new InfoBar
        {
            IsOpen = false,
            IsClosable = true
        };

        var modelIds = new List<string>();
        var updatingModels = false;

        void Populate(List<ModelItem> models)
        {
            updatingModels = true;
            var curModel = ProvidersStore.ProviderModel(settings, provider);
            var list = new List<ModelItem>(models);
            if (string.IsNullOrEmpty(curModel) && provider != "chatgpt")
                list.Insert(0, new ModelItem("", "Choose a model"));
            if (provider != "chatgpt" && !string.IsNullOrEmpty(curModel) && !list.Any(m => m.Id == curModel))
                list.Insert(0, new ModelItem(curModel, curModel));

            modelIds = list.Select(m => m.Id).ToList();
            modelCombo.Items.Clear();
            foreach (var m in list)
            {
                var display = string.IsNullOrEmpty(m.Id)
                    ? m.Name
                    : (!string.IsNullOrEmpty(m.Name) && m.Name != m.Id ? $"{m.Name} — {m.Id}" : m.Id);
                modelCombo.Items.Add(display);
            }
            modelCombo.SelectedIndex = modelIds.Count > 0 ? Math.Max(0, modelIds.IndexOf(curModel)) : -1;
            updatingModels = false;

            if (provider == "chatgpt" && modelCombo.SelectedIndex >= 0 && modelCombo.SelectedIndex < modelIds.Count)
                settings.SetString("chatgpt-model", modelIds[modelCombo.SelectedIndex]);
        }

        Populate(GetCachedModels(provider));

        modelCombo.SelectionChanged += (_, _) =>
        {
            if (updatingModels || modelCombo.SelectedIndex < 0 || modelCombo.SelectedIndex >= modelIds.Count)
                return;
            ProvidersStore.SetProviderModel(settings, provider, modelIds[modelCombo.SelectedIndex]);
        };

        refreshBtn.Click += async (_, _) =>
        {
            refreshBtn.IsEnabled = false;
            statusBar.Severity = InfoBarSeverity.Informational;
            statusBar.Title = "Loading models…";
            statusBar.Message = "Fetching model list from provider.";
            statusBar.IsOpen = true;
            try
            {
                var models = await ModelsClient.FetchModelsAsync(settings, backend, provider, interactive: true);
                SetCachedModels(provider, models);
                Populate(models);
                statusBar.Severity = InfoBarSeverity.Success;
                statusBar.Title = "Models updated";
                statusBar.Message = $"{models.Count} model(s) loaded.";
            }
            catch (Exception ex)
            {
                statusBar.Severity = InfoBarSeverity.Error;
                statusBar.Title = "Could not fetch models";
                statusBar.Message = ex.Message;
            }
            finally
            {
                refreshBtn.IsEnabled = true;
            }
        };

        customBtn.Click += async (_, _) =>
        {
            var txt = new TextBox
            {
                Text = ProvidersStore.ProviderModel(settings, provider),
                PlaceholderText = "e.g. gpt-4o-mini",
                MinWidth = 320
            };
            var dlg = new ContentDialog
            {
                Title = "Custom Model ID",
                Content = txt,
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
                RequestedTheme = ThemeHelper.ResolveElementTheme(settings)
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                var id = txt.Text.Trim();
                if (!string.IsNullOrEmpty(id))
                {
                    ProvidersStore.SetProviderModel(settings, provider, id);
                    Populate(GetCachedModels(provider));
                }
            }
        };

        container.Children.Add(card);
        container.Children.Add(statusBar);
        return container;
    }

    private void BuildChatGptSection()
    {
        var settings = _context!.Settings;
        var backend = _context.SecretBackend;
        var client = new ChatGptClient(settings, backend);
        _chatGptClient = client;

        var accCombo = new ComboBox { MinWidth = 200, MaxWidth = 280 };
        DynamicProviderStack.Children.Add(new SettingsCard
        {
            Header = "ChatGPT account",
            Description = "Use your ChatGPT subscription without a separate API key.",
            HeaderIcon = MakeIcon("\uE77B"),
            Content = accCombo
        });

        var connectBtn = new Button
        {
            Content = "Continue with ChatGPT",
            Style = Application.Current.Resources["AccentButtonStyle"] as Style
        };
        var addAccBtn = new Button { Content = "Add account" };
        var signOutBtn = new Button { Content = "Sign out" };
        var usageBtn = new Button { Content = "Manage usage" };
        usageBtn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://chatgpt.com/settings/usage") { UseShellExecute = true }); }
            catch { }
        };

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        btnRow.Children.Add(connectBtn);
        btnRow.Children.Add(addAccBtn);
        btnRow.Children.Add(signOutBtn);
        btnRow.Children.Add(usageBtn);

        var statusBar = new InfoBar { IsOpen = false, IsClosable = true };
        var actionsCard = CreateFullWidthCard(
            "\uE71B",
            "Account connection",
            "Sign in via your browser or switch accounts.",
            btnRow);
        DynamicProviderStack.Children.Add(actionsCard);
        DynamicProviderStack.Children.Add(statusBar);

        var accounts = new List<ChatGptAccountSummary>();
        var updating = false;

        async Task ReloadAccountsAsync()
        {
            try
            {
                accounts = await client.AccountsAsync(_cts.Token);
                DispatcherQueue.TryEnqueue(() =>
                {
                    updating = true;
                    accCombo.Items.Clear();
                    if (accounts.Count == 0)
                        accCombo.Items.Add("No saved accounts");
                    else
                        foreach (var a in accounts) accCombo.Items.Add(a.Label);
                    var curId = settings.GetString("chatgpt-account-id");
                    var idx = Math.Max(0, accounts.FindIndex(a => a.Id == curId));
                    accCombo.SelectedIndex = idx;
                    updating = false;

                    var sel = accounts.ElementAtOrDefault(idx);
                    signOutBtn.IsEnabled = sel?.Connected == true;
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    statusBar.Severity = InfoBarSeverity.Error;
                    statusBar.Message = ex.Message;
                    statusBar.IsOpen = true;
                });
            }
        }

        accCombo.SelectionChanged += (_, _) =>
        {
            if (updating || accCombo.SelectedIndex < 0 || accCombo.SelectedIndex >= accounts.Count)
                return;
            settings.SetString("chatgpt-account-id", accounts[accCombo.SelectedIndex].Id);
        };

        connectBtn.Click += async (_, _) =>
        {
            connectBtn.IsEnabled = false;
            statusBar.Severity = InfoBarSeverity.Informational;
            statusBar.Message = "Complete sign-in in your browser…";
            statusBar.IsOpen = true;
            try
            {
                var targetId = accounts.ElementAtOrDefault(accCombo.SelectedIndex)?.Id;
                var res = await client.SignInAsync(targetId, _cts.Token);
                settings.SetString("chatgpt-account-id", res.Id);
                statusBar.Severity = res.Authorized ? InfoBarSeverity.Success : InfoBarSeverity.Error;
                statusBar.Message = res.Authorized
                    ? "Connected to your ChatGPT plan."
                    : "Plan access was not granted. Reconnect and allow plan usage.";
                await ReloadAccountsAsync();
            }
            catch (Exception ex)
            {
                statusBar.Severity = InfoBarSeverity.Error;
                statusBar.Message = ex.Message;
            }
            finally
            {
                connectBtn.IsEnabled = true;
            }
        };

        addAccBtn.Click += async (_, _) =>
        {
            addAccBtn.IsEnabled = false;
            statusBar.Severity = InfoBarSeverity.Informational;
            statusBar.Message = "Complete sign-in in your browser…";
            statusBar.IsOpen = true;
            try
            {
                var res = await client.SignInAsync(null, _cts.Token);
                settings.SetString("chatgpt-account-id", res.Id);
                statusBar.Severity = res.Authorized ? InfoBarSeverity.Success : InfoBarSeverity.Error;
                statusBar.Message = res.Authorized ? "Connected to your ChatGPT plan." : "Plan access was not granted.";
                await ReloadAccountsAsync();
            }
            catch (Exception ex)
            {
                statusBar.Severity = InfoBarSeverity.Error;
                statusBar.Message = ex.Message;
            }
            finally
            {
                addAccBtn.IsEnabled = true;
            }
        };

        signOutBtn.Click += async (_, _) =>
        {
            var sel = accounts.ElementAtOrDefault(accCombo.SelectedIndex);
            if (sel == null) return;
            signOutBtn.IsEnabled = false;
            try
            {
                var revoked = await client.SignOutAsync(sel.Id, _cts.Token);
                statusBar.Severity = InfoBarSeverity.Informational;
                statusBar.Message = revoked ? "Signed out." : "Signed out locally.";
                statusBar.IsOpen = true;
                await ReloadAccountsAsync();
            }
            catch (Exception ex)
            {
                statusBar.Severity = InfoBarSeverity.Error;
                statusBar.Message = ex.Message;
                statusBar.IsOpen = true;
            }
        };

        _ = ReloadAccountsAsync();
    }

    private void RenderSavedProviders()
    {
        if (_context == null) return;
        SavedProvidersExpander.Items.Clear();

        List<ProviderProfile> profiles;
        try { profiles = ProvidersStore.ReadProviders(_context.Settings); }
        catch { profiles = new List<ProviderProfile>(); }

        SavedProvidersExpander.Header = profiles.Count > 0
            ? $"Saved OpenAI-Compatible Providers ({profiles.Count} saved)"
            : "Saved OpenAI-Compatible Providers";

        var labels = ProvidersStore.ProviderLabels(profiles);
        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            var editBtn = new Button { Content = "Edit" };
            editBtn.Click += async (_, _) => await ShowEditProviderDialogAsync(ProvidersStore.ResolveProvider(_context.Settings, profile.Id));
            SavedProvidersExpander.Items.Add(new SettingsCard
            {
                Header = labels[i],
                Description = profile.Url ?? "",
                Content = editBtn
            });
        }

        var addBtn = new Button
        {
            Content = "+ Add provider",
            Style = Application.Current.Resources["AccentButtonStyle"] as Style
        };
        addBtn.Click += async (_, _) => await ShowEditProviderDialogAsync(null);
        SavedProvidersExpander.Items.Add(new SettingsCard
        {
            Header = "Add custom or OpenAI-compatible provider",
            Description = "Configure OpenAI, Groq, OpenRouter, Mistral, or any compatible endpoint.",
            Content = addBtn
        });
    }

    private async Task ShowEditProviderDialogAsync(ProviderProfile? existing)
    {
        if (_context == null) return;
        var settings = _context.Settings;
        var backend = _context.SecretBackend;

        var profile = existing != null
            ? existing.Clone()
            : new ProviderProfile { Id = $"custom:{Guid.NewGuid():D}", Name = "", Url = "", Model = "", Adapter = "" };

        var formStack = new StackPanel { Spacing = 10, MinWidth = 440 };

        var nameBox = new TextBox { Header = "Name", Text = profile.Name };
        var urlBox = new TextBox { Header = "Base URL", Text = profile.Url ?? "", PlaceholderText = "https://api.openai.com/v1" };
        var keyBox = new PasswordBox { Header = "API key (optional)", PasswordRevealMode = PasswordRevealMode.Peek };
        var modelBox = new TextBox { Header = "Default model (optional)", Text = profile.Model ?? "" };

        if (existing == null)
        {
            var presetCombo = new ComboBox { Header = "Start with preset", HorizontalAlignment = HorizontalAlignment.Stretch };
            presetCombo.Items.Add("Custom connection");
            foreach (var p in ProvidersStore.ProviderPresets)
                presetCombo.Items.Add(p.Name);
            presetCombo.SelectedIndex = 0;
            presetCombo.SelectionChanged += (_, _) =>
            {
                var sel = presetCombo.SelectedIndex > 0 ? ProvidersStore.ProviderPresets[presetCombo.SelectedIndex - 1] : null;
                profile.Adapter = sel?.Adapter ?? "";
                nameBox.Text = sel?.Name ?? "";
                urlBox.Text = sel?.Url ?? "";
                modelBox.Text = "";
            };
            formStack.Children.Add(presetCombo);
        }

        formStack.Children.Add(nameBox);
        formStack.Children.Add(urlBox);
        formStack.Children.Add(keyBox);
        formStack.Children.Add(modelBox);

        // Collapsible advanced parameters inside dialog so basic users only see Name/URL/Key/Model
        var tempBox = new TextBox
        {
            Header = "Temperature (0–2)",
            Text = profile.Advanced?.Temperature?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""
        };
        var reasonCombo = new ComboBox { Header = "Reasoning effort", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var opt in new[] { "Provider default", "None", "Minimal", "Low", "Medium", "High", "Extra high" })
            reasonCombo.Items.Add(opt);
        reasonCombo.SelectedIndex = Math.Max(0, ProvidersStore.ReasoningEfforts.ToList().IndexOf(profile.Advanced?.ReasoningEffort ?? ""));

        var headersBox = new TextBox
        {
            Header = "Custom headers (JSON)",
            AcceptsReturn = true,
            Text = "{}",
            Height = 76
        };
        ScrollViewer.SetVerticalScrollBarVisibility(headersBox, ScrollBarVisibility.Auto);

        var paramsBox = new TextBox
        {
            Header = "Extra request params (JSON)",
            AcceptsReturn = true,
            Text = profile.Advanced?.Parameters?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}",
            Height = 76
        };
        ScrollViewer.SetVerticalScrollBarVisibility(paramsBox, ScrollBarVisibility.Auto);

        var advStack = new StackPanel { Spacing = 8 };
        advStack.Children.Add(tempBox);
        advStack.Children.Add(reasonCombo);
        advStack.Children.Add(headersBox);
        advStack.Children.Add(paramsBox);

        formStack.Children.Add(new Expander
        {
            Header = "Advanced parameters",
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
            Title = existing != null ? "Edit Provider" : "Add Provider",
            Content = scroll,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            SecondaryButtonText = existing != null ? "Delete provider" : null,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ThemeHelper.ResolveElementTheme(settings)
        };

        if (existing != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var k = await SecretsStore.GetApiKeyAsync(settings, backend, profile.Id, _cts.Token, interactive: true);
                    var h = await SecretsStore.GetProviderHeadersAsync(backend, profile, _cts.Token, interactive: true);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        keyBox.Password = k;
                        headersBox.Text = JsonSerializer.Serialize(h, new JsonSerializerOptions { WriteIndented = true });
                    });
                }
                catch (Exception ex)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        errBar.Message = ex.Message;
                        errBar.IsOpen = true;
                    });
                }
            });
        }

        dlg.SecondaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var refs = ProvidersStore.ProviderReferences(settings, profile.Id);
                if (refs.Count > 0)
                    throw new InvalidOperationException($"Choose another provider first for: {string.Join(", ", refs)}.");

                await SecretsStore.DeleteProviderAsync(settings, backend, profile.Id, _cts.Token);
                SetCachedModels(profile.Id, new List<ModelItem>());
                RefreshProviderSelector();
                RenderSavedProviders();
                RenderDynamicProvider();
            }
            catch (Exception ex)
            {
                args.Cancel = true;
                errBar.Message = ex.Message;
                errBar.IsOpen = true;
            }
            finally
            {
                deferral.Complete();
            }
        };

        dlg.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var headers = ProvidersStore.ValidateHeadersJson(headersBox.Text);
                JsonObject? paramObj;
                var rawParams = string.IsNullOrWhiteSpace(paramsBox.Text) ? "{}" : paramsBox.Text.Trim();
                if (JsonNode.Parse(rawParams) is JsonObject pObj)
                    paramObj = pObj;
                else
                    throw new InvalidOperationException("Extra request parameters must be a JSON object.");

                double? tempVal = null;
                if (!string.IsNullOrWhiteSpace(tempBox.Text))
                {
                    tempVal = double.TryParse(tempBox.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedT)
                        ? parsedT
                        : double.NaN;
                }

                var candidate = new ProviderProfile
                {
                    Id = profile.Id,
                    Name = nameBox.Text,
                    Url = urlBox.Text,
                    Model = modelBox.Text,
                    Adapter = profile.Adapter,
                    HasCustomHeaders = headers.Count > 0 ? true : null,
                    Advanced = new ProviderAdvancedOptions
                    {
                        Temperature = tempVal,
                        ReasoningEffort = ProvidersStore.ReasoningEfforts[Math.Max(0, reasonCombo.SelectedIndex)],
                        Parameters = paramObj
                    }
                };

                var validated = ProvidersStore.ValidateProvider(candidate, requireModel: false);
                await SecretsStore.SaveProviderConnectionAsync(settings, backend, validated, keyBox.Password, headers, _cts.Token);
                RefreshProviderSelector();
                RenderSavedProviders();
                RenderDynamicProvider();
            }
            catch (Exception ex)
            {
                args.Cancel = true;
                errBar.Message = ex.Message;
                errBar.IsOpen = true;
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dlg.ShowAsync();
    }

    private List<ModelItem> GetCachedModels(string provider)
    {
        if (_context == null) return new List<ModelItem>();
        try
        {
            var cache = JsonSerializer.Deserialize<Dictionary<string, List<ModelItem>>>(_context.Settings.GetString("model-cache"));
            if (cache != null && cache.TryGetValue(provider, out var list) && list != null)
                return list;
        }
        catch { }
        return new List<ModelItem>();
    }

    private void SetCachedModels(string provider, List<ModelItem> models)
    {
        if (_context == null) return;
        Dictionary<string, List<ModelItem>> cache;
        try
        {
            cache = JsonSerializer.Deserialize<Dictionary<string, List<ModelItem>>>(_context.Settings.GetString("model-cache"))
                    ?? new Dictionary<string, List<ModelItem>>();
        }
        catch { cache = new Dictionary<string, List<ModelItem>>(); }
        cache[provider] = models;
        _context.Settings.SetString("model-cache", JsonSerializer.Serialize(cache));
    }
}
