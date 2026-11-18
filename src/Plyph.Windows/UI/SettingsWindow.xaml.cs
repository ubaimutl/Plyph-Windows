using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Plyph.Core;
using Plyph.Windows.UI.Pages;

namespace Plyph.Windows.UI;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsHostContext _context;

    public SettingsWindow(
        IPlyphSettings settings,
        ISecretBackend secretBackend,
        Action onShortcutsChanged,
        Func<string, bool>? isShortcutRegistered = null,
        Action? onThemeChanged = null)
    {
        InitializeComponent();

        try
        {
            ProvidersStore.MigrateProviders(settings);
        }
        catch
        {
        }

        _context = new SettingsHostContext(
            settings,
            secretBackend,
            onShortcutsChanged,
            isShortcutRegistered,
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            () =>
            {
                ThemeHelper.ApplyTheme(this, settings);
                onThemeChanged?.Invoke();
            });

        ModeBadgeText.Text = settings.Mode == StorageMode.Portable ? "Portable Mode" : "Installed";
        BrandLogoHost.Content = FloatingSurfaceHelper.CreatePlyphMark(14);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Plyph.ico");
        ThemeHelper.ConfigureWindow(this, settings, 960, 680, iconPath);

        ContentFrame.Navigate(typeof(GeneralPage), _context);
        _ = SecretsStore.MigrateProviderSettingsAsync(settings, secretBackend, CancellationToken.None);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_context != null && args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            var pageType = tag switch
            {
                "general" => typeof(GeneralPage),
                "toolbar" => typeof(ToolbarPage),
                "personalization" => typeof(PersonalizationPage),
                "providers" => typeof(ProvidersPage),
                "actions" => typeof(ActionsPage),
                "shortcuts" => typeof(ShortcutsPage),
                _ => null
            };
            if (pageType != null && ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType, _context);
        }
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag as string == "open-folder")
        {
            try
            {
                Directory.CreateDirectory(_context.Settings.DataDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe", _context.Settings.DataDirectory) { UseShellExecute = true });
            }
            catch
            {
            }
        }
    }
}
