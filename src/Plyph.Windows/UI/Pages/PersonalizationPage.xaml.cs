using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Plyph.Core;

namespace Plyph.Windows.UI.Pages;

public sealed partial class PersonalizationPage : Page
{
    private IPlyphSettings? _settings;

    public PersonalizationPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not SettingsHostContext context) return;

        _settings = null;
        foreach (var box in new[] { PersonalContextBox, LanguageBox, ToneBox, StyleBox })
            box.Text = context.Settings.GetString((string)box.Tag);
        _settings = context.Settings;
    }

    private void Preference_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { Tag: string key } box)
            _settings?.SetString(key, box.Text);
    }
}
