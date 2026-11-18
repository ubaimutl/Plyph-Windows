using System.Runtime.InteropServices;
using Plyph.Core;
using Plyph.Windows.Interop;
using Windows.System;

namespace Plyph.Windows.Shortcuts;

public sealed class GlobalHotkeyManager : IDisposable
{
    private readonly IPlyphSettings _settings;
    private readonly Dictionary<int, Action> _callbacks = new();
    private readonly Dictionary<string, bool> _registrationOk = new(StringComparer.Ordinal);
    private readonly NativeMethods.WndProcDelegate _wndProc;
    private IntPtr _hwnd;
    private int _nextId = 9000;

    public GlobalHotkeyManager(IPlyphSettings settings)
    {
        _settings = settings;
        _wndProc = WindowProc;

        if (OperatingSystem.IsWindows())
        {
            var className = $"PlyphHotkeyMsg_{Environment.ProcessId}_{Guid.NewGuid():N}";
            var wc = new NativeMethods.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = NativeMethods.GetModuleHandleW(null),
                lpszClassName = className
            };
            NativeMethods.RegisterClassExW(ref wc);
            _hwnd = NativeMethods.CreateWindowExW(
                0, className, "", 0, 0, 0, 0, 0,
                NativeMethods.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        }
    }

    public bool IsShortcutRegistered(string settingKey) =>
        _registrationOk.TryGetValue(settingKey, out var ok) && ok;

    public void BindShortcuts(
        Action onAsk,
        Action onCorrect,
        Action onRewrite,
        Action onOpenActions)
    {
        UnregisterAll();
        _registrationOk.Clear();
        RegisterSettingShortcut("ask-shortcut", onAsk);
        RegisterSettingShortcut("correct-shortcut", onCorrect);
        RegisterSettingShortcut("rewrite-shortcut", onRewrite);
        RegisterSettingShortcut("actions-shortcut", onOpenActions);
    }

    private void RegisterSettingShortcut(string settingKey, Action callback)
    {
        if (!OperatingSystem.IsWindows() || _hwnd == IntPtr.Zero)
            return;

        var values = _settings.GetStrv(settingKey);
        var accelerator = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(accelerator))
        {
            _registrationOk[settingKey] = true;
            return;
        }

        if (!TryParseAccelerator(accelerator, out var modifiers, out var vk))
        {
            _registrationOk[settingKey] = false;
            return;
        }

        var id = ++_nextId;
        if (NativeMethods.RegisterHotKey(_hwnd, id, modifiers | NativeMethods.MOD_NOREPEAT, vk))
        {
            _callbacks[id] = callback;
            _registrationOk[settingKey] = true;
        }
        else
        {
            _registrationOk[settingKey] = false;
        }
    }

    public void UnregisterAll()
    {
        if (OperatingSystem.IsWindows() && _hwnd != IntPtr.Zero)
        {
            foreach (var id in _callbacks.Keys)
                NativeMethods.UnregisterHotKey(_hwnd, id);
        }
        _callbacks.Clear();
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_callbacks.TryGetValue(id, out var callback))
            {
                callback();
                return IntPtr.Zero;
            }
        }
        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public static bool TryParseAccelerator(string accelerator, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(accelerator))
            return false;

        var raw = accelerator.Trim();
        var normalized = raw
            .Replace("<Primary>", "Ctrl+", StringComparison.OrdinalIgnoreCase)
            .Replace("<Control>", "Ctrl+", StringComparison.OrdinalIgnoreCase)
            .Replace("<Ctrl>", "Ctrl+", StringComparison.OrdinalIgnoreCase)
            .Replace("<Alt>", "Alt+", StringComparison.OrdinalIgnoreCase)
            .Replace("<Shift>", "Shift+", StringComparison.OrdinalIgnoreCase)
            .Replace("<Super>", "Win+", StringComparison.OrdinalIgnoreCase)
            .Replace("<Meta>", "Win+", StringComparison.OrdinalIgnoreCase);

        var parts = normalized.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        string? keyToken = null;
        foreach (var part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= NativeMethods.MOD_CONTROL;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= NativeMethods.MOD_ALT;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= NativeMethods.MOD_SHIFT;
            }
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("Super", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= NativeMethods.MOD_WIN;
            }
            else
            {
                keyToken = part;
            }
        }

        if (modifiers == 0 || string.IsNullOrEmpty(keyToken))
            return false;

        if (keyToken.Length == 1)
        {
            var ch = char.ToUpperInvariant(keyToken[0]);
            if (ch is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                virtualKey = ch;
                return true;
            }
        }

        if (Enum.TryParse<VirtualKey>(keyToken, ignoreCase: true, out var parsedKey))
        {
            virtualKey = (uint)parsedKey;
            return virtualKey != 0;
        }

        return false;
    }

    public static string FormatAcceleratorForDisplay(string? accelerator)
    {
        if (string.IsNullOrWhiteSpace(accelerator))
            return "Not set";

        if (!TryParseAccelerator(accelerator, out var mods, out var vk))
            return accelerator;

        var parts = new List<string>();
        if ((mods & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(((VirtualKey)vk).ToString());
        return string.Join(" + ", parts);
    }

    public void Dispose()
    {
        UnregisterAll();
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
