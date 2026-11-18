using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Plyph.Core;
using Plyph.Windows.Interop;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace Plyph.Windows.UI;

public static class ThemeHelper
{
    public static bool IsHighContrastEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            return new AccessibilitySettings().HighContrast;
        }
        catch
        {
            return false;
        }
    }

    public static bool ShouldUseDark(IPlyphSettings? settings)
    {
        var pref = settings?.GetString("app-theme") ?? "system";
        if (string.Equals(pref, "dark", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(pref, "light", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            var val = key?.GetValue("AppsUseLightTheme");
            if (val is int i)
                return i == 0;
            var sysVal = key?.GetValue("SystemUsesLightTheme");
            if (sysVal is int si)
                return si == 0;
        }
        catch
        {
        }
        return false;
    }

    public static ElementTheme ResolveElementTheme(IPlyphSettings? settings)
    {
        var pref = settings?.GetString("app-theme") ?? "system";
        return pref.ToLowerInvariant() switch
        {
            "dark" => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            _ => IsHighContrastEnabled()
                ? ElementTheme.Default
                : (ShouldUseDark(settings) ? ElementTheme.Dark : ElementTheme.Light)
        };
    }

    public static void ConfigureWindow(
        Window window,
        IPlyphSettings settings,
        int widthDip,
        int heightDip,
        string? iconPath = null,
        bool preferAcrylic = false)
    {
        ApplyBackdrop(window, preferAcrylic);

        var hwnd = WindowNative.GetWindowHandle(window);
        var scale = hwnd != IntPtr.Zero ? Math.Max(1.0, NativeMethods.GetDpiForWindow(hwnd) / 96.0) : 1.0;

        if (window.AppWindow is { } appWindow)
        {
            var pt = NativeMethods.GetCursorPos(out var cur)
                ? new System.Drawing.Point(cur.X, cur.Y)
                : new System.Drawing.Point(400, 300);
            NativeMethods.GetWorkAreaMonitorIndex(pt, out var work);

            var maxW = Math.Max(320, work.Right - work.Left - 24);
            var maxH = Math.Max(180, work.Bottom - work.Top - 24);
            var physW = Math.Min(maxW, (int)Math.Round(widthDip * scale));
            var physH = Math.Min(maxH, (int)Math.Round(heightDip * scale));
            appWindow.Resize(new SizeInt32(physW, physH));

            var cx = work.Left + Math.Max(0, (work.Right - work.Left - physW) / 2);
            var cy = work.Top + Math.Max(0, (work.Bottom - work.Top - physH) / 2);
            appWindow.Move(new PointInt32(cx, cy));

            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
                appWindow.SetIcon(iconPath);
        }

        ApplyTheme(window, settings, preferAcrylic);
    }

    public static bool ApplyBackdrop(Window window, bool preferAcrylic = false)
    {
        if (IsHighContrastEnabled() || !NativeMethods.IsWindows11OrGreater())
        {
            window.SystemBackdrop = null;
            return false;
        }

        if (preferAcrylic && DesktopAcrylicController.IsSupported())
        {
            if (window.SystemBackdrop is not DesktopAcrylicBackdrop)
                window.SystemBackdrop = new DesktopAcrylicBackdrop();
            return true;
        }

        if (!preferAcrylic && MicaController.IsSupported())
        {
            if (window.SystemBackdrop is not MicaBackdrop)
                window.SystemBackdrop = new MicaBackdrop();
            return true;
        }

        window.SystemBackdrop = null;
        return false;
    }

    public static void ApplyTheme(Window window, IPlyphSettings settings, bool preferAcrylic = false, bool useBackdrop = true)
    {
        var elementTheme = ResolveElementTheme(settings);
        var isDark = ShouldUseDark(settings);
        var highContrast = IsHighContrastEnabled();
        var hasBackdrop = useBackdrop && ApplyBackdrop(window, preferAcrylic);
        if (!useBackdrop)
            window.SystemBackdrop = null;

        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = elementTheme;

            if (root is Panel rootPanel)
            {
                if (hasBackdrop)
                {
                    rootPanel.Background = new SolidColorBrush(Colors.Transparent);
                }
                else
                {
                    var key = preferAcrylic ? "PopupSurfaceBackgroundBrush" : "WindowFallbackBackgroundBrush";
                    var fallbackColor = isDark
                        ? Color.FromArgb(255, 32, 32, 32)
                        : Color.FromArgb(255, 243, 243, 243);
                    rootPanel.Background = ResolveBrushForTheme(elementTheme, isDark, highContrast, key)
                        ?? new SolidColorBrush(fallbackColor);
                }
            }
        }

        var hwnd = WindowNative.GetWindowHandle(window);
        NativeMethods.ApplyDarkTitleBar(hwnd, isDark);

        if (NativeMethods.IsWindows11OrGreater() &&
            AppWindowTitleBar.IsCustomizationSupported() &&
            window.AppWindow?.TitleBar is { } titleBar)
        {
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = isDark ? Colors.White : Color.FromArgb(255, 27, 29, 34);
            titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
            titleBar.ButtonHoverBackgroundColor = isDark
                ? Color.FromArgb(24, 255, 255, 255)
                : Color.FromArgb(16, 0, 0, 0);
        }
    }

    public static Brush? GetThemeBrush(FrameworkElement scope, string key)
    {
        var highContrast = IsHighContrastEnabled();
        var isDark = scope.ActualTheme == ElementTheme.Dark;
        return ResolveBrushForTheme(scope.ActualTheme, isDark, highContrast, key);
    }

    public static Brush? ResolveBrushForTheme(ElementTheme elementTheme, bool isDark, bool highContrast, string key)
    {
        var themeKey = highContrast
            ? "HighContrast"
            : (elementTheme == ElementTheme.Dark || (elementTheme == ElementTheme.Default && isDark) ? "Dark" : "Light");

        var root = Application.Current.Resources;
        if (TryFindBrushInDictionary(root, themeKey, key) is { } themed)
            return themed;

        return root.TryGetValue(key, out var fallback) ? fallback as Brush : null;
    }

    private static Brush? TryFindBrushInDictionary(ResourceDictionary dict, string themeKey, string key)
    {
        if (dict.ThemeDictionaries.TryGetValue(themeKey, out var themeObj) &&
            themeObj is ResourceDictionary themeDict &&
            themeDict.TryGetValue(key, out var val) &&
            val is Brush b)
        {
            return b;
        }

        foreach (var merged in dict.MergedDictionaries)
        {
            if (TryFindBrushInDictionary(merged, themeKey, key) is { } found)
                return found;
        }

        return null;
    }

    public static void BringWindowToFront(Window window, bool keepOnTop = false)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero)
            return;

        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        var curFg = NativeMethods.GetForegroundWindow();
        var curTid = curFg != IntPtr.Zero ? NativeMethods.GetWindowThreadProcessId(curFg, out _) : 0;
        var myTid = NativeMethods.GetCurrentThreadId();
        var attached = false;

        try
        {
            if (curTid != 0 && curTid != myTid)
                attached = NativeMethods.AttachThreadInput(myTid, curTid, true);

            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);

            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);

            if (!keepOnTop)
            {
                NativeMethods.SetWindowPos(
                    hwnd,
                    NativeMethods.HWND_NOTOPMOST,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
            }
        }
        finally
        {
            if (attached)
                NativeMethods.AttachThreadInput(myTid, curTid, false);
        }

        if (window.AppWindow?.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = keepOnTop;
    }
}
