using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Plyph.Core;
using Plyph.Windows.Interop;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Text;
using WinPoint = System.Drawing.Point;

namespace Plyph.Windows.UI;

public sealed partial class CompactResultWindow : Window
{
    private readonly IPlyphSettings _settings;
    private readonly FloatingPopupChrome _chrome;
    private readonly WinPoint _anchorPoint;
    private readonly Action<string>? _onCopyError;
    private readonly Action? _onOpenSettings;
    private readonly Action? _onCancelStandalone;

    private ResultSession? _session;
    private Func<string, bool, Task<ResultSession?>>? _onAskSubmit;
    private bool _refining;
    private bool _discussing;
    private bool _enlarged;
    private bool _isClosing;
    private bool _updatingText;
    private bool _pinned;
    private bool _isStandaloneError;
    private string _standaloneErrorText = "";

    private CompactResultWindow(
        IPlyphSettings settings,
        WinPoint anchorPoint,
        Action<string>? onCopyError = null,
        Action? onOpenSettings = null,
        Action? onCancelStandalone = null)
    {
        _settings = settings;
        _anchorPoint = anchorPoint;
        _onCopyError = onCopyError;
        _onOpenSettings = onOpenSettings;
        _onCancelStandalone = onCancelStandalone;

        InitializeComponent();
        ActionIcons.FitPath(RefineIcon, 15);
        ActionIcons.FitPath(ChangesIcon, 15);

        _chrome = new FloatingPopupChrome(this, nonActivating: false, allowTinySize: true, cornerRadiusDip: 8);
        FloatingPopupChrome.ConfigureSurfaceBorder(SurfaceBorder, 8);
        FloatingSurfaceHelper.EnableDrag(this, HeaderDragArea);

        ThemeHelper.ApplyTheme(this, _settings, preferAcrylic: true,
            useBackdrop: _settings.GetString("result-popup-appearance") != "solid");

        RootGrid.ActualThemeChanged += (_, _) =>
        {
            if (_session?.ShowChanges == true)
                RenderDiff();
            if (_session?.Discussion.Count > 0)
                RenderDiscussionTranscript();
        };

        Activated += (_, e) =>
        {
            if (_isClosing)
                return;
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                // The activation event also catches fast clicks that fall between timer ticks.
                if (_settings.GetBoolean("result-popup-dismiss-on-outside-click") &&
                    (NativeMethods.IsKeyDown(NativeMethods.VK_LBUTTON) ||
                     NativeMethods.IsKeyDown(NativeMethods.VK_RBUTTON) ||
                     NativeMethods.IsKeyDown(NativeMethods.VK_MBUTTON)) &&
                    NativeMethods.GetCursorPos(out var point))
                {
                    DispatcherQueue.TryEnqueue(() => DismissOnOutsideClick(new WinPoint(point.X, point.Y)));
                }
            }
            else if (AskInputPanel.Visibility == Visibility.Visible)
            {
                AskPromptBox.Focus(FocusState.Programmatic);
            }
        };

        Closed += (_, _) =>
        {
            _isClosing = true;
            _chrome.Dispose();
            if (_session != null)
            {
                _session.Dispose();
                _session.CompleteClose();
            }
            else
            {
                _onCancelStandalone?.Invoke();
            }
        };
    }

    public static CompactResultWindow CreateForResult(ResultSession session, bool startEnlarged = false)
    {
        var win = new CompactResultWindow(session.Settings, session.AnchorPoint);
        win.SetEnlargedState(startEnlarged, reposition: false);
        win.BindSession(session, initialResize: true);
        return win;
    }

    public static CompactResultWindow CreateForAsk(
        IPlyphSettings settings,
        WinPoint anchorPoint,
        string providerBadge,
        bool hasSelection,
        Func<string, bool, Task<ResultSession?>> onSubmit,
        Action<string> onCopyError,
        Action onOpenSettings,
        Action onCancel)
    {
        var win = new CompactResultWindow(settings, anchorPoint, onCopyError, onOpenSettings, onCancel)
        {
            _onAskSubmit = onSubmit
        };
        win.ShowAskInputMode(providerBadge, hasSelection);
        return win;
    }

    public static CompactResultWindow CreateForError(
        IPlyphSettings settings,
        WinPoint anchorPoint,
        string errorMessage,
        Action<string> onCopyError,
        Action? onOpenSettings = null)
    {
        var win = new CompactResultWindow(settings, anchorPoint, onCopyError: onCopyError, onOpenSettings: onOpenSettings);
        win.ShowErrorMode(errorMessage);
        return win;
    }

    public void ApplyCurrentTheme()
    {
        ThemeHelper.ApplyTheme(this, _settings, preferAcrylic: true,
            useBackdrop: _settings.GetString("result-popup-appearance") != "solid");
        if (_session?.ShowChanges == true)
            RenderDiff();
        if (_session?.Discussion.Count > 0)
            RenderDiscussionTranscript();
    }

    private void ShowAskInputMode(string providerBadge, bool hasSelection)
    {
        _isStandaloneError = false;
        ActionIconHost.Content = ActionIcons.Create("ask", 17);
        ActionTitleText.Text = "Ask AI";
        ProviderBadgeText.Text = providerBadge;
        ToolTipService.SetToolTip(ProviderBadgeText, providerBadge);
        ProviderBadgeText.Visibility = Visibility.Visible;
        PinButton.Visibility = Visibility.Collapsed;

        IncludeSelectionBox.IsChecked = hasSelection;
        IncludeSelectionBox.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        AskInputPanel.Visibility = Visibility.Visible;
        WorkingPanel.Visibility = Visibility.Collapsed;
        ResultBodyPanel.Visibility = Visibility.Collapsed;
        DiscussionPane.Visibility = Visibility.Collapsed;
        FollowUpRow.Visibility = Visibility.Collapsed;
        ResultFooterRow.Visibility = Visibility.Collapsed;
        ErrorFooterRow.Visibility = Visibility.Collapsed;
        ReplacementWarningBar.IsOpen = false;
        StatusInfoBar.IsOpen = false;

        PositionPopup(initial: true);
    }

    private void ShowWorkingMode(string title, string statusMessage)
    {
        _isStandaloneError = false;
        ActionTitleText.Text = title;
        WorkingStatusText.Text = statusMessage;
        WorkingSpinner.IsActive = true;

        AskInputPanel.Visibility = Visibility.Collapsed;
        WorkingPanel.Visibility = Visibility.Visible;
        ResultBodyPanel.Visibility = Visibility.Collapsed;
        DiscussionPane.Visibility = Visibility.Collapsed;
        FollowUpRow.Visibility = Visibility.Collapsed;
        ResultFooterRow.Visibility = Visibility.Collapsed;
        ErrorFooterRow.Visibility = Visibility.Collapsed;
        StatusInfoBar.IsOpen = false;

        PositionPopup(initial: false);
    }

    public void ShowErrorMode(string message)
    {
        _isStandaloneError = true;
        _standaloneErrorText = message;
        WorkingSpinner.IsActive = false;

        ActionIconHost.Content = ActionIcons.CreateGlyph("\uEA39", 17);
        ActionTitleText.Text = "Couldn’t complete action";
        ProviderBadgeText.Visibility = Visibility.Collapsed;
        PinButton.Visibility = Visibility.Collapsed;

        AskInputPanel.Visibility = Visibility.Collapsed;
        WorkingPanel.Visibility = Visibility.Collapsed;
        ResultBodyPanel.Visibility = Visibility.Collapsed;
        DiscussionPane.Visibility = Visibility.Collapsed;
        FollowUpRow.Visibility = Visibility.Collapsed;
        ResultFooterRow.Visibility = Visibility.Collapsed;
        ReplacementWarningBar.IsOpen = false;

        StatusInfoBar.Severity = InfoBarSeverity.Error;
        StatusInfoBar.Title = "";
        StatusInfoBar.Message = message;
        StatusInfoBar.IsClosable = false;
        StatusInfoBar.IsOpen = true;

        var showSettings = _onOpenSettings != null &&
            (message.Contains("API key", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("provider", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("Settings", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("sign in", StringComparison.OrdinalIgnoreCase));
        ErrorOpenSettingsButton.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        ErrorFooterRow.Visibility = Visibility.Visible;

        PositionPopup(initial: true);
    }

    public void BindSession(ResultSession session, bool initialResize = false)
    {
        _session = session;
        _pinned = session.Pinned;
        _isStandaloneError = false;
        WorkingSpinner.IsActive = false;

        ActionIconHost.Content = ActionIcons.Create(session.ActionIcon, 17);
        ActionTitleText.Text = session.ActionTitle;
        ProviderBadgeText.Text = session.ProviderBadge;
        ToolTipService.SetToolTip(ProviderBadgeText, session.ProviderBadge);
        ProviderBadgeText.Visibility = Visibility.Visible;
        PinButton.Visibility = Visibility.Visible;
        PinButton.IsChecked = _pinned;

        AskInputPanel.Visibility = Visibility.Collapsed;
        WorkingPanel.Visibility = Visibility.Collapsed;
        ErrorFooterRow.Visibility = Visibility.Collapsed;
        ResultBodyPanel.Visibility = Visibility.Visible;
        ResultFooterRow.Visibility = Visibility.Visible;
        StatusInfoBar.IsClosable = true;
        StatusInfoBar.IsOpen = false;

        ChangesToggle.Visibility = session.OriginalText != null ? Visibility.Visible : Visibility.Collapsed;
        if (session.Refinement != null)
        {
            RefineToggle.Visibility = Visibility.Visible;
            DiscussToggle.Visibility = Visibility.Visible;
        }
        else
        {
            RefineToggle.Visibility = Visibility.Collapsed;
            DiscussToggle.Visibility = Visibility.Collapsed;
        }

        if (!session.CanReplace)
        {
            CopyIconButton.Visibility = Visibility.Collapsed;
            PrimaryActionButton.Content = "Copy";
            if (!string.IsNullOrWhiteSpace(session.ReplaceDisabledReason))
            {
                ReplacementWarningBar.Message = session.ReplaceDisabledReason;
                ReplacementWarningBar.IsOpen = true;
            }
        }
        else
        {
            CopyIconButton.Visibility = Visibility.Visible;
            PrimaryActionButton.Content = "Replace";
            ReplacementWarningBar.IsOpen = false;
        }

        _updatingText = true;
        ResultEditorBox.Text = session.CurrentText;
        _updatingText = false;

        ChangesToggle.IsChecked = session.ShowChanges && session.HasOriginalDiff;
        ApplyChangesView();
        UpdateRevisionHeader();

        if (session.Discussion.Count > 0)
            RenderDiscussionTranscript();
        else
            DiscussionPane.Visibility = Visibility.Collapsed;

        PositionPopup(initial: initialResize);
    }

    private void BeginClose()
    {
        if (_isClosing) return;
        _isClosing = true;
        Close();
    }

    private void SetEnlargedState(bool enlarged, bool reposition = true)
    {
        _enlarged = enlarged;
        ExpandIcon.Glyph = enlarged ? "\uE73F" : "\uE740";
        ToolTipService.SetToolTip(ExpandButton, enlarged ? "Compact popup" : "Enlarge popup");
        AutomationProperties.SetName(ExpandButton, enlarged ? "Compact popup" : "Enlarge popup");

        ResultEditorBox.MaxHeight = enlarged ? 360 : 180;
        DiffContainer.MaxHeight = enlarged ? 360 : 180;
        DiscussionPane.MaxHeight = enlarged ? 260 : 160;

        if (_session?.Discussion.Count > 0)
            RenderDiscussionTranscript();

        if (reposition)
            PositionPopup(initial: false);
    }

    private void PositionPopup(bool initial)
    {
        if (AppWindow == null || _isClosing) return;
        var scale = _chrome.GetDpiScale();
        var refPoint = initial
            ? _anchorPoint
            : new WinPoint(AppWindow.Position.X, AppWindow.Position.Y);
        NativeMethods.GetWorkAreaMonitorIndex(refPoint, out var work);

        var workWidthDip = Math.Max(360.0, (work.Right - work.Left) / scale);
        var workHeightDip = Math.Max(240.0, (work.Bottom - work.Top) / scale);
        var widthDip = _isStandaloneError
            ? 390.0
            : (_enlarged ? Math.Min(680.0, workWidthDip - 32.0) : 416.0);
        var maxHeightDip = _enlarged ? Math.Min(740.0, workHeightDip - 32.0) : 560.0;

        SurfaceBorder.Width = widthDip;
        SurfaceBorder.UpdateLayout();
        var desired = FloatingSurfaceHelper.MeasureDip(SurfaceBorder, widthDip, maxHeightDip);

        var physW = Math.Max(300, (int)Math.Ceiling(widthDip * scale));
        var physH = Math.Max(108, (int)Math.Ceiling(Math.Min(maxHeightDip, desired.Height + 6) * scale));

        int x, y;
        if (initial)
        {
            (x, y) = FloatingSurfaceHelper.PositionNearAnchor(_anchorPoint, physW, physH, preferAbove: false, gapPx: 10);
        }
        else
        {
            var pos = AppWindow.Position;
            NativeMethods.GetWorkAreaMonitorIndex(new WinPoint(pos.X, pos.Y), out var curWork);
            x = Math.Clamp(pos.X, curWork.Left + 8, Math.Max(curWork.Left + 8, curWork.Right - physW - 8));
            y = Math.Clamp(pos.Y, curWork.Top + 8, Math.Max(curWork.Top + 8, curWork.Bottom - physH - 8));
        }

        _chrome.UpdateRegionForWin10(physW, physH);
        AppWindow.MoveAndResize(new RectInt32(x, y, physW, physH));
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CancelAndClose();
        }
    }

    public void DismissOnOutsideClick(WinPoint point)
    {
        if (_isClosing || _pinned || !_settings.GetBoolean("result-popup-dismiss-on-outside-click"))
            return;
        if (!NativeMethods.GetWindowRect(_chrome.Hwnd, out var bounds))
            return;
        if (point.X < bounds.Left || point.X >= bounds.Right || point.Y < bounds.Top || point.Y >= bounds.Bottom)
            CancelAndClose();
    }

    private void CancelAndClose()
    {
        _session?.CompleteCancel();
        BeginClose();
    }

    private void AskPromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(AskPromptBox.Text))
            StatusInfoBar.IsOpen = false;
        PositionPopup(initial: false);
    }

    private async void AskPromptBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            var shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            if (!shift)
            {
                e.Handled = true;
                await SubmitAskAsync();
            }
        }
    }

    private async void AskSendButton_Click(object sender, RoutedEventArgs e) => await SubmitAskAsync();

    private async Task SubmitAskAsync()
    {
        if (_onAskSubmit == null) return;
        var instruction = AskPromptBox.Text.Trim();
        if (string.IsNullOrEmpty(instruction))
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Message = "Enter a question or request.";
            StatusInfoBar.IsClosable = false;
            StatusInfoBar.IsOpen = true;
            PositionPopup(initial: false);
            AskPromptBox.Focus(FocusState.Programmatic);
            return;
        }

        ShowWorkingMode("Ask AI", $"Asking {ProviderBadgeText.Text}…");
        try
        {
            var nextSession = await _onAskSubmit(instruction, IncludeSelectionBox.IsChecked == true);
            if (_isClosing)
            {
                nextSession?.Dispose();
                return;
            }
            if (nextSession != null)
            {
                BindSession(nextSession, initialResize: false);
            }
        }
        catch (OperationCanceledException)
        {
            BeginClose();
        }
        catch (Exception ex)
        {
            if (!_isClosing)
                ShowErrorMode(ex.Message);
        }
    }

    private void ResultEditorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingText || _session == null) return;
        _session.CurrentText = ResultEditorBox.Text;

        if (_session.OriginalText != null)
        {
            var hasDiff = _session.HasOriginalDiff;
            ChangesToggle.IsEnabled = hasDiff && !_refining;
            if (!hasDiff && _session.ShowChanges)
            {
                _session.ShowChanges = false;
                ChangesToggle.IsChecked = false;
                ApplyChangesView();
            }
        }
        PositionPopup(initial: false);
    }

    private void ChangesToggle_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_session == null || ChangesToggle == null || ResultEditorBox == null || DiffContainer == null)
            return;
        _session.ShowChanges = ChangesToggle.IsChecked == true;
        ApplyChangesView();
        PositionPopup(initial: false);
    }

    private void ApplyChangesView()
    {
        if (_session?.OriginalText == null)
            return;

        var hasDiff = _session.HasOriginalDiff;
        ChangesToggle.IsEnabled = hasDiff && !_refining;
        if (!hasDiff && _session.ShowChanges)
        {
            _session.ShowChanges = false;
            ChangesToggle.IsChecked = false;
        }

        if (_session.ShowChanges)
        {
            RenderDiff();
            ResultEditorBox.Visibility = Visibility.Collapsed;
            DiffContainer.Visibility = Visibility.Visible;
        }
        else
        {
            DiffContainer.Visibility = Visibility.Collapsed;
            ResultEditorBox.Visibility = Visibility.Visible;
        }
    }

    private void RenderDiff()
    {
        DiffRichTextBlock.Blocks.Clear();
        if (_session?.OriginalText == null)
            return;

        var paragraph = new Paragraph();
        var addedFg = ThemeHelper.GetThemeBrush(RootGrid, "DiffAddedForegroundBrush");
        var removedFg = ThemeHelper.GetThemeBrush(RootGrid, "DiffRemovedForegroundBrush");

        foreach (var part in DiffEngine.DiffText(_session.OriginalText, _session.CurrentText))
        {
            var run = new Run { Text = part.Text };
            if (part.Type == "added")
            {
                if (addedFg != null) run.Foreground = addedFg;
                run.FontWeight = FontWeights.SemiBold;
                run.TextDecorations = TextDecorations.Underline;
            }
            else if (part.Type == "removed")
            {
                if (removedFg != null) run.Foreground = removedFg;
                run.TextDecorations = TextDecorations.Strikethrough;
            }
            paragraph.Inlines.Add(run);
        }

        DiffRichTextBlock.Blocks.Add(paragraph);
    }

    private void UpdateRevisionHeader()
    {
        if (_session == null) return;
        var count = _session.Revisions.Count;
        var visible = count > 1;
        RevisionCountLabel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PrevButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        RevisionCountLabel.Text = $"{_session.RevisionIndex + 1}/{count}";
        PrevButton.IsEnabled = _session.RevisionIndex > 0 && !_refining;
        NextButton.IsEnabled = _session.RevisionIndex < count - 1 && !_refining;
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => SwitchRevision(-1);
    private void NextButton_Click(object sender, RoutedEventArgs e) => SwitchRevision(1);

    private void SwitchRevision(int delta)
    {
        if (_session == null) return;
        _session.CurrentText = ResultEditorBox.Text;
        var nextIndex = Math.Clamp(_session.RevisionIndex + delta, 0, _session.Revisions.Count - 1);
        if (nextIndex == _session.RevisionIndex)
            return;

        _session.RevisionIndex = nextIndex;
        _updatingText = true;
        ResultEditorBox.Text = _session.CurrentText;
        _updatingText = false;

        UpdateRevisionHeader();
        ApplyChangesView();
        PositionPopup(initial: false);
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _pinned = PinButton.IsChecked == true;
        if (_session != null)
            _session.Pinned = _pinned;
    }

    private void RefineToggle_Click(object sender, RoutedEventArgs e) =>
        OpenFollowUp(discuss: false, open: RefineToggle.IsChecked == true);

    private void DiscussToggle_Click(object sender, RoutedEventArgs e) =>
        OpenFollowUp(discuss: true, open: DiscussToggle.IsChecked == true);

    private void OpenFollowUp(bool discuss, bool open)
    {
        if (_session == null) return;
        SaveCurrentFollowUpDraft();

        if (!open)
        {
            RefineToggle.IsChecked = false;
            DiscussToggle.IsChecked = false;
            FollowUpRow.Visibility = Visibility.Collapsed;
            DiscussionPane.Visibility = _session.Discussion.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PositionPopup(initial: false);
            return;
        }

        _discussing = discuss;
        RefineToggle.IsChecked = !discuss;
        DiscussToggle.IsChecked = discuss;
        FollowUpRow.Visibility = Visibility.Visible;
        FollowUpInputBox.PlaceholderText = discuss
            ? "Ask a follow-up question…"
            : "Refine (e.g. shorter, more formal)…";
        FollowUpInputBox.Text = _session.FollowUpDrafts[discuss ? "discuss" : "refine"];
        DiscussionPane.Visibility = _session.Discussion.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PositionPopup(initial: false);
        FollowUpInputBox.Focus(FocusState.Programmatic);
    }

    private void SaveCurrentFollowUpDraft()
    {
        if (_session == null || FollowUpRow.Visibility != Visibility.Visible)
            return;
        _session.FollowUpDrafts[_discussing ? "discuss" : "refine"] = FollowUpInputBox.Text;
    }

    private void RenderDiscussionTranscript()
    {
        if (_session == null) return;
        DiscussionStack.Children.Clear();
        var userBg = ThemeHelper.GetThemeBrush(RootGrid, "UserBubbleBackgroundBrush");
        var assistantBg = ThemeHelper.GetThemeBrush(RootGrid, "AssistantBubbleBackgroundBrush");
        var secondaryBrush = ThemeHelper.GetThemeBrush(RootGrid, "TextFillColorSecondaryBrush");
        var monoFamily = new FontFamily("Consolas");

        foreach (var turn in _session.Discussion)
        {
            var isUser = turn.Role == "user";
            var card = new Border
            {
                Background = isUser ? userBg : assistantBg,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 6, 9, 7),
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = _enlarged ? 540 : 340
            };

            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new TextBlock
            {
                Text = isUser ? "You" : "AI",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = secondaryBrush
            });

            var rtb = new RichTextBlock
            {
                IsTextSelectionEnabled = true,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap
            };
            var para = new Paragraph();
            foreach (var span in MarkdownParser.ParseToSpans(turn.Content))
            {
                var run = new Run { Text = span.Text };
                if (span.Bold) run.FontWeight = FontWeights.Bold;
                if (span.Italic) run.FontStyle = FontStyle.Italic;
                if (span.Strikethrough) run.TextDecorations = TextDecorations.Strikethrough;
                if (span.Monospace) run.FontFamily = monoFamily;
                para.Inlines.Add(run);
            }
            rtb.Blocks.Add(para);
            stack.Children.Add(rtb);
            card.Child = stack;
            DiscussionStack.Children.Add(card);
        }

        DiscussionPane.Visibility = _session.Discussion.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DiscussionScrollViewer.UpdateLayout();
        DiscussionScrollViewer.ChangeView(null, DiscussionScrollViewer.ScrollableHeight, null);
    }

    private async void FollowUpInputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await SubmitFollowUpAsync();
        }
    }

    private async void FollowUpSendButton_Click(object sender, RoutedEventArgs e) => await SubmitFollowUpAsync();

    private async Task SubmitFollowUpAsync()
    {
        if (_refining || _session?.Refinement == null)
            return;

        var instruction = FollowUpInputBox.Text.Trim();
        if (string.IsNullOrEmpty(instruction))
            return;

        var wasDiscussing = _discussing;
        _session.CurrentText = ResultEditorBox.Text;
        StatusInfoBar.IsOpen = false;
        SetBusy(true);

        try
        {
            if (wasDiscussing)
            {
                var pendingUser = new DiscussionTurn
                {
                    Role = "user",
                    Content = instruction,
                    CurrentResult = _session.CurrentText,
                    Revision = _session.RevisionIndex + 1
                };
                var history = new List<DiscussionTurn>(_session.Discussion) { pendingUser };
                var reply = await _session.Refinement.DiscussAsync(history);
                if (_isClosing) return;

                _session.Discussion.Add(pendingUser);
                _session.Discussion.Add(new DiscussionTurn { Role = "assistant", Content = reply });
                RenderDiscussionTranscript();

                _session.FollowUpDrafts["discuss"] = "";
                FollowUpInputBox.Text = "";
            }
            else
            {
                var updated = (await _session.Refinement.RunAsync(_session.CurrentText, instruction)).Replace("\r\n", "\n");
                if (_isClosing) return;
                _session.Revisions.Add(updated);
                _session.RevisionIndex = _session.Revisions.Count - 1;

                _updatingText = true;
                ResultEditorBox.Text = updated;
                _updatingText = false;

                _session.FollowUpDrafts["refine"] = "";
                FollowUpInputBox.Text = "";
                UpdateRevisionHeader();
                ApplyChangesView();
            }
        }
        catch (Exception ex)
        {
            if (_isClosing) return;
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Message = ex.Message;
            StatusInfoBar.IsOpen = true;
        }
        finally
        {
            if (!_isClosing)
            {
                SetBusy(false);
                PositionPopup(initial: false);
            }
        }
    }

    private void SetBusy(bool busy)
    {
        _refining = busy;
        FollowUpInputBox.IsEnabled = !busy;
        FollowUpSendButton.IsEnabled = !busy;
        FollowUpSendIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        FollowUpSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        FollowUpSpinner.IsActive = busy;
        RefineToggle.IsEnabled = !busy;
        DiscussToggle.IsEnabled = !busy;
        PrimaryActionButton.IsEnabled = !busy;
        CopyIconButton.IsEnabled = !busy;
        ResultEditorBox.IsReadOnly = busy;
        UpdateRevisionHeader();
        ApplyChangesView();
    }

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        SetEnlargedState(!_enlarged, reposition: true);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CancelAndClose();

    private void ErrorCopyButton_Click(object sender, RoutedEventArgs e)
    {
        _onCopyError?.Invoke(_standaloneErrorText);
        ErrorCopyButton.Content = "Copied";
    }

    private void ErrorOpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _onOpenSettings?.Invoke();
        BeginClose();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        _session.CurrentText = ResultEditorBox.Text;
        _session.CompleteCopy(_session.CurrentText);
        BeginClose();
    }

    private void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        _session.CurrentText = ResultEditorBox.Text;
        if (_session.CanReplace)
            _session.CompleteReplace(_session.CurrentText);
        else
            _session.CompleteCopy(_session.CurrentText);
        BeginClose();
    }
}
