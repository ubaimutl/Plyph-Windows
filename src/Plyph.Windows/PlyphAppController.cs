using System.Drawing;
using System.Text;
using Microsoft.Win32;
using Plyph.Core;
using Plyph.Windows.Interop;
using Plyph.Windows.Overlays;
using Plyph.Windows.Selection;
using Plyph.Windows.Services;
using Plyph.Windows.Shortcuts;
using Plyph.Windows.UI;

namespace Plyph.Windows;

public sealed class PlyphAppController : IDisposable
{
    private readonly IPlyphSettings _settings;
    private readonly ISecretBackend _secretBackend;
    private readonly AiClient _aiClient;
    private readonly SelectionCaptureService _captureService;
    private readonly TextReplacer _textReplacer;
    private readonly GlobalHotkeyManager _hotkeyManager;
    private readonly WindowsSelectionMonitor _selectionMonitor;
    private readonly KeepAliveWindow _keepAliveWindow;

    private readonly FloatingToolbarWindow _floatingToolbar;
    private readonly FeedbackToastOverlay _feedbackOverlay;
    private readonly TrayIconManager _trayIcon;

    private ActionPaletteWindow? _actionPalette;
    private SettingsWindow? _settingsWindow;
    private CompactResultWindow? _compactResultWindow;
    private ResultWindow? _resultWindow;

    private IntPtr _floatingAnchorWindow;
    private Point _floatingAnchorPoint;
    private string _floatingCachedText = "";
    private bool _runningRequest;
    private int _trayStatusSerial;
    private string _trayState = "default";
    private CancellationTokenSource? _activeRequestCts;

    public PlyphAppController(IPlyphSettings settings, ISecretBackend secretBackend, bool openSettingsOnStart)
    {
        _settings = settings;
        _secretBackend = secretBackend;
        _aiClient = new AiClient(settings, secretBackend)
        {
            DiagnosticLogger = msg => DiagnosticLogStore.Append(msg)
        };
        _captureService = new SelectionCaptureService(settings);
        _textReplacer = new TextReplacer();
        _textReplacer.UndoStateChanged += (_, _) => { };

        _keepAliveWindow = new KeepAliveWindow();
        _feedbackOverlay = new FeedbackToastOverlay(settings);

        _floatingToolbar = new FloatingToolbarWindow(
            settings,
            () => ActionsStore.GetAvailableActions(_settings),
            ActivateToolbarAction,
            suppress =>
            {
                _floatingAnchorWindow = IntPtr.Zero;
                _floatingCachedText = "";
                _selectionMonitor?.NotifyHidden(suppress);
            });

        _hotkeyManager = new GlobalHotkeyManager(settings);
        _selectionMonitor = new WindowsSelectionMonitor(
            settings,
            isRequestRunning: () => _runningRequest,
            isPointInsideFloatingToolbar: pt =>
                _floatingToolbar.IsScreenPointInside(pt) ||
                (_actionPalette != null && _actionPalette.IsShown && _actionPalette.IsScreenPointInside(pt)));

        _selectionMonitor.SelectionDetected += OnSelectionDetected;
        _selectionMonitor.MousePressed += (_, point) => _compactResultWindow?.DismissOnOutsideClick(point);
        _selectionMonitor.DismissRequested += (_, suppress) =>
        {
            if (_actionPalette != null && _actionPalette.IsShown)
                _actionPalette.Dismiss();
            if (_floatingToolbar.IsShown)
                _floatingToolbar.Hide(suppress);
        };

        _trayIcon = new TrayIconManager(
            onOpenActions: OpenStandaloneActionsPalette,
            onAsk: () => _ = TriggerActionByIdAsync("ask", fromFloatingToolbar: false),
            onUndo: () => _ = UndoLastReplacementAsync(),
            onSettings: OpenSettings,
            onExit: ExitApplication,
            canUndo: () => _textReplacer.CanUndo);

        if (OperatingSystem.IsWindows())
            SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;

        RebindHotkeys();

        if (openSettingsOnStart)
            OpenSettings();
    }

    private void OnSystemUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Accessibility or UserPreferenceCategory.Color)
        {
            _keepAliveWindow.DispatcherQueue.TryEnqueue(ApplyThemeToOpenWindows);
        }
    }

    private void RebindHotkeys()
    {
        _hotkeyManager.BindShortcuts(
            onAsk: () => _ = TriggerActionByIdAsync("ask", fromFloatingToolbar: false),
            onCorrect: () => _ = TriggerActionByIdAsync("correct", fromFloatingToolbar: false),
            onRewrite: () => _ = TriggerActionByIdAsync("rewrite", fromFloatingToolbar: false),
            onOpenActions: OpenStandaloneActionsPalette);
    }

    private void OnSelectionDetected(object? sender, SelectionDetectedEventArgs e)
    {
        _floatingAnchorWindow = e.SourceWindow;
        _floatingAnchorPoint = e.AnchorPoint;
        _floatingCachedText = e.Text;
        _floatingToolbar.ShowAt(e.AnchorPoint, e.SelectionBounds);
    }

    private void CloseStandalonePalette()
    {
        if (_actionPalette != null && _actionPalette.IsShown)
            _actionPalette.Dismiss();
        _actionPalette = null;
    }

    private (IntPtr TargetWindow, string? CachedText) ResolveTargetWindowAndCachedText()
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || WindowsSelectionMonitor.IsShellOrOwnWindow(fg))
        {
            var lastWin = _selectionMonitor.LastRealAppWindow;
            var cached = _selectionMonitor.GetRecentCachedTextFor(lastWin) ?? _selectionMonitor.LastSelectionText;
            return (lastWin, cached);
        }
        return (fg, _selectionMonitor.GetRecentCachedTextFor(fg) ?? _selectionMonitor.LastSelectionText);
    }

    private void OpenStandaloneActionsPalette()
    {
        CloseStandalonePalette();
        _floatingToolbar.Hide(suppressSelection: false);
        var (focusedWindow, cached) = ResolveTargetWindowAndCachedText();
        var posSetting = _settings.GetString("action-palette-position");
        var nearPointer = !string.Equals(posSetting, "monitor-center", StringComparison.OrdinalIgnoreCase);

        var anchor = NativeMethods.GetCursorPos(out var pt) ? new Point(pt.X, pt.Y) : new Point(400, 300);
        var actions = ActionsStore.GetAvailableActions(_settings);
        var palette = new ActionPaletteWindow(
            _settings,
            actions,
            onActivate: action => _ = ExecuteActionAsync(action, focusedWindow, cached, fromFloatingToolbar: false, anchor),
            onClose: () => _actionPalette = null,
            anchorScreenRect: null,
            nearPointer: nearPointer);

        _actionPalette = palette;
    }

    private void ActivateToolbarAction(PlyphAction action)
    {
        CloseStandalonePalette();
        var win = _floatingAnchorWindow != IntPtr.Zero ? _floatingAnchorWindow : _selectionMonitor.FloatingSourceWindow;
        var cached = _floatingCachedText;
        var anchor = _floatingAnchorPoint;
        _ = ExecuteActionAsync(action, win, cached, fromFloatingToolbar: true, anchor);
    }

    private async Task TriggerActionByIdAsync(string modeOrId, bool fromFloatingToolbar)
    {
        var (focusedWindow, cached) = ResolveTargetWindowAndCachedText();
        var action = ActionsStore.GetAvailableActions(_settings)
            .FirstOrDefault(a => a.Id == modeOrId || a.Mode == modeOrId);
        if (action == null)
            return;

        var anchor = NativeMethods.GetCursorPos(out var pt) ? new Point(pt.X + 8, pt.Y + 12) : new Point(400, 300);
        await ExecuteActionAsync(action, focusedWindow, cached, fromFloatingToolbar, anchor);
    }

    private TransformOptions BuildTransformOptions(PlyphAction action)
    {
        if (action.Mode == "custom")
        {
            return new TransformOptions
            {
                Provider = action.Provider,
                Model = action.Model,
                InputMode = action.InputMode,
                UsePersonalPreferences = action.UsePersonalPreferences,
                InputLimit = action.InputLimit,
                OutputLimit = action.OutputLimit
            };
        }

        if (action.Mode == "prompt")
        {
            return new TransformOptions
            {
                Provider = _settings.GetString("prompt-run-provider"),
                Model = _settings.GetString("prompt-run-model"),
                InputMode = "prompt",
                InputLimit = _settings.GetInt64("prompt-run-input-limit"),
                OutputLimit = _settings.GetInt64("prompt-run-output-limit")
            };
        }

        return new TransformOptions();
    }

    private async Task ExecuteActionAsync(
        PlyphAction action,
        IntPtr sourceWindow,
        string? cachedText,
        bool fromFloatingToolbar,
        Point? anchorPoint = null)
    {
        if (_runningRequest)
            return;

        var effectiveAnchor = anchorPoint ??
                              (NativeMethods.GetCursorPos(out var pt) ? new Point(pt.X + 8, pt.Y + 12) : new Point(400, 300));

        if (!fromFloatingToolbar)
            _floatingToolbar.Hide(suppressSelection: false);

        CloseModalResultWindows();

        if (action.Mode == "ask")
        {
            await ShowInlineAskPopupAsync(action, sourceWindow, cachedText, fromFloatingToolbar, effectiveAnchor);
            return;
        }

        await RunAiWorkflowAsync(action, sourceWindow, cachedText, fromFloatingToolbar, effectiveAnchor);
    }

    private async Task ShowInlineAskPopupAsync(
        PlyphAction action, IntPtr sourceWindow, string? cachedText, bool fromFloatingToolbar, Point anchorPoint)
    {
        _floatingToolbar.Hide(suppressSelection: false);

        CapturedSelection selection;
        _runningRequest = true;
        try
        {
            selection = await _captureService.ReadAskSelectionAsync(
                sourceWindow, fromFloatingToolbar ? cachedText : null, fromFloatingToolbar);
        }
        catch (Exception ex)
        {
            ReportError(ex.Message, anchorPoint);
            return;
        }
        finally
        {
            _runningRequest = false;
        }

        var badge = ProvidersStore.FormatProviderBadge(_settings);
        var win = CompactResultWindow.CreateForAsk(
            _settings,
            anchorPoint,
            badge,
            hasSelection: !string.IsNullOrWhiteSpace(selection.Text),
            onSubmit: async (instruction, includeSelection) =>
            {
                try
                {
                    var context = includeSelection
                        ? selection
                        : new CapturedSelection("", null, IsConfirmedSelection: false);
                    var session = await BuildResultSessionAsync(action, instruction, sourceWindow, null, anchorPoint, context);
                    SetTrayStatus("default", session != null ? "Plyph — Previewing result" : "Plyph — Ready");
                    return session;
                }
                catch (OperationCanceledException)
                {
                    SetTrayStatus("default", "Plyph — Ready");
                    throw;
                }
                catch (Exception ex)
                {
                    SetTrayStatus("error", $"Plyph error: {ex.Message}");
                    ScheduleTrayReset(3500);
                    throw;
                }
            },
            onCopyError: err =>
            {
                SelectionCaptureService.SetClipboardTextSafe(err);
                ShowDoneStatus("Error copied", fromFloatingToolbar: false);
            },
            onOpenSettings: OpenSettings,
            onCancel: () =>
            {
                _activeRequestCts?.Cancel();
                _aiClient.Cancel();
                if (!_runningRequest)
                    SetTrayStatus("default", "Plyph — Ready");
            });
        TrackCompactWindow(win);
        win.Activate();
        ThemeHelper.BringWindowToFront(win, keepOnTop: true);
    }

    private async Task RunAiWorkflowAsync(
        PlyphAction action,
        IntPtr sourceWindow,
        string? cachedText,
        bool fromFloatingToolbar,
        Point anchorPoint)
    {
        try
        {
            SetWorkingStatus("Processing selection…", fromFloatingToolbar);
            var session = await BuildResultSessionAsync(
                action, null, sourceWindow, cachedText, anchorPoint);

            if (session == null)
            {
                _floatingToolbar.Hide(suppressSelection: false);
                _feedbackOverlay.Hide();
                if (!_runningRequest)
                    SetTrayStatus("default", "Plyph — Ready");
                return;
            }

            var forcePreview = action.InputMode == "prompt" || _settings.GetBoolean("preview-results");
            if (!forcePreview)
            {
                await PerformReplaceAsync(session.CurrentText, sourceWindow, delayed: false, fromFloatingToolbar);
                session.Dispose();
                return;
            }

            _floatingToolbar.Hide(suppressSelection: false);
            _feedbackOverlay.Hide();
            SetTrayStatus("default", "Plyph — Previewing result");

            ShowResultPresentation(session);
        }
        catch (OperationCanceledException)
        {
            _floatingToolbar.Hide(suppressSelection: false);
            _feedbackOverlay.Hide();
            if (!_runningRequest)
                SetTrayStatus("default", "Plyph — Ready");
        }
        catch (Exception ex)
        {
            ReportError(ex.Message, anchorPoint);
        }
    }

    private async Task<ResultSession?> BuildResultSessionAsync(
        PlyphAction action,
        string? askInstruction,
        IntPtr sourceWindow,
        string? cachedText,
        Point anchorPoint,
        CapturedSelection? askSelection = null)
    {
        _runningRequest = true;
        _activeRequestCts?.Cancel();
        _activeRequestCts?.Dispose();
        _activeRequestCts = new CancellationTokenSource();
        var ct = _activeRequestCts.Token;

        try
        {
            var captured = askSelection ?? await _captureService.ReadSelectionAsync(sourceWindow, cachedText);
            ct.ThrowIfCancellationRequested();
            if (askSelection == null && string.IsNullOrWhiteSpace(captured.Text))
                throw new InvalidOperationException("Select text in the active window first (or enable clipboard fallback in Settings).");

            SetTrayStatus("working", $"Plyph — {(action.Mode == "ask" ? "Asking AI…" : "Processing selection…")}");

            var options = BuildTransformOptions(action);
            var customPrompt = action.Mode == "ask"
                ? askInstruction
                : action.Mode == "custom" ? action.Prompt : null;

            var result = await _aiClient.TransformAsync(captured.Text, action.Mode, customPrompt, options, ct);
            if (ct.IsCancellationRequested)
                return null;

            var showDiff = action.InputMode != "prompt" && action.Mode != "ask" && _settings.GetBoolean("preview-show-changes");
            var originalText = (action.InputMode == "prompt" || action.Mode == "ask") ? null : captured.Text;

            var followUpProvider = options.Provider;
            var followUpModel = options.Model;
            var followUpPersonal = action.Mode == "custom"
                ? action.UsePersonalPreferences
                : _settings.GetBoolean($"{action.Mode}-use-personal-preferences");

            var refinement = new ResultRefinementCallbacks
            {
                RunAsync = async (currentResult, instruction) =>
                {
                    var refineOpts = new TransformOptions
                    {
                        Provider = followUpProvider,
                        Model = followUpModel,
                        InputMode = action.InputMode == "prompt" ? "prompt" : "transform",
                        UsePersonalPreferences = followUpPersonal
                    };
                    var promptText = $"Refine the provided text according to this instruction.\n\nInstruction: {instruction}";
                    return await _aiClient.TransformAsync(currentResult, "refine", promptText, refineOpts);
                },
                DiscussAsync = async history =>
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("Original selection:");
                    sb.AppendLine(captured.Text);
                    sb.AppendLine();
                    foreach (var turn in history)
                    {
                        if (!string.IsNullOrEmpty(turn.CurrentResult))
                        {
                            sb.AppendLine($"Current result (revision {turn.Revision ?? 1}):");
                            sb.AppendLine(turn.CurrentResult);
                            sb.AppendLine();
                        }
                        sb.AppendLine(turn.Role == "user" ? $"User: {turn.Content}" : $"Assistant: {turn.Content}");
                    }
                    var discussOpts = new TransformOptions
                    {
                        Provider = followUpProvider,
                        Model = followUpModel,
                        InputMode = "prompt",
                        UsePersonalPreferences = followUpPersonal
                    };
                    return await _aiClient.TransformAsync(
                        sb.ToString(),
                        "prompt",
                        "Answer the latest user question about the selection and generated result concisely and helpfully.",
                        discussOpts);
                },
                OnDispose = () => _aiClient.Cancel()
            };

            var sourceValid = sourceWindow != IntPtr.Zero && NativeMethods.IsWindow(sourceWindow);
            var canReplace = sourceValid && captured.IsConfirmedSelection;
            string? replaceWarning = null;
            if (askSelection == null || !string.IsNullOrWhiteSpace(captured.Text))
            {
                if (!sourceValid)
                    replaceWarning = "Original target window is no longer available — Copy is ready.";
                else if (!captured.IsConfirmedSelection)
                    replaceWarning = "Original selection could not be confirmed (clipboard fallback) — Copy is ready.";
            }

            return new ResultSession(result, refinement)
            {
                Settings = _settings,
                ActionTitle = action.Name,
                ActionIcon = action.Icon,
                ProviderBadge = ProvidersStore.FormatProviderBadge(_settings, followUpProvider, followUpModel),
                OriginalText = originalText,
                ShowChanges = showDiff,
                CanReplace = canReplace,
                ReplaceDisabledReason = replaceWarning,
                AnchorPoint = anchorPoint,
                OnReplace = finalText => _ = PerformReplaceAsync(
                    finalText, sourceWindow, delayed: true, fromFloatingToolbar: false, expectedSelection: captured.Text),
                OnCopy = finalText =>
                {
                    SelectionCaptureService.SetClipboardTextSafe(finalText);
                    ShowDoneStatus("Copied to clipboard", fromFloatingToolbar: false);
                }
            };
        }
        finally
        {
            _runningRequest = false;
        }
    }

    private void SetTrayStatus(string state, string tooltip)
    {
        _trayStatusSerial++;
        _trayState = state;
        _trayIcon.SetStatus(state, tooltip);
    }

    private void ScheduleTrayReset(int delayMs = 2500)
    {
        var token = _trayStatusSerial;
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            _keepAliveWindow.DispatcherQueue.TryEnqueue(() =>
            {
                if (_trayStatusSerial == token && !_runningRequest && _trayState != "default")
                    SetTrayStatus("default", "Plyph — Use AI on selected text");
            });
        });
    }

    private void CloseModalResultWindows()
    {
        var oldCompact = _compactResultWindow;
        _compactResultWindow = null;
        try { oldCompact?.Close(); } catch { }

        var oldResult = _resultWindow;
        _resultWindow = null;
        try { oldResult?.Close(); } catch { }
    }

    private void TrackCompactWindow(CompactResultWindow win)
    {
        _compactResultWindow = win;
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_compactResultWindow, win))
                _compactResultWindow = null;
            _selectionMonitor.ClearDismissedSelection();
            if (!_runningRequest && _trayState is "working" or "error")
                SetTrayStatus("default", "Plyph — Use AI on selected text");
        };
    }

    private void TrackResultWindow(ResultWindow win)
    {
        _resultWindow = win;
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_resultWindow, win))
                _resultWindow = null;
            _selectionMonitor.ClearDismissedSelection();
            if (!_runningRequest && _trayState is "working" or "error")
                SetTrayStatus("default", "Plyph — Use AI on selected text");
        };
    }

    private bool ShouldStartInFullWorkspace()
    {
        var pref = (_settings.GetString("result-popup-mode") ?? "auto").Trim().ToLowerInvariant();
        return pref == "workspace";
    }

    private bool ShouldAutoEnlargeCompact(string result)
    {
        var pref = (_settings.GetString("result-popup-mode") ?? "auto").Trim().ToLowerInvariant();
        if (pref == "compact")
            return false;

        if (result.Length > 700)
            return true;

        var lines = 1;
        foreach (var ch in result)
        {
            if (ch == '\n' && ++lines > 10)
                return true;
        }
        return false;
    }

    private void ShowResultPresentation(ResultSession session)
    {
        CloseModalResultWindows();

        if (ShouldStartInFullWorkspace())
            OpenFullResultWorkspace(session);
        else
            OpenCompactResultCard(session, ShouldAutoEnlargeCompact(session.CurrentText));
    }

    private void OpenCompactResultCard(ResultSession session, bool startEnlarged = false)
    {
        var win = CompactResultWindow.CreateForResult(session, startEnlarged);
        TrackCompactWindow(win);
        win.Activate();
        ThemeHelper.BringWindowToFront(win, keepOnTop: true);
    }

    private void OpenFullResultWorkspace(ResultSession session)
    {
        var win = new ResultWindow(session, onSwitchToCompact: s => OpenCompactResultCard(s, ShouldAutoEnlargeCompact(s.CurrentText)));
        TrackResultWindow(win);
        win.Activate();
        ThemeHelper.BringWindowToFront(win, keepOnTop: session.Pinned);
    }

    private async Task PerformReplaceAsync(
        string output, IntPtr targetWindow, bool delayed, bool fromFloatingToolbar, string? expectedSelection = null)
    {
        var (ok, err) = await _textReplacer.ReplaceAsync(output, delayed, targetWindow, expectedSelection);
        if (ok)
        {
            _selectionMonitor.ClearDismissedSelection();
            ShowDoneStatus("Replaced", fromFloatingToolbar);
        }
        else if (!string.IsNullOrEmpty(err))
        {
            if (delayed)
            {
                SelectionCaptureService.SetClipboardTextSafe(output);
                err += " The result was copied to the clipboard.";
            }
            ReportError(err);
        }
    }

    private async Task UndoLastReplacementAsync()
    {
        var (ok, err) = await _textReplacer.UndoLastAsync();
        if (ok)
            ShowDoneStatus("Undone", fromFloatingToolbar: false);
        else if (!string.IsNullOrEmpty(err))
            ReportError(err);
    }

    private void SetWorkingStatus(string message, bool onFloatingToolbar)
    {
        SetTrayStatus("working", $"Plyph — {message}");
        if (onFloatingToolbar && _floatingToolbar.IsShown)
            _floatingToolbar.ShowFeedback(message, "working");
        else if (_settings.GetBoolean("pointer-feedback"))
            _feedbackOverlay.ShowStatus(message, "working");
    }

    private void ShowDoneStatus(string message, bool fromFloatingToolbar)
    {
        SetTrayStatus("success", $"Plyph — {message}");
        if (fromFloatingToolbar && _floatingToolbar.IsShown)
        {
            _floatingToolbar.ShowFeedback(message, "success");
            _ = Task.Run(async () =>
            {
                await Task.Delay(1300);
                _keepAliveWindow.DispatcherQueue.TryEnqueue(() => _floatingToolbar.Hide(suppressSelection: false));
            });
        }
        else if (_settings.GetBoolean("pointer-feedback"))
        {
            _feedbackOverlay.ShowStatus(message, "success", durationMs: 1400);
        }

        ScheduleTrayReset(2500);
    }

    private void ReportError(string message, Point? anchorPoint = null)
    {
        _floatingToolbar.Hide(suppressSelection: false);
        _feedbackOverlay.Hide();
        SetTrayStatus("error", $"Plyph error: {message}");
        ScheduleTrayReset(3500);

        var effectiveAnchor = anchorPoint ??
                              (_floatingAnchorPoint != Point.Empty
                                  ? _floatingAnchorPoint
                                  : (NativeMethods.GetCursorPos(out var pt) ? new Point(pt.X + 8, pt.Y + 12) : new Point(400, 300)));

        CloseModalResultWindows();
        var win = CompactResultWindow.CreateForError(
            _settings,
            effectiveAnchor,
            message,
            onCopyError: txt =>
            {
                SelectionCaptureService.SetClipboardTextSafe(txt);
                ShowDoneStatus("Error copied", fromFloatingToolbar: false);
            },
            onOpenSettings: OpenSettings);
        TrackCompactWindow(win);
        win.Activate();
        ThemeHelper.BringWindowToFront(win, keepOnTop: true);
    }

    public void OpenSettings()
    {
        CloseStandalonePalette();
        _floatingToolbar.Hide(suppressSelection: false);

        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            ThemeHelper.BringWindowToFront(_settingsWindow, keepOnTop: false);
            return;
        }

        _settingsWindow = new SettingsWindow(
            _settings,
            _secretBackend,
            onShortcutsChanged: RebindHotkeys,
            isShortcutRegistered: _hotkeyManager.IsShortcutRegistered,
            onThemeChanged: ApplyThemeToOpenWindows);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate();
        ThemeHelper.BringWindowToFront(_settingsWindow, keepOnTop: false);
    }

    private void ApplyThemeToOpenWindows()
    {
        if (_settingsWindow != null) ThemeHelper.ApplyTheme(_settingsWindow, _settings);
        if (_resultWindow != null) _resultWindow.ApplyCurrentTheme();
        if (_compactResultWindow != null) _compactResultWindow.ApplyCurrentTheme();
        _floatingToolbar.RefreshTheme();
    }

    private void ExitApplication()
    {
        Dispose();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
            SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged;

        _activeRequestCts?.Cancel();
        _activeRequestCts?.Dispose();
        _selectionMonitor.Dispose();
        _hotkeyManager.Dispose();
        _textReplacer.Dispose();
        CloseStandalonePalette();
        _floatingToolbar.Dispose();
        _feedbackOverlay.Dispose();
        _trayIcon.Dispose();
        _settingsWindow?.Close();
        _compactResultWindow?.Close();
        _resultWindow?.Close();
        _keepAliveWindow.Close();
    }
}
