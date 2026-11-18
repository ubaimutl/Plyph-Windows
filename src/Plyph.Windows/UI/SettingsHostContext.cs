using Plyph.Core;

namespace Plyph.Windows.UI;

public sealed record SettingsHostContext(
    IPlyphSettings Settings,
    ISecretBackend SecretBackend,
    Action OnShortcutsChanged,
    Func<string, bool>? IsShortcutRegistered,
    IntPtr WindowHandle,
    Action OnThemeChanged);
