using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Plyph.Core;
using Plyph.Windows.Interop;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Text;
using WinRT.Interop;

namespace Plyph.Windows.UI;

public sealed partial class ResultWindow : Window
{
    private readonly ResultSession _session;
    private readonly Action<ResultSession>? _onSwitchToCompact;

    private bool _refining;
    private bool _expanded;
    private bool _discussing;
    private bool _updatingText;
    private bool _transferring;

    public ResultWindow(ResultSession session, Action<ResultSession>? onSwitchToCompact = null)
    {
        _session = session;
        _onSwitchToCompact = onSwitchToCompact;

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        BrandLogoHost.Content = FloatingSurfaceHelper.CreatePlyphMark(14);
        TitleBarTitleText.Text = _session.ActionTitle;
        ProviderBadgeText.Text = _session.ProviderBadge;

        if (_onSwitchToCompact == null)
            CompactButton.Visibility = Visibility.Collapsed;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Plyph.ico");
        ThemeHelper.ConfigureWindow(this, _session.Settings, 760, 500, iconPath);

        _updatingText = true;
        ResultEditorBox.Text = _session.CurrentText;
        SideBySideResultBox.Text = _session.CurrentText;
        if (_session.OriginalText != null)
            OriginalReadOnlyBox.Text = _session.OriginalText.Replace("\r\n", "\n");
        _updatingText = false;

        WrapButton.IsChecked = _session.WrapText;
        PinButton.IsChecked = _session.Pinned;

        if (_session.OriginalText != null)
        {
            ChangesButton.Visibility = Visibility.Visible;
            SideBySideButton.Visibility = Visibility.Visible;
        }

        if (_session.Refinement != null)
        {
            RefineToggle.Visibility = Visibility.Visible;
            DiscussToggle.Visibility = Visibility.Visible;
        }

        if (!_session.CanReplace)
        {
            ReplaceButton.IsEnabled = false;
            ReplaceButton.Style = Application.Current.Resources["DefaultButtonStyle"] as Style;
            CopyButton.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
            if (!string.IsNullOrWhiteSpace(_session.ReplaceDisabledReason))
            {
                ReplacementWarningBar.Message = _session.ReplaceDisabledReason;
                ReplacementWarningBar.IsOpen = true;
            }
        }

        ChangesButton.IsChecked = _session.ShowChanges && !_session.SideBySideCompare && _session.HasOriginalDiff;
        SideBySideButton.IsChecked = _session.SideBySideCompare && _session.OriginalText != null;
        ApplyComparisonView();
        UpdateRevisionHeader();

        if (_session.Discussion.Count > 0)
            RenderDiscussionTranscript();

        if (_session.OpenDiscussionOnExpand && _session.Refinement != null)
        {
            _session.OpenDiscussionOnExpand = false;
            OpenFollowUp(discuss: true, open: true);
        }

        RootGrid.ActualThemeChanged += (_, _) =>
        {
            if (_session.ShowChanges)
                RenderDiff();
            if (_session.Discussion.Count > 0)
                RenderDiscussionTranscript();
        };

        Closed += (_, _) =>
        {
            if (!_transferring)
            {
                _session.Dispose();
                _session.CompleteClose();
            }
        };
    }

    public void ApplyCurrentTheme()
    {
        ThemeHelper.ApplyTheme(this, _session.Settings);
        if (_session.ShowChanges)
            RenderDiff();
        if (_session.Discussion.Count > 0)
            RenderDiscussionTranscript();
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            _session.CompleteCancel();
            Close();
        }
    }

    private void ResultEditorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingText) return;
        SyncEditedText(ResultEditorBox.Text, fromSideBySide: false);
    }

    private void SideBySideResultBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingText) return;
        SyncEditedText(SideBySideResultBox.Text, fromSideBySide: true);
    }

    private void SyncEditedText(string newText, bool fromSideBySide)
    {
        _session.CurrentText = newText;
        _updatingText = true;
        if (fromSideBySide)
            ResultEditorBox.Text = _session.CurrentText;
        else
            SideBySideResultBox.Text = _session.CurrentText;
        _updatingText = false;

        if (_session.OriginalText != null)
        {
            var hasDiff = _session.HasOriginalDiff;
            ChangesButton.IsEnabled = hasDiff && !_refining;
            if (_session.ShowChanges && !_session.SideBySideCompare)
            {
                if (!hasDiff)
                {
                    _session.ShowChanges = false;
                    ChangesButton.IsChecked = false;
                    ApplyComparisonView();
                }
                else
                {
                    RenderDiff();
                }
            }
        }
    }

    private void ChangesButton_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (ChangesButton == null || ResultEditorBox == null || DiffContainer == null)
            return;
        _session.ShowChanges = ChangesButton.IsChecked == true;
        if (_session.ShowChanges && _session.SideBySideCompare)
        {
            _session.SideBySideCompare = false;
            SideBySideButton.IsChecked = false;
        }
        ApplyComparisonView();
    }

    private void SideBySideButton_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (SideBySideButton == null || SideBySideGrid == null)
            return;
        _session.SideBySideCompare = SideBySideButton.IsChecked == true;
        if (_session.SideBySideCompare && _session.ShowChanges)
        {
            _session.ShowChanges = false;
            ChangesButton.IsChecked = false;
        }
        ApplyComparisonView();
    }

    private void ApplyComparisonView()
    {
        if (_session.OriginalText == null)
        {
            DiffContainer.Visibility = Visibility.Collapsed;
            SideBySideGrid.Visibility = Visibility.Collapsed;
            ResultEditorBox.Visibility = Visibility.Visible;
            return;
        }

        var hasDiff = _session.HasOriginalDiff;
        ChangesButton.IsEnabled = hasDiff && !_refining;
        if (!hasDiff && _session.ShowChanges)
        {
            _session.ShowChanges = false;
            ChangesButton.IsChecked = false;
        }

        if (_session.SideBySideCompare)
        {
            ResultEditorBox.Visibility = Visibility.Collapsed;
            DiffContainer.Visibility = Visibility.Collapsed;
            SideBySideGrid.Visibility = Visibility.Visible;
        }
        else if (_session.ShowChanges)
        {
            RenderDiff();
            ResultEditorBox.Visibility = Visibility.Collapsed;
            SideBySideGrid.Visibility = Visibility.Collapsed;
            DiffContainer.Visibility = Visibility.Visible;
        }
        else
        {
            DiffContainer.Visibility = Visibility.Collapsed;
            SideBySideGrid.Visibility = Visibility.Collapsed;
            ResultEditorBox.Visibility = Visibility.Visible;
        }
    }

    private void RenderDiff()
    {
        DiffRichTextBlock.Blocks.Clear();
        if (_session.OriginalText == null)
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

    private void WrapButton_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (WrapButton == null || ResultEditorBox == null || DiffRichTextBlock == null)
            return;
        var wrap = WrapButton.IsChecked == true;
        _session.WrapText = wrap;
        var mode = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ResultEditorBox.TextWrapping = mode;
        DiffRichTextBlock.TextWrapping = mode;
        OriginalReadOnlyBox.TextWrapping = mode;
        SideBySideResultBox.TextWrapping = mode;
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _session.Pinned = PinButton.IsChecked == true;
        try
        {
            if (AppWindow?.Presenter is OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = _session.Pinned;
        }
        catch
        {
        }
    }

    private void CompactButton_Click(object sender, RoutedEventArgs e)
    {
        if (_onSwitchToCompact == null) return;
        SaveCurrentFollowUpDraft();
        _transferring = true;
        _onSwitchToCompact(_session);
        Close();
    }

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        ToolTipService.SetToolTip(ExpandButton, _expanded ? "Default size" : "Enlarge window");
        ExpandIcon.Glyph = _expanded ? "\uE73F" : "\uE740";

        if (AppWindow == null)
            return;

        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = hwnd != IntPtr.Zero ? Math.Max(1.0, NativeMethods.GetDpiForWindow(hwnd) / 96.0) : 1.0;
        var pt = NativeMethods.GetCursorPos(out var c) ? new System.Drawing.Point(c.X, c.Y) : new System.Drawing.Point(400, 300);
        NativeMethods.GetWorkAreaMonitorIndex(pt, out var work);

        var targetW = _expanded ? (int)Math.Round((work.Right - work.Left) * 0.86) : (int)Math.Round(760 * scale);
        var targetH = _expanded ? (int)Math.Round((work.Bottom - work.Top) * 0.86) : (int)Math.Round(500 * scale);

        targetW = Math.Min(targetW, work.Right - work.Left - 24);
        targetH = Math.Min(targetH, work.Bottom - work.Top - 24);
        AppWindow.Resize(new SizeInt32(targetW, targetH));
        var cx = work.Left + Math.Max(0, (work.Right - work.Left - targetW) / 2);
        var cy = work.Top + Math.Max(0, (work.Bottom - work.Top - targetH) / 2);
        AppWindow.Move(new PointInt32(cx, cy));
    }

    private void UpdateRevisionHeader()
    {
        var count = _session.Revisions.Count;
        var visible = count > 1;
        RevisionCountLabel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PrevButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        RevisionCountLabel.Text = $"{_session.RevisionIndex + 1} of {count}";
        PrevButton.IsEnabled = _session.RevisionIndex > 0 && !_refining;
        NextButton.IsEnabled = _session.RevisionIndex < count - 1 && !_refining;
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => SwitchResult(-1);
    private void NextButton_Click(object sender, RoutedEventArgs e) => SwitchResult(1);

    private void SwitchResult(int delta)
    {
        var nextIndex = Math.Clamp(_session.RevisionIndex + delta, 0, _session.Revisions.Count - 1);
        if (nextIndex == _session.RevisionIndex)
            return;

        _session.RevisionIndex = nextIndex;
        _updatingText = true;
        ResultEditorBox.Text = _session.CurrentText;
        SideBySideResultBox.Text = _session.CurrentText;
        _updatingText = false;

        UpdateRevisionHeader();
        ApplyComparisonView();
    }

    private void RefineToggle_Click(object sender, RoutedEventArgs e)
    {
        var nextShow = RefineToggle.IsChecked == true;
        OpenFollowUp(discuss: false, open: nextShow);
    }

    private void DiscussToggle_Click(object sender, RoutedEventArgs e)
    {
        var nextShow = DiscussToggle.IsChecked == true;
        OpenFollowUp(discuss: true, open: nextShow);
    }

    private void OpenFollowUp(bool discuss, bool open)
    {
        SaveCurrentFollowUpDraft();

        if (!open)
        {
            RefineToggle.IsChecked = false;
            DiscussToggle.IsChecked = false;
            FollowUpRow.Visibility = Visibility.Collapsed;
            UpdateDiscussionVisibility();
            return;
        }

        _discussing = discuss;
        RefineToggle.IsChecked = !discuss;
        DiscussToggle.IsChecked = discuss;
        FollowUpRow.Visibility = Visibility.Visible;
        FollowUpInputBox.PlaceholderText = discuss
            ? "Ask a follow-up question about this result…"
            : "Refine this result…";
        FollowUpInputBox.Text = _session.FollowUpDrafts[discuss ? "discuss" : "refine"];
        UpdateDiscussionVisibility();
        FollowUpInputBox.Focus(FocusState.Programmatic);
    }

    private void SaveCurrentFollowUpDraft()
    {
        if (FollowUpRow.Visibility != Visibility.Visible)
            return;
        var key = _discussing ? "discuss" : "refine";
        _session.FollowUpDrafts[key] = FollowUpInputBox.Text;
    }

    private void UpdateDiscussionVisibility()
    {
        var visible = _discussing || _session.Discussion.Count > 0;
        DiscussionPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DiscussionRowDef.Height = visible
            ? new GridLength(0.75, GridUnitType.Star)
            : new GridLength(0, GridUnitType.Pixel);
    }

    private void RenderDiscussionTranscript()
    {
        DiscussionStack.Children.Clear();
        var userBg = ThemeHelper.GetThemeBrush(RootGrid, "UserBubbleBackgroundBrush");
        var assistantBg = ThemeHelper.GetThemeBrush(RootGrid, "AssistantBubbleBackgroundBrush");
        var borderBrush = ThemeHelper.GetThemeBrush(RootGrid, "CardStrokeColorDefaultBrush");
        var secondaryBrush = ThemeHelper.GetThemeBrush(RootGrid, "TextFillColorSecondaryBrush");
        var monoFamily = new FontFamily("Consolas");

        foreach (var turn in _session.Discussion)
        {
            var isUser = turn.Role == "user";
            var card = new Border
            {
                Background = isUser ? userBg : assistantBg,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 10),
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = 640
            };

            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(new TextBlock
            {
                Text = isUser ? "You" : "AI",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = secondaryBrush
            });

            var rtb = new RichTextBlock
            {
                IsTextSelectionEnabled = true,
                FontSize = 13.5,
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

        UpdateDiscussionVisibility();
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
        if (_refining || _session.Refinement == null)
            return;

        var instruction = FollowUpInputBox.Text.Trim();
        if (string.IsNullOrEmpty(instruction))
            return;

        var wasDiscussing = _discussing;
        var current = _session.CurrentText;

        StatusInfoBar.IsOpen = false;
        SetBusyState(true);

        try
        {
            if (wasDiscussing)
            {
                var pendingUser = new DiscussionTurn
                {
                    Role = "user",
                    Content = instruction,
                    CurrentResult = current,
                    Revision = _session.RevisionIndex + 1
                };
                var history = new List<DiscussionTurn>(_session.Discussion) { pendingUser };
                var reply = await _session.Refinement.DiscussAsync(history);

                _session.Discussion.Add(pendingUser);
                _session.Discussion.Add(new DiscussionTurn { Role = "assistant", Content = reply });
                RenderDiscussionTranscript();

                _session.FollowUpDrafts["discuss"] = "";
                FollowUpInputBox.Text = "";
            }
            else
            {
                var updated = (await _session.Refinement.RunAsync(current, instruction)).Replace("\r\n", "\n");
                _session.Revisions.Add(updated);
                _session.RevisionIndex = _session.Revisions.Count - 1;

                _updatingText = true;
                ResultEditorBox.Text = updated;
                SideBySideResultBox.Text = updated;
                _updatingText = false;

                UpdateRevisionHeader();
                ApplyComparisonView();

                _session.FollowUpDrafts["refine"] = "";
                FollowUpInputBox.Text = "";
            }
        }
        catch (Exception ex)
        {
            StatusInfoBar.Message = ex.Message;
            StatusInfoBar.IsOpen = true;
        }
        finally
        {
            SetBusyState(false);
        }
    }

    private void SetBusyState(bool busy)
    {
        _refining = busy;
        FollowUpInputBox.IsEnabled = !busy;
        FollowUpSendButton.IsEnabled = !busy;
        RefineToggle.IsEnabled = !busy;
        DiscussToggle.IsEnabled = !busy;
        ReplaceButton.IsEnabled = !busy && _session.CanReplace;
        CopyButton.IsEnabled = !busy;
        ResultEditorBox.IsReadOnly = busy;
        SideBySideResultBox.IsReadOnly = busy;
        UpdateRevisionHeader();
        ApplyComparisonView();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _session.CompleteCancel();
        Close();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        _session.CompleteCopy(_session.CurrentText);
        Close();
    }

    private void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.CanReplace) return;
        _session.CompleteReplace(_session.CurrentText);
        Close();
    }
}
