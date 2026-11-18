using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Plyph.Core;
using Plyph.Windows.Interop;
using Windows.Graphics;
using XamlColor = Windows.UI.Color;
using XamlRectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using WinPoint = System.Drawing.Point;
using WinRect = System.Drawing.Rectangle;

namespace Plyph.Windows.UI;

/// <summary>
/// Compact WinUI 3 floating selection toolbar (non-activating via WS_EX_NOACTIVATE + MA_NOACTIVATE).
/// Supports three density modes: compact actions bar, compact icon strip, and single expandable button.
/// </summary>
public sealed partial class FloatingToolbarWindow : Window, IDisposable
{
    private const int FloatingHideDelayMs = 6500;
    private const int FloatingCollapseDelayMs = 2200;

    private readonly IPlyphSettings _settings;
    private readonly Func<List<PlyphAction>> _getActions;
    private readonly Action<PlyphAction> _onActivateAction;
    private readonly Action<bool> _onHideComplete;

    private readonly FloatingPopupChrome _chrome;
    private readonly DispatcherQueueTimer _dismissTimer;
    private readonly DispatcherQueueTimer _collapseTimer;

    private Button? _moreButton;
    private WinRect? _lastSelectionBounds;
    private WinPoint _selectionAnchor;
    private WinPoint _menuOrigin;
    private bool _expanded;
    private bool _extendedMenuOpen;
    private bool _showingFeedback;
    private bool _busy;
    private bool _disposed;
    private string _feedbackMessage = "";
    private string _feedbackState = "success";

    public int Left { get; private set; }
    public int Top { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsShown { get; private set; }
    public WinRect ScreenBounds => new(Left, Top, Width, Height);

    public FloatingToolbarWindow(
        IPlyphSettings settings,
        Func<List<PlyphAction>> getActions,
        Action<PlyphAction> onActivateAction,
        Action<bool> onHideComplete)
    {
        _settings = settings;
        _getActions = getActions;
        _onActivateAction = onActivateAction;
        _onHideComplete = onHideComplete;

        InitializeComponent();

        _chrome = new FloatingPopupChrome(this, nonActivating: true, allowTinySize: true, cornerRadiusDip: 8);
        FloatingPopupChrome.ConfigureSurfaceBorder(SurfaceBorder, 8);
        if (AppWindow != null)
            AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 1, 1));

        ThemeHelper.ApplyTheme(this, _settings, preferAcrylic: true);

        // Initialize XAML composition offscreen under WS_EX_NOACTIVATE without stealing focus, then immediately hide.
        if (OperatingSystem.IsWindows())
        {
            Activate();
            if (_chrome.Hwnd != IntPtr.Zero)
                NativeMethods.ShowWindow(_chrome.Hwnd, NativeMethods.SW_HIDE);
        }

        var dq = DispatcherQueue.GetForCurrentThread();
        _dismissTimer = dq.CreateTimer();
        _dismissTimer.IsRepeating = false;
        _dismissTimer.Tick += (_, _) =>
        {
            _dismissTimer.Stop();
            if (!IsCursorNearToolbar(0) && !_extendedMenuOpen && !_busy)
                Hide(suppressSelection: false);
        };

        _collapseTimer = dq.CreateTimer();
        _collapseTimer.IsRepeating = false;
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (_extendedMenuOpen || _busy)
                return;
            if (!IsCursorNearToolbar())
                CollapseIfButtonMode();
            else
                ScheduleCollapse();
        };
    }

    public bool IsScreenPointInside(WinPoint screenPoint, int margin = 6) =>
        IsShown && WinRect.Inflate(ScreenBounds, margin, margin).Contains(screenPoint);

    private double GetToolbarScale()
    {
        var requested = _settings.GetDouble("floating-toolbar-scale");
        return requested is >= 0.8 and <= 2.0 ? requested : 1.0;
    }

    private string GetLayoutMode()
    {
        var raw = (_settings.GetString("floating-toolbar-layout") ?? "bar").Trim().ToLowerInvariant();
        return raw is "icons" or "button" ? raw : "bar";
    }

    private bool IsVertical => _settings.GetString("floating-toolbar-orientation") == "vertical";

    public void RefreshTheme()
    {
        if (_disposed) return;
        ThemeHelper.ApplyTheme(this, _settings, preferAcrylic: true);
        if (IsShown)
        {
            RebuildContent();
            ResizeAndPlaceAt(new WinPoint(Left, Top), repositionFromAnchor: false);
        }
    }

    public void ShowAt(WinPoint anchorPoint, WinRect? selectionBounds = null)
    {
        if (_disposed) return;

        var mode = GetLayoutMode();
        _lastSelectionBounds = selectionBounds;
        _selectionAnchor = selectionBounds is { } bounds
            ? new WinPoint(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2)
            : anchorPoint;
        if (!IsShown && _chrome.Hwnd != IntPtr.Zero)
        {
            // Pick up the destination monitor's DPI while the window is still hidden.
            NativeMethods.SetWindowPos(_chrome.Hwnd, IntPtr.Zero, _selectionAnchor.X, _selectionAnchor.Y, 1, 1,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        _collapseTimer.Stop();
        _expanded = mode != "button";
        _extendedMenuOpen = false;
        _showingFeedback = false;
        _busy = false;
        _feedbackMessage = "";

        ThemeHelper.ApplyTheme(this, _settings, preferAcrylic: true);
        RebuildContent();
        ResizeAndPlaceAt(anchorPoint, repositionFromAnchor: true);

        ResetDismissTimeout(FloatingHideDelayMs);
    }

    public void Hide(bool suppressSelection)
    {
        _dismissTimer.Stop();
        _collapseTimer.Stop();
        _expanded = false;
        _extendedMenuOpen = false;
        _showingFeedback = false;
        _busy = false;
        _lastSelectionBounds = null;
        ExtendedDivider.Visibility = Visibility.Collapsed;
        ExtendedContainer.Visibility = Visibility.Collapsed;

        if (IsShown && OperatingSystem.IsWindows() && _chrome.Hwnd != IntPtr.Zero)
            NativeMethods.ShowWindow(_chrome.Hwnd, NativeMethods.SW_HIDE);

        IsShown = false;
        _onHideComplete(suppressSelection);
    }

    private void ToggleExtendedMenu()
    {
        if (_disposed || _busy || _showingFeedback)
            return;

        _extendedMenuOpen = !_extendedMenuOpen;
        if (_extendedMenuOpen)
        {
            _menuOrigin = new WinPoint(Left, Top);
            _expanded = true;
            _collapseTimer.Stop();
            _dismissTimer.Stop();
        }
        else if (GetLayoutMode() == "button")
        {
            ScheduleCollapse();
        }
        else
        {
            ResetDismissTimeout(FloatingHideDelayMs);
        }

        UpdateExtendedMenu();
        ResizeAndPlaceAt(_menuOrigin, repositionFromAnchor: false);
        if (_extendedMenuOpen)
            ExtendedScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    public void ShowFeedback(string message, string state = "success")
    {
        if (_disposed) return;
        var origin = _extendedMenuOpen ? _menuOrigin : new WinPoint(Left, Top);
        _expanded = false;
        _extendedMenuOpen = false;
        _showingFeedback = true;
        _busy = state == "working";
        _feedbackMessage = message;
        _feedbackState = state;
        _collapseTimer.Stop();
        _dismissTimer.Stop();

        ThemeHelper.ApplyTheme(this, _settings, preferAcrylic: true);
        RebuildContent();
        ResizeAndPlaceAt(origin, repositionFromAnchor: false);
    }

    private void CollapseIfButtonMode()
    {
        if (_extendedMenuOpen || _showingFeedback || _busy)
            return;

        if (GetLayoutMode() == "button")
        {
            _expanded = false;
            _collapseTimer.Stop();
            RebuildContent();
            ResizeAndPlaceAt(new WinPoint(Left, Top), repositionFromAnchor: false);
            ResetDismissTimeout(FloatingHideDelayMs);
        }
    }

    private void ExpandFromButtonMode()
    {
        if (_busy) return;
        _expanded = true;
        _showingFeedback = false;
        RebuildContent();
        ResizeAndPlaceAt(new WinPoint(Left, Top), repositionFromAnchor: false);
        ResetDismissTimeout(0);
        ScheduleCollapse();
    }

    private Brush GetPlyphMarkBrush()
    {
        var useAccent = _settings.GetBoolean("toolbar-accent-icon");
        var highContrast = ThemeHelper.IsHighContrastEnabled();
        var isDark = ThemeHelper.ShouldUseDark(_settings);
        if (highContrast || useAccent)
        {
            var key = highContrast ? "SystemColorWindowTextColorBrush" : "AccentTextFillColorPrimaryBrush";
            var themed = ThemeHelper.ResolveBrushForTheme(ThemeHelper.ResolveElementTheme(_settings), isDark, highContrast, key);
            if (themed != null)
                return themed;
        }

        return new SolidColorBrush(isDark ? Colors.White : Colors.Black);
    }

    private void RebuildContent()
    {
        ToolbarPanel.Margin = new Thickness(0);
        ToolbarPanel.Children.Clear();
        _moreButton = null;

        var s = GetToolbarScale();
        var btnHeight = Math.Round(32 * s);
        var iconSize = Math.Round(14 * s);
        var textSize = Math.Round(12 * s);
        var btnRadius = new CornerRadius(Math.Round(6 * s));
        var mode = GetLayoutMode();
        var vertical = IsVertical && !_showingFeedback;
        ToolbarPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        var isSingleButton = !_showingFeedback && mode == "button" && !_expanded;
        var pad = isSingleButton ? Math.Round(2 * s) : Math.Round(3 * s);

        SurfaceBorder.Padding = new Thickness(pad);
        if (!NativeMethods.IsWindows11OrGreater())
            SurfaceBorder.CornerRadius = new CornerRadius(Math.Round(8 * s));
        ToolbarPanel.Spacing = Math.Round(2 * s);

        if (_showingFeedback)
        {
            ExtendedDivider.Visibility = Visibility.Collapsed;
            ExtendedContainer.Visibility = Visibility.Collapsed;
            ToolbarPanel.Children.Add(BuildFeedbackView(s, btnHeight, iconSize, textSize));
            return;
        }

        var markBrush = GetPlyphMarkBrush();

        if (isSingleButton)
        {
            ExtendedDivider.Visibility = Visibility.Collapsed;
            ExtendedContainer.Visibility = Visibility.Collapsed;
            var singleSize = Math.Round(32 * s);
            var markBtn = CreateToolbarButton(singleSize, singleSize, new Thickness(0), btnRadius);
            markBtn.Content = FloatingSurfaceHelper.CreatePlyphMark(Math.Round(15 * s), markBrush);
            SetButtonLabel(markBtn, "Plyph actions");
            markBtn.Click += (_, _) => ExpandFromButtonMode();
            ToolbarPanel.Children.Add(markBtn);
            return;
        }

        var actions = GetQuickActions();
        var iconsOnly = mode == "icons" || vertical;

        if (mode is "icons" or "button")
        {
            var logoBtn = CreateToolbarButton(vertical ? btnHeight : Math.Round(26 * s), btnHeight, new Thickness(0), btnRadius);
            logoBtn.Content = FloatingSurfaceHelper.CreatePlyphMark(Math.Round(14 * s), markBrush);
            SetButtonLabel(logoBtn, mode == "button" ? "Collapse toolbar" : "Plyph actions");
            logoBtn.Click += (_, _) =>
            {
                if (mode != "button")
                {
                    ToggleExtendedMenu();
                    return;
                }

                if (_extendedMenuOpen)
                    ToggleExtendedMenu();
                CollapseIfButtonMode();
            };
            ToolbarPanel.Children.Add(logoBtn);
            ToolbarPanel.Children.Add(CreateDivider(s, vertical));
        }

        var firstQuickAction = ToolbarPanel.Children.Count;
        foreach (var act in actions)
        {
            var compactLabel = FloatingSurfaceHelper.GetCompactLabel(act);
            var btn = CreateToolbarButton(
                iconsOnly ? btnHeight : double.NaN,
                btnHeight,
                iconsOnly ? new Thickness(0) : new Thickness(Math.Round(8 * s), 0, Math.Round(8 * s), 0),
                btnRadius);

            var icon = ActionIcons.Create(act.Icon, iconSize);
            if (iconsOnly)
            {
                btn.Content = icon;
            }
            else
            {
                var sp = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = Math.Round(6 * s),
                    VerticalAlignment = VerticalAlignment.Center
                };
                sp.Children.Add(icon);
                sp.Children.Add(new TextBlock
                {
                    Text = compactLabel,
                    FontSize = textSize,
                    MaxWidth = Math.Round(90 * s),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                });
                btn.Content = sp;
            }

            SetButtonLabel(btn, act.Name);
            btn.Click += (_, _) => _onActivateAction(act);
            ToolbarPanel.Children.Add(btn);
        }

        ToolbarPanel.Children.Add(CreateDivider(s, vertical));

        var moreBtn = CreateToolbarButton(btnHeight, btnHeight, new Thickness(0), btnRadius);
        moreBtn.Content = new FontIcon
        {
            FontFamily = Application.Current.Resources["SymbolThemeFontFamily"] as FontFamily,
            Glyph = "\uE712",
            FontSize = iconSize
        };
        moreBtn.Click += (_, _) => ToggleExtendedMenu();
        _moreButton = moreBtn;
        ToolbarPanel.Children.Add(moreBtn);

        // Keep the overflow button visible even when pinned action labels take up more room.
        NativeMethods.GetWorkAreaMonitorIndex(_selectionAnchor, out var work);
        var workLength = vertical ? work.Bottom - work.Top : work.Right - work.Left;
        var budget = (workLength - 16) / _chrome.GetDpiScale()
            - pad * 2 - SurfaceBorder.BorderThickness.Left - SurfaceBorder.BorderThickness.Right;
        var dividerIndex = ToolbarPanel.Children.Count - 2;
        while (dividerIndex > firstQuickAction)
        {
            var measured = FloatingSurfaceHelper.MeasureDip(ToolbarPanel);
            if ((vertical ? measured.Height : measured.Width) <= budget)
                break;
            ToolbarPanel.Children.RemoveAt(--dividerIndex);
        }
        if (dividerIndex == firstQuickAction)
        {
            ToolbarPanel.Children.RemoveAt(dividerIndex);
            if (firstQuickAction > 0)
                ToolbarPanel.Children.RemoveAt(firstQuickAction - 1);
        }

        UpdateExtendedMenu();
    }

    private void UpdateExtendedMenu()
    {
        ExtendedActionsStack.Children.Clear();
        ExtendedDivider.Visibility = ExtendedContainer.Visibility = _extendedMenuOpen
            ? Visibility.Visible : Visibility.Collapsed;
        if (_extendedMenuOpen)
        {
            var s = GetToolbarScale();
            PopulateExtendedMenu(s, Math.Round(14 * s), new CornerRadius(Math.Round(6 * s)));
        }

        UpdateMoreButtonHighlight();
        if (_moreButton != null)
            SetButtonLabel(_moreButton, _extendedMenuOpen ? "Hide actions" : "More actions");
    }

    private void PopulateExtendedMenu(double s, double iconSize, CornerRadius radius)
    {
        var allActions = _getActions();
        var itemHeight = Math.Round(32 * s);
        var itemTextSize = Math.Round(12.5 * s);
        var hintColor = ThemeHelper.GetThemeBrush(RootGrid, "TextFillColorSecondaryBrush");
        var dividerColor = Application.Current.Resources.TryGetValue("DividerStrokeColorDefaultBrush", out var divObj) && divObj is Brush divBrush
            ? divBrush
            : new SolidColorBrush(XamlColor.FromArgb(40, 128, 128, 128));

        foreach (var act in allActions)
        {
            if (act.SeparatorBefore && ExtendedActionsStack.Children.Count > 0)
            {
                ExtendedActionsStack.Children.Add(new XamlRectangle
                {
                    Height = 1,
                    Margin = new Thickness(Math.Round(6 * s), Math.Round(2 * s), Math.Round(6 * s), Math.Round(2 * s)),
                    Fill = dividerColor
                });
            }

            var rowGrid = new Grid { ColumnSpacing = Math.Round(8 * s) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = ActionIcons.Create(act.Icon, iconSize);
            icon.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(icon, 0);
            rowGrid.Children.Add(icon);

            var label = new TextBlock
            {
                Text = act.Name,
                FontSize = itemTextSize,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(label, 1);
            rowGrid.Children.Add(label);

            var shortcut = GetShortcutHint(act);
            if (!string.IsNullOrEmpty(shortcut))
            {
                var hint = new TextBlock
                {
                    Text = shortcut,
                    FontSize = Math.Round(11 * s),
                    Foreground = hintColor,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(hint, 2);
                rowGrid.Children.Add(hint);
            }

            var btn = CreateToolbarButton(double.NaN, itemHeight, new Thickness(Math.Round(8 * s), 0, Math.Round(8 * s), 0), radius);
            btn.HorizontalAlignment = HorizontalAlignment.Stretch;
            btn.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            btn.Content = rowGrid;

            SetButtonLabel(btn, string.IsNullOrEmpty(shortcut) ? act.Name : $"{act.Name} ({shortcut})");
            btn.Click += (_, _) => _onActivateAction(act);
            ExtendedActionsStack.Children.Add(btn);
        }

        ExtendedProviderText.Text = $"Provider: {ProvidersStore.FormatProviderBadge(_settings)}";
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
        return key != null ? (_settings.GetStrv(key).FirstOrDefault() ?? "") : "";
    }

    private UIElement BuildFeedbackView(double s, double btnHeight, double iconSize, double textSize)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Math.Round(7 * s),
            Padding = new Thickness(Math.Round(8 * s), 0, Math.Round(10 * s), 0),
            Height = btnHeight,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (_feedbackState == "working")
        {
            row.Children.Add(new ProgressRing
            {
                Width = Math.Round(14 * s),
                Height = Math.Round(14 * s),
                IsActive = true
            });
        }
        else
        {
            var color = _feedbackState == "error"
                ? XamlColor.FromArgb(255, 220, 38, 38)
                : XamlColor.FromArgb(255, 22, 163, 74);
            row.Children.Add(new FontIcon
            {
                FontFamily = Application.Current.Resources["SymbolThemeFontFamily"] as FontFamily,
                Glyph = _feedbackState == "error" ? "\uEA39" : "\uE73E",
                FontSize = iconSize,
                Foreground = new SolidColorBrush(color)
            });
        }

        row.Children.Add(new TextBlock
        {
            Text = _feedbackMessage,
            FontSize = textSize,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = Math.Round(240 * s),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        return row;
    }

    private List<PlyphAction> GetQuickActions()
    {
        var allActions = _getActions();
        var list = new List<PlyphAction>();
        foreach (var id in ActionsStore.GetQuickActionIds(_settings))
        {
            var act = allActions.FirstOrDefault(a => a.Id == id) ?? allActions.FirstOrDefault(a => a.Mode == id);
            if (act != null && !list.Contains(act))
                list.Add(act);
        }
        return list;
    }

    private Button CreateToolbarButton(double width, double height, Thickness padding, CornerRadius radius)
    {
        var btn = new Button
        {
            Height = height,
            MinWidth = 0,
            MinHeight = 0,
            Padding = padding,
            CornerRadius = radius,
            VerticalAlignment = VerticalAlignment.Center,
            UseSystemFocusVisuals = false,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent)
        };
        if (!double.IsNaN(width))
            btn.Width = width;
        if (RootGrid.Resources.TryGetValue("ToolbarMinimalButtonStyle", out var minStyleObj) && minStyleObj is Style minStyle)
            btn.Style = minStyle;
        return btn;
    }

    private static XamlRectangle CreateDivider(double s, bool vertical)
    {
        return new XamlRectangle
        {
            Width = vertical ? Math.Round(15 * s) : 1,
            Height = vertical ? 1 : Math.Round(15 * s),
            Margin = vertical
                ? new Thickness(0, Math.Round(2 * s), 0, Math.Round(2 * s))
                : new Thickness(Math.Round(2 * s), 0, Math.Round(2 * s), 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = Application.Current.Resources.TryGetValue("DividerStrokeColorDefaultBrush", out var bObj) && bObj is Brush b
                ? b
                : new SolidColorBrush(XamlColor.FromArgb(48, 128, 128, 128))
        };
    }

    private void UpdateMoreButtonHighlight()
    {
        if (_moreButton == null) return;
        _moreButton.Background = _extendedMenuOpen
            ? (ThemeHelper.GetThemeBrush(RootGrid, "SubtleFillColorSecondaryBrush") ?? new SolidColorBrush(XamlColor.FromArgb(30, 128, 128, 128)))
            : new SolidColorBrush(Colors.Transparent);
    }

    private void ResizeAndPlaceAt(WinPoint anchorPoint, bool repositionFromAnchor)
    {
        const int margin = 8;
        NativeMethods.GetWorkAreaMonitorIndex(_selectionAnchor, out var work);
        var scale = _chrome.GetDpiScale();
        var maxWidth = Math.Max(1, (work.Right - work.Left - margin * 2) / scale);
        var maxHeight = Math.Max(1, (work.Bottom - work.Top - margin * 2) / scale);
        var vertical = ToolbarPanel.Orientation == Orientation.Vertical;
        var menuBeside = _extendedMenuOpen && vertical;
        var menuBeforeToolbar = false;
        MainStack.Orientation = menuBeside ? Orientation.Horizontal : Orientation.Vertical;
        ExtendedDivider.Width = menuBeside ? 1 : double.NaN;
        ExtendedDivider.Height = menuBeside ? double.NaN : 1;
        ExtendedDivider.Margin = menuBeside ? new Thickness(3, 4, 3, 4) : new Thickness(4, 3, 4, 3);
        ToolbarPanel.VerticalAlignment = menuBeside ? VerticalAlignment.Top : VerticalAlignment.Center;
        ToolbarPanel.Margin = new Thickness(0);
        var inset = SurfaceBorder.Padding.Left + SurfaceBorder.Padding.Right
            + SurfaceBorder.BorderThickness.Left + SurfaceBorder.BorderThickness.Right;
        var rowSize = FloatingSurfaceHelper.MeasureDip(ToolbarPanel);
        var verticalInset = SurfaceBorder.Padding.Top + SurfaceBorder.Padding.Bottom
            + SurfaceBorder.BorderThickness.Top + SurfaceBorder.BorderThickness.Bottom;
        var rowHeight = rowSize.Height + verticalInset;

        if (_extendedMenuOpen)
        {
            var menuWidth = Math.Clamp(maxWidth - inset, 1, Math.Round(320 * GetToolbarScale()));
            if (menuBeside)
            {
                var dividerWidth = ExtendedDivider.Width + ExtendedDivider.Margin.Left + ExtendedDivider.Margin.Right;
                var before = (_menuOrigin.X - work.Left - margin) / scale - dividerWidth;
                var after = (work.Right - margin - _menuOrigin.X) / scale - rowSize.Width - inset - dividerWidth;
                menuBeforeToolbar = menuWidth > after && before > after;
                menuWidth = Math.Clamp(menuBeforeToolbar ? before : after, 1, menuWidth);
            }

            ExtendedContainer.Width = menuWidth;
            ExtendedScroll.MaxHeight = Math.Round(250 * GetToolbarScale());

            var menuHeight = FloatingSurfaceHelper.MeasureDip(ExtendedContainer, menuWidth).Height
                + (menuBeside ? 0 : FloatingSurfaceHelper.MeasureDip(ExtendedDivider).Height);
            var listHeight = FloatingSurfaceHelper.MeasureDip(ExtendedScroll, menuWidth).Height;
            var below = (work.Bottom - margin - _menuOrigin.Y) / scale - rowHeight;
            var above = (_menuOrigin.Y - work.Top - margin) / scale;
            if (!menuBeside)
                menuBeforeToolbar = menuHeight > below && above > below;
            var availableHeight = menuBeside ? maxHeight - verticalInset : menuBeforeToolbar ? above : below;
            ExtendedScroll.MaxHeight = Math.Clamp(availableHeight - (menuHeight - listHeight),
                1, ExtendedScroll.MaxHeight);
        }

        var toolbarIndex = menuBeforeToolbar ? 2 : 0;
        if (MainStack.Children.IndexOf(ToolbarPanel) != toolbarIndex)
        {
            MainStack.Children.Remove(ToolbarPanel);
            MainStack.Children.Insert(toolbarIndex, ToolbarPanel);
            MainStack.Children.Remove(ExtendedDivider);
            MainStack.Children.Insert(1, ExtendedDivider);
        }

        var desired = FloatingSurfaceHelper.MeasureDip(SurfaceBorder, maxWidth, maxHeight);
        var physW = Math.Max(1, (int)Math.Ceiling(desired.Width * scale));
        var physH = Math.Max(1, (int)Math.Ceiling(desired.Height * scale));

        int x, y;
        if (repositionFromAnchor)
        {
            if (_lastSelectionBounds.HasValue)
                (x, y) = FloatingSurfaceHelper.PositionNearSelectionRect(
                    _lastSelectionBounds.Value, physW, physH, gapPx: 6, alignToTop: vertical);
            else
                (x, y) = FloatingSurfaceHelper.PositionNearAnchor(anchorPoint, physW, physH, preferAbove: false, gapPx: 8);
        }
        else
        {
            var origin = _extendedMenuOpen ? _menuOrigin : anchorPoint;
            var left = menuBeside && menuBeforeToolbar
                ? origin.X - (int)Math.Round((desired.Width - inset - rowSize.Width) * scale)
                : origin.X;
            x = Math.Clamp(left, work.Left + margin, Math.Max(work.Left + margin, work.Right - physW - margin));
            var top = !menuBeside && menuBeforeToolbar
                ? origin.Y - (physH - (int)Math.Ceiling(rowHeight * scale))
                : origin.Y;
            y = Math.Clamp(top, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - physH - margin));
            if (menuBeside)
                ToolbarPanel.Margin = new Thickness(0, Math.Clamp((origin.Y - y) / scale, 0,
                    Math.Max(0, desired.Height - rowHeight)), 0, 0);
            else if (_extendedMenuOpen)
                ToolbarPanel.Margin = new Thickness(Math.Clamp((origin.X - x) / scale, 0,
                    Math.Max(0, desired.Width - inset - rowSize.Width)), 0, 0, 0);
        }

        Left = x;
        Top = y;
        Width = physW;
        Height = physH;

        _chrome.UpdateRegionForWin10(physW, physH);

        if (_chrome.Hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                _chrome.Hwnd,
                NativeMethods.HWND_TOPMOST,
                x, y, physW, physH,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            if (!IsShown || !NativeMethods.IsWindowVisible(_chrome.Hwnd))
                NativeMethods.ShowWindow(_chrome.Hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        }
        else
        {
            AppWindow?.MoveAndResize(new RectInt32(x, y, physW, physH));
        }

        IsShown = true;
    }

    private static void SetButtonLabel(Button button, string label)
    {
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
    }

    private bool IsCursorNearToolbar(int margin = 16)
    {
        if (!IsShown || !NativeMethods.GetCursorPos(out var pt))
            return false;
        return IsScreenPointInside(new WinPoint(pt.X, pt.Y), margin);
    }

    private void ScheduleCollapse()
    {
        _collapseTimer.Stop();
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(FloatingCollapseDelayMs);
        _collapseTimer.Start();
    }

    private void ResetDismissTimeout(int delayMs)
    {
        _dismissTimer.Stop();
        if (delayMs > 0)
        {
            _dismissTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
            _dismissTimer.Start();
        }
    }

    private void SurfaceBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _dismissTimer.Stop();
        _collapseTimer.Stop();
    }

    private void SurfaceBorder_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_extendedMenuOpen || _showingFeedback || _busy)
            return;

        if (GetLayoutMode() == "button" && _expanded)
            ScheduleCollapse();
        else
            ResetDismissTimeout(FloatingHideDelayMs);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dismissTimer.Stop();
        _collapseTimer.Stop();
        _chrome.Dispose();
        try { Close(); } catch { }
    }
}
