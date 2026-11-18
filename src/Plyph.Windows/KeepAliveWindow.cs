using Microsoft.UI.Xaml;
using Plyph.Windows.Interop;
using WinRT.Interop;

namespace Plyph.Windows;

/// <summary>
/// Hidden keep-alive window so the WinUI 3 application remains alive in the System Tray
/// even after Settings or Result windows are closed.
/// </summary>
public sealed class KeepAliveWindow : Window
{
    public KeepAliveWindow()
    {
        Title = "Plyph Host";
        if (AppWindow != null)
        {
            try { AppWindow.IsShownInSwitchers = false; } catch { }
            AppWindow.Hide();
        }
        var hwnd = WindowNative.GetWindowHandle(this);
        NativeMethods.HideFromTaskbarAndAltTab(hwnd);
    }
}
