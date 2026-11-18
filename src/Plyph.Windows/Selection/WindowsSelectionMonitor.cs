using System.Diagnostics;
using System.Drawing;
using Microsoft.UI.Dispatching;
using Plyph.Core;
using Plyph.Windows.Interop;

namespace Plyph.Windows.Selection;

public sealed record SelectionDetectedEventArgs(
    string Text,
    IntPtr SourceWindow,
    Point AnchorPoint,
    Rectangle? SelectionBounds = null);

public sealed class WindowsSelectionMonitor : IDisposable
{
    private static readonly TimeSpan DismissedSelectionCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TrayFallbackCacheLifetime = TimeSpan.FromSeconds(45);
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow"
    };

    private readonly IPlyphSettings _settings;
    private readonly Func<bool> _isRequestRunning;
    private readonly Func<Point, bool> _isPointInsideFloatingToolbar;

    private IntPtr _selectionHook;
    private IntPtr _foregroundHook;
    private IntPtr _locationHook;
    private readonly NativeMethods.WinEventDelegate _winEventProc;

    private readonly DispatcherQueueTimer _gestureTimer;
    private bool _wasLeftButtonDown;
    private int _mouseButtonsDown;
    private Point _mouseDownPoint;
    private IntPtr _mouseDownWindow;
    private NativeMethods.RECT _mouseDownWindowRect;
    private bool _mouseMovedWhileDown;
    private bool _mouseDownWasArrowOrHand;
    private bool _sawIBeamDuringGesture;
    private DateTime _lastMouseUpTime = DateTime.MinValue;
    private Point _lastMouseUpPoint;
    private bool _lastUpWasArrowOrHand;
    private bool _wasShiftSelecting;
    private bool _wasEscapeDown;
    private DateTime _suppressWinEventUntil = DateTime.MinValue;

    private readonly SemaphoreSlim _uiaQueries = new(1);
    private int _selectionReadSerial;
    private int? _gestureReadSerial;
    private (IntPtr Window, bool AllowFallback, bool BrowserTextGesture, Point? EndPoint, DateTime Expires)? _pendingGesture;
    private bool _disposed;
    private string? _lastSelectionText;
    private IntPtr _floatingSourceWindow;
    private NativeMethods.RECT _floatingSourceRect;
    private Rectangle? _floatingSelectionBounds;

    private IntPtr _lastRealAppWindow;
    private string _recentValidText = "";
    private IntPtr _recentValidWindow;
    private DateTime _recentValidUntil = DateTime.MinValue;

    private string? _dismissedSelectionText;
    private IntPtr _dismissedSelectionWindow;
    private DateTime _dismissedSelectionUntil = DateTime.MinValue;

    public event EventHandler<SelectionDetectedEventArgs>? SelectionDetected;
    public event EventHandler<bool>? DismissRequested;
    public event EventHandler<Point>? MousePressed;

    public string? LastSelectionText => _lastSelectionText;
    public IntPtr FloatingSourceWindow => _floatingSourceWindow;
    public IntPtr LastRealAppWindow => _lastRealAppWindow;

    public WindowsSelectionMonitor(
        IPlyphSettings settings,
        Func<bool> isRequestRunning,
        Func<Point, bool> isPointInsideFloatingToolbar)
    {
        _settings = settings;
        _isRequestRunning = isRequestRunning;
        _isPointInsideFloatingToolbar = isPointInsideFloatingToolbar;

        _winEventProc = OnWinEvent;

        if (OperatingSystem.IsWindows())
        {
            var flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;
            _selectionHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_TEXTSELECTIONCHANGED,
                NativeMethods.EVENT_OBJECT_TEXTSELECTIONCHANGED,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                flags);

            _foregroundHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                flags);

            _locationHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
                NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                flags);

            RememberForegroundIfRealApp(NativeMethods.GetForegroundWindow());
        }

        _gestureTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _gestureTimer.Interval = TimeSpan.FromMilliseconds(30);
        _gestureTimer.IsRepeating = true;
        _gestureTimer.Tick += OnGestureTimerTick;
        _gestureTimer.Start();
    }

    public static bool IsShellOrOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            return true;

        var cls = NativeMethods.GetWindowClassName(hwnd);
        if (!string.IsNullOrEmpty(cls) && ShellWindowClasses.Contains(cls))
            return true;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private void RememberForegroundIfRealApp(IntPtr fg)
    {
        if (!IsShellOrOwnWindow(fg))
            _lastRealAppWindow = fg;
    }

    public string? GetRecentCachedTextFor(IntPtr window)
    {
        if (window != IntPtr.Zero &&
            window == _recentValidWindow &&
            DateTime.UtcNow <= _recentValidUntil &&
            !string.IsNullOrWhiteSpace(_recentValidText))
        {
            return _recentValidText;
        }
        return null;
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND)
        {
            var fg = NativeMethods.GetForegroundWindow();
            RememberForegroundIfRealApp(fg);
            if (_floatingSourceWindow != IntPtr.Zero && fg != _floatingSourceWindow)
            {
                if (!IsPointInToolbar())
                    DismissRequested?.Invoke(this, true);
            }
            return;
        }

        if (eventType == NativeMethods.EVENT_OBJECT_LOCATIONCHANGE)
        {
            if (_floatingSourceWindow != IntPtr.Zero && hwnd == _floatingSourceWindow)
            {
                if (NativeMethods.GetWindowRect(_floatingSourceWindow, out var rect))
                {
                    if (rect.Left != _floatingSourceRect.Left ||
                        rect.Top != _floatingSourceRect.Top ||
                        rect.Right != _floatingSourceRect.Right ||
                        rect.Bottom != _floatingSourceRect.Bottom)
                    {
                        DismissRequested?.Invoke(this, true);
                    }
                }
            }
            return;
        }

        if (eventType == NativeMethods.EVENT_OBJECT_TEXTSELECTIONCHANGED)
        {
            if (_gestureReadSerial.HasValue || DateTime.UtcNow < _suppressWinEventUntil)
                return;
            if (_settings.GetBoolean("selection-uia-enabled"))
                _ = HandleSelectionChangeAsync(isGesture: false, allowNonUiaMouseFallback: false);
        }
    }

    private void OnGestureTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!OperatingSystem.IsWindows())
            return;

        RememberForegroundIfRealApp(NativeMethods.GetForegroundWindow());

        var escDown = NativeMethods.IsKeyDown(NativeMethods.VK_ESCAPE);
        if (escDown && !_wasEscapeDown)
        {
            _pendingGesture = null;
            _selectionReadSerial++;
            DismissRequested?.Invoke(this, true);
        }
        _wasEscapeDown = escDown;

        if (_floatingSourceWindow != IntPtr.Zero)
        {
            if (!NativeMethods.IsWindow(_floatingSourceWindow))
            {
                DismissRequested?.Invoke(this, true);
            }
            else
            {
                var fg = NativeMethods.GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != _floatingSourceWindow && !IsPointInToolbar())
                    DismissRequested?.Invoke(this, true);
            }
        }

        var leftDown = NativeMethods.IsKeyDown(NativeMethods.VK_LBUTTON);
        if (!NativeMethods.GetCursorPos(out var ptRaw))
            return;
        var cursor = new Point(ptRaw.X, ptRaw.Y);
        var buttonsDown = (leftDown ? 1 : 0) |
            (NativeMethods.IsKeyDown(NativeMethods.VK_RBUTTON) ? 2 : 0) |
            (NativeMethods.IsKeyDown(NativeMethods.VK_MBUTTON) ? 4 : 0);
        var newlyPressed = (buttonsDown & ~_mouseButtonsDown) != 0;
        _mouseButtonsDown = buttonsDown;
        if (newlyPressed)
        {
            _pendingGesture = null;
            _selectionReadSerial++;
            MousePressed?.Invoke(this, cursor);
        }

        bool MovedFromMouseDown() =>
            Math.Abs(cursor.X - _mouseDownPoint.X) >= Math.Max(4, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDRAG)) ||
            Math.Abs(cursor.Y - _mouseDownPoint.Y) >= Math.Max(4, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDRAG));

        if (leftDown && !_wasLeftButtonDown)
        {
            _mouseDownPoint = cursor;
            _mouseDownWindow = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowRect(_mouseDownWindow, out _mouseDownWindowRect);
            _mouseMovedWhileDown = false;
            _mouseDownWasArrowOrHand = NativeMethods.IsCursorStandardArrowOrHand();
            _sawIBeamDuringGesture = NativeMethods.IsCurrentCursorIBeam();

            if (!_isPointInsideFloatingToolbar(cursor))
                DismissRequested?.Invoke(this, true);
        }
        else if (leftDown && _wasLeftButtonDown)
        {
            if (!_mouseMovedWhileDown && MovedFromMouseDown())
            {
                _mouseMovedWhileDown = true;
            }
            if (!_sawIBeamDuringGesture && NativeMethods.IsCurrentCursorIBeam())
                _sawIBeamDuringGesture = true;
        }
        else if (!leftDown && _wasLeftButtonDown)
        {
            _mouseMovedWhileDown |= MovedFromMouseDown();
            var now = DateTime.UtcNow;
            var curIsArrowOrHand = NativeMethods.IsCursorStandardArrowOrHand();
            if (!_sawIBeamDuringGesture && NativeMethods.IsCurrentCursorIBeam())
                _sawIBeamDuringGesture = true;

            var isDoubleClick =
                (now - _lastMouseUpTime).TotalMilliseconds <= 450 &&
                Math.Abs(cursor.X - _lastMouseUpPoint.X) <= 6 &&
                Math.Abs(cursor.Y - _lastMouseUpPoint.Y) <= 6;

            var gestureWasNonArrowOrIBeam =
                _sawIBeamDuringGesture ||
                (!_mouseDownWasArrowOrHand && !curIsArrowOrHand && (!_lastUpWasArrowOrHand || _mouseMovedWhileDown));

            _lastMouseUpTime = now;
            _lastMouseUpPoint = cursor;
            _lastUpWasArrowOrHand = curIsArrowOrHand;

            if ((_mouseMovedWhileDown || isDoubleClick) && !_isPointInsideFloatingToolbar(cursor))
            {
                var fg = NativeMethods.GetForegroundWindow();
                NativeMethods.GetWindowRect(fg, out var currentRect);

                var windowMovedOrResized =
                    fg == _mouseDownWindow &&
                    (currentRect.Left != _mouseDownWindowRect.Left ||
                     currentRect.Top != _mouseDownWindowRect.Top ||
                     currentRect.Right != _mouseDownWindowRect.Right ||
                     currentRect.Bottom != _mouseDownWindowRect.Bottom);

                var insideClientArea = NativeMethods.IsPointInClientArea(fg, _mouseDownPoint);

                if (!windowMovedOrResized && insideClientArea)
                {
                    // Always allow UIA to inspect real selections (even in browsers with custom I-beams),
                    // while restricting non-UIA fallback strictly to non-Arrow/Hand cursors.
                    if (_settings.GetBoolean("selection-uia-enabled") || gestureWasNonArrowOrIBeam)
                    {
                        _suppressWinEventUntil = DateTime.UtcNow.AddMilliseconds(350);
                        _ = HandleSelectionChangeAsync(
                            isGesture: true,
                            allowNonUiaMouseFallback: gestureWasNonArrowOrIBeam,
                            browserTextGesture: _sawIBeamDuringGesture &&
                                NativeMethods.GetWindowClassName(fg) is "Chrome_WidgetWin_1" or "MozillaWindowClass",
                            gestureEndPoint: cursor);
                    }
                }
            }
        }
        _wasLeftButtonDown = leftDown;

        var shiftDown = NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT);
        var otherModifierDown = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL) ||
                                NativeMethods.IsKeyDown(NativeMethods.VK_MENU) ||
                                NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) ||
                                NativeMethods.IsKeyDown(NativeMethods.VK_RWIN);
        var navKeyDown = NativeMethods.IsKeyDown(NativeMethods.VK_LEFT) ||
                         NativeMethods.IsKeyDown(NativeMethods.VK_RIGHT) ||
                         NativeMethods.IsKeyDown(NativeMethods.VK_UP) ||
                         NativeMethods.IsKeyDown(NativeMethods.VK_DOWN) ||
                         NativeMethods.IsKeyDown(NativeMethods.VK_HOME) ||
                         NativeMethods.IsKeyDown(NativeMethods.VK_END) ||
                         NativeMethods.IsKeyDown(NativeMethods.VK_PRIOR) ||
                         NativeMethods.IsKeyDown(NativeMethods.VK_NEXT);

        if (otherModifierDown)
        {
            _wasShiftSelecting = false;
        }
        else if (shiftDown && navKeyDown)
        {
            _wasShiftSelecting = true;
        }
        else if (_wasShiftSelecting && !shiftDown && !navKeyDown)
        {
            _wasShiftSelecting = false;
            _ = HandleSelectionChangeAsync(isGesture: true, allowNonUiaMouseFallback: true);
        }

        if (_pendingGesture is { } pending)
        {
            if (DateTime.UtcNow > pending.Expires || NativeMethods.GetForegroundWindow() != pending.Window)
                _pendingGesture = null;
            else if (!_isRequestRunning())
                _ = HandleSelectionChangeAsync(
                    isGesture: true, allowNonUiaMouseFallback: pending.AllowFallback,
                    browserTextGesture: pending.BrowserTextGesture, gestureEndPoint: pending.EndPoint);
        }
    }

    private bool IsPointInToolbar()
    {
        if (!NativeMethods.GetCursorPos(out var pt))
            return false;
        return _isPointInsideFloatingToolbar(new Point(pt.X, pt.Y));
    }

    public bool IsAppExcluded(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;
        if (IsShellOrOwnWindow(hwnd))
            return true;

        var exclusions = AppExclusions.Read(_settings);
        if (exclusions.Count == 0)
            return false;

        var className = NativeMethods.GetWindowClassName(hwnd);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        string? processName = null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            processName = process.ProcessName;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }

        return exclusions.Any(rule => AppExclusions.Matches(rule, processName, className));
    }

    private async Task HandleSelectionChangeAsync(
        bool isGesture, bool allowNonUiaMouseFallback, bool browserTextGesture = false, Point? gestureEndPoint = null)
    {
        if (_disposed) return;
        if (isGesture)
        {
            _pendingGesture = null;
            var source = NativeMethods.GetForegroundWindow();
            if (_isRequestRunning() && !IsShellOrOwnWindow(source))
            {
                _pendingGesture = (source, allowNonUiaMouseFallback, browserTextGesture, gestureEndPoint, DateTime.UtcNow.AddSeconds(3));
                return;
            }
        }

        var serial = ++_selectionReadSerial;
        if (isGesture)
        {
            // A deliberate selection may include the same text the user previously dismissed.
            ClearDismissedSelection();
            _gestureReadSerial = serial;
        }

        try
        {
            await ReadSelectionAsync(serial, allowNonUiaMouseFallback, browserTextGesture, gestureEndPoint);
        }
        finally
        {
            if (_gestureReadSerial == serial)
                _gestureReadSerial = null;
        }
    }

    private async Task ReadSelectionAsync(int serial, bool allowNonUiaMouseFallback, bool browserTextGesture, Point? gestureEndPoint)
    {
        if (!_settings.GetBoolean("show-floating-button"))
        {
            DismissRequested?.Invoke(this, false);
            return;
        }

        if (_isRequestRunning())
            return;

        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero || IsAppExcluded(window))
        {
            DismissRequested?.Invoke(this, false);
            return;
        }

        RememberForegroundIfRealApp(window);

        var fallbackPoint = gestureEndPoint ??
            (NativeMethods.GetCursorPos(out var cursor) ? new Point(cursor.X, cursor.Y) : new Point(200, 200));

        bool IsCurrent() =>
            !_disposed && serial == _selectionReadSerial &&
            _settings.GetBoolean("show-floating-button") &&
            NativeMethods.GetForegroundWindow() == window &&
            !_isRequestRunning();

        await Task.Delay(90).ConfigureAwait(true);
        if (!IsCurrent())
            return;

        while (NativeMethods.IsKeyDown(NativeMethods.VK_LBUTTON) ||
               NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT))
        {
            await Task.Delay(40).ConfigureAwait(true);
            if (!IsCurrent())
                return;
        }

        string text = "";
        Rectangle? bounds = null;
        var uiaStatus = UiaSelectionStatus.Unsupported;

        if (_settings.GetBoolean("selection-uia-enabled"))
        {
            await _uiaQueries.WaitAsync();
            try
            {
                if (!IsCurrent())
                    return;

                static (UiaSelectionStatus Status, string Text, Rectangle? Bounds) QuerySelection()
                {
                    var status = UiAutomationInterop.TryQuerySelectedText(out var selectedText, out var selectionBounds);
                    return (status, selectedText, selectionBounds);
                }

                (uiaStatus, text, bounds) = await Task.Run(QuerySelection);
                if (browserTextGesture && uiaStatus != UiaSelectionStatus.Selected)
                {
                    await Task.Delay(120);
                    if (IsCurrent())
                        (uiaStatus, text, bounds) = await Task.Run(QuerySelection);
                }
            }
            finally
            {
                _uiaQueries.Release();
            }

            if (!IsCurrent())
                return;
        }

        var besideSelection = _settings.GetString("floating-toolbar-position") != "cursor";
        if (besideSelection)
        {
            if (bounds is { } rect && (rect.Width <= 0 || rect.Height <= 0 ||
                !NativeMethods.IsPointInClientArea(window, new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2))))
            {
                bounds = null;
            }

            bounds ??= NativeMethods.GetWin32EditSelectionBounds(window);
        }

        var hasSelection = uiaStatus == UiaSelectionStatus.Selected || (besideSelection && bounds.HasValue);

        // An I-beam drag or double-click in a browser can precede its UIA selection update.
        if (!hasSelection && browserTextGesture && _settings.GetBoolean("selection-mouse-fallback"))
            hasSelection = true;

        if (!hasSelection && uiaStatus != UiaSelectionStatus.EmptySupported)
        {
            if (NativeMethods.HasWin32EditSelection(window))
            {
                hasSelection = true;
            }
            else if (allowNonUiaMouseFallback && _settings.GetBoolean("selection-mouse-fallback"))
            {
                var cls = NativeMethods.GetWindowClassName(window);
                if (!string.Equals(cls, "CabinetWClass", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(cls, "ExploreWClass", StringComparison.OrdinalIgnoreCase))
                {
                    hasSelection = true;
                }
            }
        }

        if (!hasSelection)
        {
            ClearDismissedSelection();
            DismissRequested?.Invoke(this, false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            _recentValidText = text;
            _recentValidWindow = window;
            _recentValidUntil = DateTime.UtcNow + TrayFallbackCacheLifetime;
        }

        if (!string.IsNullOrEmpty(text) && _dismissedSelectionText != null)
        {
            if (text == _dismissedSelectionText && window == _dismissedSelectionWindow)
            {
                if (DateTime.UtcNow < _dismissedSelectionUntil)
                    return;
            }
            _dismissedSelectionText = null;
            _dismissedSelectionWindow = IntPtr.Zero;
            _dismissedSelectionUntil = DateTime.MinValue;
        }

        if (!IsCurrent())
            return;

        var anchorBounds = besideSelection ? bounds : null;
        if (gestureEndPoint == null && _floatingSourceWindow == window && _lastSelectionText == text &&
            (anchorBounds == null || anchorBounds == _floatingSelectionBounds))
            return;

        _lastSelectionText = text;
        _floatingSelectionBounds = anchorBounds;
        var anchor = anchorBounds is { } selection
            ? new Point(selection.Right + 6, selection.Bottom + 6)
            : fallbackPoint;
        WatchSourceWindow(window);
        SelectionDetected?.Invoke(this, new SelectionDetectedEventArgs(text, window, anchor, anchorBounds));
    }

    public void ClearDismissedSelection()
    {
        _dismissedSelectionText = null;
        _dismissedSelectionWindow = IntPtr.Zero;
        _dismissedSelectionUntil = DateTime.MinValue;
    }

    private void WatchSourceWindow(IntPtr window)
    {
        _floatingSourceWindow = window;
        NativeMethods.GetWindowRect(window, out _floatingSourceRect);
    }

    public void NotifyHidden(bool suppressSelection)
    {
        if (suppressSelection && !string.IsNullOrEmpty(_lastSelectionText) && _floatingSourceWindow != IntPtr.Zero)
        {
            _dismissedSelectionText = _lastSelectionText;
            _dismissedSelectionWindow = _floatingSourceWindow;
            _dismissedSelectionUntil = DateTime.UtcNow + DismissedSelectionCooldown;
        }
        _selectionReadSerial++;
        _lastSelectionText = null;
        _floatingSourceWindow = IntPtr.Zero;
        _floatingSelectionBounds = null;
    }

    public void Dispose()
    {
        _disposed = true;
        _gestureTimer.Stop();

        if (OperatingSystem.IsWindows())
        {
            if (_selectionHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_selectionHook);
                _selectionHook = IntPtr.Zero;
            }
            if (_foregroundHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }
            if (_locationHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_locationHook);
                _locationHook = IntPtr.Zero;
            }
        }
    }
}
