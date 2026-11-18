using Microsoft.UI.Dispatching;
using Plyph.Windows.Interop;

namespace Plyph.Windows.Selection;

public sealed class TextReplacer : IDisposable
{
    private IntPtr _undoTargetWindow;
    private readonly DispatcherQueueTimer _undoTimer;

    public bool CanUndo => _undoTargetWindow != IntPtr.Zero;
    public event EventHandler? UndoStateChanged;

    public TextReplacer()
    {
        _undoTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _undoTimer.Interval = TimeSpan.FromSeconds(60);
        _undoTimer.IsRepeating = false;
        _undoTimer.Tick += (_, _) => ClearUndo();
    }

    public static async Task<bool> WaitForModifiersReleasedAsync(int timeoutMs = 1500)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        var started = Environment.TickCount64;
        while (true)
        {
            if (!NativeMethods.AreAnyModifiersDown())
                return true;
            if (Environment.TickCount64 - started >= timeoutMs)
                return false;
            await Task.Delay(20).ConfigureAwait(true);
        }
    }

    public async Task<(bool Success, string? ErrorMessage)> ReplaceAsync(
        string output,
        bool delayed,
        IntPtr focusedWindow)
    {
        SelectionCaptureService.SetClipboardTextSafe(output);

        if (delayed)
            await Task.Delay(200).ConfigureAwait(true);

        if (focusedWindow != IntPtr.Zero && NativeMethods.IsWindow(focusedWindow))
        {
            NativeMethods.SetForegroundWindow(focusedWindow);
            await Task.Delay(35).ConfigureAwait(true);
        }

        var released = await WaitForModifiersReleasedAsync().ConfigureAwait(true);
        if (!released)
            return (false, "Release the shortcut keys and try again.");

        await Task.Delay(25).ConfigureAwait(true);
        NativeMethods.SendCtrlShortcut(NativeMethods.VK_V);
        RememberUndo(focusedWindow);
        return (true, null);
    }

    public async Task<(bool Success, string? ErrorMessage)> UndoLastAsync()
    {
        if (_undoTargetWindow == IntPtr.Zero)
            return (false, null);

        var currentFg = NativeMethods.GetForegroundWindow();
        if (currentFg != _undoTargetWindow)
        {
            return (false, "Return to the original window before undoing.");
        }

        var released = await WaitForModifiersReleasedAsync().ConfigureAwait(true);
        if (!released)
            return (false, "Release the shortcut keys and try again.");

        await Task.Delay(25).ConfigureAwait(true);
        NativeMethods.SendCtrlShortcut(NativeMethods.VK_Z);
        ClearUndo();
        return (true, null);
    }

    private void RememberUndo(IntPtr focusedWindow)
    {
        _undoTimer.Stop();
        _undoTargetWindow = focusedWindow;
        _undoTimer.Start();
        UndoStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearUndo()
    {
        _undoTimer.Stop();
        _undoTargetWindow = IntPtr.Zero;
        UndoStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _undoTimer.Stop();
    }
}
