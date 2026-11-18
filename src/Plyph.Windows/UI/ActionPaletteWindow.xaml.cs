using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Plyph.Core;
using Plyph.Windows.Interop;
using Windows.Graphics;
using Windows.System;
using XamlColor = Windows.UI.Color;
using XamlRectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using WinPoint = System.Drawing.Point;
using WinRect = System.Drawing.Rectangle;

namespace Plyph.Windows.UI;

/// <summary>
/// WinUI 3 searchable actions menu &amp; command palette.
/// Non-activating when anchored below/above the floating toolbar so the user's selection never drops;
/// activating with auto-focused search when opened via global hotkey or system tray.
/// </summary>
public sealed partial class ActionPaletteWindow : Window, IDisposable
{
    private readonly IPlyphSettings _settings;
    private readonly List<PlyphAction> _allActions;
    private readonly Action<PlyphAction> _onActivate;
    private readonly Action _onClose;
    private readonly bool _nonActivating;
    private readonly FloatingPopupChrome _chrome;

    private List<PlyphAction> _filtered = new();
    private readonly List<Button> _itemButtons = new();
    private int _selectedIndex;
    private bool _finished;
    private bool _readyForDeactivate;

    public int Left { get; private set; }
    public int Top { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsShown { get; private set; }
    public WinRect ScreenBounds => new(Left, Top, Width, Height);

    public ActionPaletteWindow(
        IPlyphSettings settings,
        List<PlyphAction> actions,
        Action<PlyphAction> onActivate,
        Action onClose,
        WinRect? anchorScreenRect = null,
        bool nearPointer = false)
    {
        _settings = settings;
        _allActions = actions;
        _onActivate = onActivate;
        _onClose = onClose;
        _nonActivating = anchorScreenRect.HasValue;

        InitializeComponent();

        _chrome = new FloatingPopupChrome(this, nonActivating: _nonActivating, allowTinySize: true, cornerRadiusDip: 8);
        ThemeHelper.ApplyTheme(this, _settings, preferAcrylic: true);

        SearchBox.Visibility = _nonActivating ? Visibility.Collapsed : Visibility.Visible;
        ProviderFooterText.Text = $"Provider: {ProvidersStore.FormatProviderBadge(_settings)}";

        PopulateList("");
        PositionAndShow(anchorScreenRect, nearPointer);

        Activated += (_, e) =>
        {
            if (!_nonActivating && e.WindowActivationState == WindowActivationState.Deactivated && _readyForDeactivate)
                Dismiss();
        };

        Closed += (_, _) =>
        {
            _chrome.Dispose();
            Finish(null);
        };
    }

    public bool IsScreenPointInside(WinPoint screenPoint, int margin = 6) =>
        IsShown && WinRect.Inflate(ScreenBounds, margin, margin).Contains(screenPoint);

    public void Dismiss() => Finish(null);

    private void PopulateList(string query)
    {
        ItemsStack.Children.Clear();
        _itemButtons.Clear();

        var q = query.Trim();
        _filtered = string.IsNullOrEmpty(q)
            ? _allActions.ToList()
            : _allActions.Where(a =>
                a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.Mode.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(a.Prompt) && a.Prompt.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();

        if (_filtered.Count == 0)
        {
            ItemsStack.Children.Add(new TextBlock
            {
                Text = "No matching actions",
                FontSize = 12,
                Foreground = ThemeHelper.GetThemeBrush(RootGrid, "TextFillColorSecondaryBrush"),
                Margin = new Thickness(8, 10, 8, 10)
            });
            _selectedIndex = -1;
            return;
        }

        var showSeparators = string.IsNullOrEmpty(q);
        for (var i = 0; i < _filtered.Count; i++)
        {
            var act = _filtered[i];
            if (showSeparators && act.SeparatorBefore)
            {
                ItemsStack.Children.Add(new XamlRectangle
                {
                    Height = 1,
                    Margin = new Thickness(6, 3, 6, 3),
                    Fill = Application.Current.Resources.TryGetValue("DividerStrokeColorDefaultBrush", out var div) && div is Brush b
                        ? b
                        : new SolidColorBrush(XamlColor.FromArgb(40, 128, 128, 128))
                });
            }

            var btn = BuildActionRow(act, i);
            _itemButtons.Add(btn);
            ItemsStack.Children.Add(btn);
        }

        _selectedIndex = 0;
        UpdateSelectionHighlight();
    }

    private Button BuildActionRow(PlyphAction act, int index)
    {
        var rowGrid = new Grid { ColumnSpacing = 8 };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = ActionIcons.Create(act.Icon, 14);
        icon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(icon, 0);
        rowGrid.Children.Add(icon);

        var nameText = new TextBlock
        {
            Text = act.Name,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(nameText, 1);
        rowGrid.Children.Add(nameText);

        var shortcutHint = GetShortcutHint(act);
        if (!string.IsNullOrEmpty(shortcutHint))
        {
            var hintBlock = new TextBlock
            {
                Text = shortcutHint,
                FontSize = 11,
                Foreground = ThemeHelper.GetThemeBrush(RootGrid, "TextFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(hintBlock, 2);
            rowGrid.Children.Add(hintBlock);
        }

        var btn = new Button
        {
            Content = rowGrid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Height = 30,
            MinHeight = 30,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent)
        };
        if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out var styleObj) && styleObj is Style subtleStyle)
            btn.Style = subtleStyle;

        btn.PointerEntered += (_, _) =>
        {
            _selectedIndex = index;
            UpdateSelectionHighlight();
        };
        btn.Click += (_, _) => Finish(act);
        return btn;
    }

    private string GetShortcutHint(PlyphAction act)
    {
        var key = act.Mode switch
        {
            "ask" => "ask-shortcut",
            "correct" => "correct-shortcut",
            "rewrite" => "rewrite-shortcut",
            _ => null
        };
        if (key == null) return "";
        return _settings.GetStrv(key).FirstOrDefault() ?? "";
    }

    private void UpdateSelectionHighlight()
    {
        if (_nonActivating)
            return;

        var selBrush = ThemeHelper.GetThemeBrush(RootGrid, "SubtleFillColorSecondaryBrush")
                       ?? new SolidColorBrush(XamlColor.FromArgb(26, 128, 128, 128));
        var clearBrush = new SolidColorBrush(Colors.Transparent);

        for (var i = 0; i < _itemButtons.Count; i++)
            _itemButtons[i].Background = i == _selectedIndex ? selBrush : clearBrush;
    }

    private void PositionAndShow(WinRect? anchorScreenRect, bool nearPointer)
    {
        var scale = _chrome.GetDpiScale();
        var s = Math.Clamp(_settings.GetDouble("floating-toolbar-scale"), 0.85, 1.5);
        if (double.IsNaN(s) || s <= 0) s = 1.0;

        var targetDipW = Math.Round(256 * Math.Max(1.0, s * 0.95));
        SurfaceBorder.Width = targetDipW;
        SurfaceBorder.UpdateLayout();

        var measured = FloatingSurfaceHelper.MeasureDip(SurfaceBorder, targetDipW, 420);
        var physW = Math.Max(220, (int)Math.Ceiling(targetDipW * scale));
        var physH = Math.Max(70, (int)Math.Ceiling(measured.Height * scale));

        var refPoint = anchorScreenRect.HasValue
            ? new WinPoint(anchorScreenRect.Value.Right, anchorScreenRect.Value.Bottom)
            : (NativeMethods.GetCursorPos(out var cur) ? new WinPoint(cur.X, cur.Y) : new WinPoint(400, 300));
        NativeMethods.GetWorkAreaMonitorIndex(refPoint, out var work);

        int x, y;
        if (anchorScreenRect.HasValue)
        {
            var anchor = anchorScreenRect.Value;
            x = anchor.Right - physW;
            if (x < work.Left + 8) x = anchor.Left;
            y = anchor.Bottom + 4;
            if (y + physH > work.Bottom - 8 && anchor.Top - physH - 4 >= work.Top + 8)
                y = anchor.Top - physH - 4;
        }
        else if (nearPointer)
        {
            x = refPoint.X - physW / 2;
            y = refPoint.Y + 12;
        }
        else
        {
            x = work.Left + (work.Right - work.Left - physW) / 2;
            y = work.Top + (work.Bottom - work.Top - physH) / 2;
        }

        x = Math.Clamp(x, work.Left + 8, Math.Max(work.Left + 8, work.Right - physW - 8));
        y = Math.Clamp(y, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - physH - 8));

        Left = x;
        Top = y;
        Width = physW;
        Height = physH;

        _chrome.UpdateRegionForWin10(physW, physH);

        if (_nonActivating)
        {
            if (AppWindow != null)
                AppWindow.MoveAndResize(new RectInt32(x, y, physW, physH));
            Activate();
            if (_chrome.Hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(
                    _chrome.Hwnd, NativeMethods.HWND_TOPMOST, x, y, physW, physH,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                NativeMethods.ShowWindow(_chrome.Hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            }
        }
        else
        {
            if (AppWindow != null)
                AppWindow.MoveAndResize(new RectInt32(x, y, physW, physH));
            Activate();
            ThemeHelper.BringWindowToFront(this, keepOnTop: true);
            SearchBox.Focus(FocusState.Programmatic);
            DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(120);
                _readyForDeactivate = true;
            });
        }

        IsShown = true;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PopulateList(SearchBox.Text);
        // Resize smoothly if list height shrunk/grew
        var scale = _chrome.GetDpiScale();
        SurfaceBorder.UpdateLayout();
        var measured = FloatingSurfaceHelper.MeasureDip(SurfaceBorder, SurfaceBorder.Width, 420);
        var physH = Math.Max(70, (int)Math.Ceiling(measured.Height * scale));
        if (Math.Abs(physH - Height) > 2 && AppWindow != null)
        {
            Height = physH;
            NativeMethods.GetWorkAreaMonitorIndex(new WinPoint(Left, Top), out var work);
            Top = Math.Clamp(Top, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - physH - 8));
            AppWindow.MoveAndResize(new RectInt32(Left, Top, Width, Height));
            _chrome.UpdateRegionForWin10(Width, Height);
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Finish(null);
            return;
        }
        if (e.Key == VirtualKey.Up && _filtered.Count > 0)
        {
            e.Handled = true;
            _selectedIndex = (_selectedIndex - 1 + _filtered.Count) % _filtered.Count;
            UpdateSelectionHighlight();
            return;
        }
        if (e.Key == VirtualKey.Down && _filtered.Count > 0)
        {
            e.Handled = true;
            _selectedIndex = (_selectedIndex + 1) % _filtered.Count;
            UpdateSelectionHighlight();
            return;
        }
        if (e.Key == VirtualKey.Enter && _selectedIndex >= 0 && _selectedIndex < _filtered.Count)
        {
            e.Handled = true;
            Finish(_filtered[_selectedIndex]);
        }
    }

    private void Finish(PlyphAction? action)
    {
        if (_finished)
            return;
        _finished = true;
        IsShown = false;
        _chrome.Dispose();
        try { Close(); } catch { }
        _onClose();
        if (action != null)
            _onActivate(action);
    }

    public void Dispose() => Finish(null);
}
