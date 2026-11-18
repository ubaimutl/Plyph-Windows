using System.Runtime.InteropServices;
using Plyph.Windows.Interop;
using Plyph.Windows.UI;

namespace Plyph.Windows.Services;

public sealed class TrayIconManager : IDisposable
{
    private const uint TrayCallbackMessage = NativeMethods.WM_APP + 101;
    private const uint TrayId = 1;

    private const uint CmdOpenActions = 1001;
    private const uint CmdAsk = 1002;
    private const uint CmdUndo = 1003;
    private const uint CmdSettings = 1004;
    private const uint CmdExit = 1005;

    private readonly Action _onOpenActions;
    private readonly Action _onAsk;
    private readonly Action _onUndo;
    private readonly Action _onSettings;
    private readonly Action _onExit;
    private readonly Func<bool> _canUndo;

    private readonly NativeMethods.WndProcDelegate _wndProc;
    private IntPtr _hwnd;
    private IntPtr _currentIcon;
    private bool _added;
    private string _tooltip = "Plyph — Use AI on selected text";

    public TrayIconManager(
        Action onOpenActions,
        Action onAsk,
        Action onUndo,
        Action onSettings,
        Action onExit,
        Func<bool> canUndo)
    {
        _onOpenActions = onOpenActions;
        _onAsk = onAsk;
        _onUndo = onUndo;
        _onSettings = onSettings;
        _onExit = onExit;
        _canUndo = canUndo;

        _wndProc = WindowProc;

        if (OperatingSystem.IsWindows())
        {
            var className = $"PlyphTrayHost_{Environment.ProcessId}_{Guid.NewGuid():N}";
            var wc = new NativeMethods.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = NativeMethods.GetModuleHandleW(null),
                lpszClassName = className
            };
            NativeMethods.RegisterClassExW(ref wc);
            _hwnd = NativeMethods.CreateWindowExW(
                0, className, "PlyphTray", 0, 0, 0, 0, 0,
                NativeMethods.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            _currentIcon = IconRenderer.CreateStatusIconHandle("default", 32);
            AddIcon();
        }
    }

    private void AddIcon()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        var nid = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _currentIcon,
            szTip = _tooltip,
            szInfo = "",
            szInfoTitle = ""
        };
        _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref nid);
    }

    public void SetStatus(string state, string tooltip)
    {
        _tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (!OperatingSystem.IsWindows() || _hwnd == IntPtr.Zero || !_added)
            return;

        var nextIcon = IconRenderer.CreateStatusIconHandle(state, 32);
        var prevIcon = _currentIcon;
        _currentIcon = nextIcon;

        var nid = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayId,
            uFlags = NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            hIcon = _currentIcon,
            szTip = _tooltip,
            szInfo = "",
            szInfoTitle = ""
        };
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref nid);

        if (prevIcon != IntPtr.Zero)
            NativeMethods.DestroyIcon(prevIcon);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TrayCallbackMessage)
        {
            var evt = unchecked((int)((long)lParam & 0xFFFF));
            if (evt == NativeMethods.WM_LBUTTONDBLCLK || evt == NativeMethods.NIN_SELECT || evt == NativeMethods.NIN_KEYSELECT)
            {
                _onSettings();
                return IntPtr.Zero;
            }
            if (evt == NativeMethods.WM_RBUTTONUP || evt == NativeMethods.WM_LBUTTONUP)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }
        }
        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)CmdOpenActions, "Open Plyph Actions");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)CmdAsk, "Ask…");
            var undoFlags = NativeMethods.MF_STRING | (_canUndo() ? 0u : NativeMethods.MF_GRAYED);
            NativeMethods.AppendMenuW(menu, undoFlags, (UIntPtr)CmdUndo, "Undo Last Replace");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)CmdSettings, "Settings…");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)CmdExit, "Exit Plyph");
            NativeMethods.SetMenuDefaultItem(menu, CmdSettings, 0);

            NativeMethods.GetCursorPos(out var pt);
            NativeMethods.SetForegroundWindow(_hwnd);

            var cmd = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TPM_RIGHTALIGN | NativeMethods.TPM_BOTTOMALIGN | NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY,
                pt.X,
                pt.Y,
                _hwnd,
                IntPtr.Zero);

            switch (cmd)
            {
                case CmdOpenActions: _onOpenActions(); break;
                case CmdAsk: _onAsk(); break;
                case CmdUndo: _onUndo(); break;
                case CmdSettings: _onSettings(); break;
                case CmdExit: _onExit(); break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            if (_added && _hwnd != IntPtr.Zero)
            {
                var nid = new NativeMethods.NOTIFYICONDATAW
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
                    hWnd = _hwnd,
                    uID = TrayId,
                    szTip = "",
                    szInfo = "",
                    szInfoTitle = ""
                };
                NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref nid);
                _added = false;
            }
            if (_currentIcon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_currentIcon);
                _currentIcon = IntPtr.Zero;
            }
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
    }
}
