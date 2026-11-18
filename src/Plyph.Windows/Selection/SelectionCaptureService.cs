using System.Diagnostics;
using Plyph.Core;
using Plyph.Windows.Interop;

namespace Plyph.Windows.Selection;

public sealed record CapturedSelection(string Text, string? PrimaryText, bool IsConfirmedSelection = true);

public sealed class SelectionCaptureService
{
    private readonly IPlyphSettings _settings;

    public SelectionCaptureService(IPlyphSettings settings)
    {
        _settings = settings;
    }

    public async Task<CapturedSelection> ReadAskSelectionAsync(
        IntPtr sourceWindow, string? toolbarText, bool fromSelectionToolbar)
    {
        var empty = new CapturedSelection("", null, IsConfirmedSelection: false);
        if (WindowsSelectionMonitor.IsShellOrOwnWindow(sourceWindow) || !NativeMethods.IsWindow(sourceWindow))
            return empty;

        if (!string.IsNullOrWhiteSpace(toolbarText))
            return new CapturedSelection(toolbarText, toolbarText);

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != sourceWindow && WindowsSelectionMonitor.IsShellOrOwnWindow(foreground))
        {
            NativeMethods.SetForegroundWindow(sourceWindow);
            await Task.Delay(80);
        }
        if (NativeMethods.GetForegroundWindow() != sourceWindow)
            return empty;

        if (_settings.GetBoolean("selection-uia-enabled"))
        {
            var (status, text) = await Task.Run(() =>
            {
                var status = UiAutomationInterop.TryQuerySelectedText(out var selected, out _, includePointerElement: false);
                return (status, selected);
            });
            if (NativeMethods.GetForegroundWindow() != sourceWindow)
                return empty;
            if (status == UiaSelectionStatus.Selected && !string.IsNullOrWhiteSpace(text))
                return new CapturedSelection(text, text);
        }

        // Some editors copy the current line without a selection, so standalone Ask needs evidence of one.
        if (!fromSelectionToolbar && !NativeMethods.HasWin32EditSelection(sourceWindow))
            return empty;

        var copied = await TryExplicitCopyPreservingClipboardAsync(sourceWindow);
        return string.IsNullOrWhiteSpace(copied) ? empty : new CapturedSelection(copied, copied);
    }

    public async Task<CapturedSelection> ReadSelectionAsync(IntPtr focusedWindow, string? cachedFloatingText = null)
    {
        if (focusedWindow != IntPtr.Zero && NativeMethods.GetForegroundWindow() != focusedWindow)
        {
            NativeMethods.SetForegroundWindow(focusedWindow);
            await Task.Delay(80).ConfigureAwait(true);
        }

        if (UsesExplicitCopy(focusedWindow))
        {
            var copied = await ReadExplicitCopyAsync().ConfigureAwait(true);
            return new CapturedSelection(copied, null, IsConfirmedSelection: true);
        }

        // Layer 1: Try Windows UI Automation on the active control
        if (_settings.GetBoolean("selection-uia-enabled") &&
            UiAutomationInterop.TryGetSelectedText(out var uiaText, out _) &&
            !string.IsNullOrWhiteSpace(uiaText))
        {
            return new CapturedSelection(uiaText, uiaText, IsConfirmedSelection: true);
        }

        if (!string.IsNullOrWhiteSpace(cachedFloatingText))
        {
            return new CapturedSelection(cachedFloatingText, cachedFloatingText, IsConfirmedSelection: true);
        }

        // Layer 2: Automatic Ctrl+C explicit copy fallback (preserving previous clipboard contents)
        var explicitCopied = await TryExplicitCopyPreservingClipboardAsync().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(explicitCopied))
        {
            return new CapturedSelection(explicitCopied, explicitCopied, IsConfirmedSelection: true);
        }

        // Layer 3: Optional clipboard fallback
        if (!_settings.GetBoolean("clipboard-fallback"))
            return new CapturedSelection("", null, IsConfirmedSelection: false);

        var fallbackText = GetClipboardTextSafe();
        return new CapturedSelection(fallbackText, null, IsConfirmedSelection: false);
    }

    public bool UsesExplicitCopy(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return false;

        var raw = _settings.GetString("explicit-copy-apps");
        var allowed = raw
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim().ToLowerInvariant())
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList();

        if (allowed.Count == 0)
            return false;

        var values = new List<string>();
        var cls = NativeMethods.GetWindowClassName(window);
        if (!string.IsNullOrEmpty(cls))
            values.Add(cls.ToLowerInvariant());

        NativeMethods.GetWindowThreadProcessId(window, out var pid);
        if (pid != 0)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                if (!string.IsNullOrEmpty(proc.ProcessName))
                    values.Add(proc.ProcessName.ToLowerInvariant());
            }
            catch
            {
            }
        }

        return allowed.Any(name => values.Any(v => v.Contains(name, StringComparison.Ordinal)));
    }

    public async Task<string> ReadExplicitCopyAsync()
    {
        var previous = GetClipboardTextSafe();
        var released = await TextReplacer.WaitForModifiersReleasedAsync().ConfigureAwait(true);
        if (!released)
            throw new InvalidOperationException("Release the shortcut keys and try again.");

        await Task.Delay(25).ConfigureAwait(true);
        SetClipboardTextSafe("");
        await Task.Delay(25).ConfigureAwait(true);
        NativeMethods.SendCtrlShortcut(NativeMethods.VK_C);

        foreach (var delay in new[] { 60, 100, 180, 300 })
        {
            await Task.Delay(delay).ConfigureAwait(true);
            var text = GetClipboardTextSafe();
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (!string.IsNullOrEmpty(previous))
                    _ = RestoreClipboardAfterDelayAsync(previous);
                return text;
            }
        }

        if (!string.IsNullOrEmpty(previous))
            SetClipboardTextSafe(previous);
        if (_settings.GetBoolean("clipboard-fallback") && !string.IsNullOrWhiteSpace(previous))
            return previous;

        throw new InvalidOperationException("Could not capture selected text. Select it and try again.");
    }

    private async Task<string> TryExplicitCopyPreservingClipboardAsync(IntPtr requiredForeground = default)
    {
        var previous = GetClipboardTextSafe();
        var released = await TextReplacer.WaitForModifiersReleasedAsync().ConfigureAwait(true);
        if (!released)
            throw new InvalidOperationException("Release the shortcut keys and try again.");

        await Task.Delay(25).ConfigureAwait(true);
        if (requiredForeground != IntPtr.Zero && NativeMethods.GetForegroundWindow() != requiredForeground)
            return "";
        SetClipboardTextSafe("");
        await Task.Delay(25).ConfigureAwait(true);
        if (requiredForeground != IntPtr.Zero && NativeMethods.GetForegroundWindow() != requiredForeground)
        {
            if (!string.IsNullOrEmpty(previous))
                SetClipboardTextSafe(previous);
            return "";
        }
        NativeMethods.SendCtrlShortcut(NativeMethods.VK_C);

        foreach (var delay in new[] { 60, 100, 180 })
        {
            await Task.Delay(delay).ConfigureAwait(true);
            var text = GetClipboardTextSafe();
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (!string.IsNullOrEmpty(previous))
                    _ = RestoreClipboardAfterDelayAsync(previous);
                return text;
            }
        }

        if (!string.IsNullOrEmpty(previous))
            SetClipboardTextSafe(previous);
        return "";
    }

    private static async Task RestoreClipboardAfterDelayAsync(string previousText)
    {
        await Task.Delay(150).ConfigureAwait(true);
        SetClipboardTextSafe(previousText);
    }

    public static string GetClipboardTextSafe()
    {
        try
        {
            return Win32Clipboard.GetText();
        }
        catch
        {
            return "";
        }
    }

    public static void SetClipboardTextSafe(string text)
    {
        try
        {
            Win32Clipboard.SetText(text);
        }
        catch
        {
        }
    }
}
