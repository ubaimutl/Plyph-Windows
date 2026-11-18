using System.Drawing;
using System.Text.Json.Serialization;
using Plyph.Core;

namespace Plyph.Windows.UI;

public sealed class DiscussionTurn
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("currentResult")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentResult { get; set; }

    [JsonPropertyName("revision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Revision { get; set; }
}

public sealed class ResultRefinementCallbacks : IDisposable
{
    public required Func<string, string, Task<string>> RunAsync { get; init; }
    public required Func<List<DiscussionTurn>, Task<string>> DiscussAsync { get; init; }
    public Action? OnDispose { get; init; }

    public void Dispose() => OnDispose?.Invoke();
}

/// <summary>
/// Shared state for a single result flow so transitions between the compact inline card
/// and the full editing workspace preserve revisions, edits, diff settings, and discussion turns.
/// </summary>
public sealed class ResultSession : IDisposable
{
    public required IPlyphSettings Settings { get; init; }
    public required string ActionTitle { get; init; }
    public required string ActionIcon { get; init; }
    public required string ProviderBadge { get; init; }
    public string? OriginalText { get; init; }
    public bool CanReplace { get; init; } = true;
    public string? ReplaceDisabledReason { get; init; }
    public Point AnchorPoint { get; init; }
    public ResultRefinementCallbacks? Refinement { get; private set; }

    public List<string> Revisions { get; }
    public int RevisionIndex { get; set; }
    public bool ShowChanges { get; set; }
    public bool SideBySideCompare { get; set; }
    public bool WrapText { get; set; } = true;
    public bool Pinned { get; set; }
    public bool OpenDiscussionOnExpand { get; set; }
    public List<DiscussionTurn> Discussion { get; } = new();
    public Dictionary<string, string> FollowUpDrafts { get; } = new()
    {
        ["refine"] = "",
        ["discuss"] = ""
    };

    public required Action<string> OnReplace { get; init; }
    public required Action<string> OnCopy { get; init; }
    public Action? OnCancel { get; init; }
    public Action? OnClose { get; init; }

    public bool Finished { get; private set; }

    public ResultSession(string initialResult, ResultRefinementCallbacks? refinement)
    {
        Revisions = new List<string> { initialResult.Replace("\r\n", "\n") };
        Refinement = refinement;
    }

    public string CurrentText
    {
        get => Revisions[Math.Clamp(RevisionIndex, 0, Revisions.Count - 1)];
        set => Revisions[Math.Clamp(RevisionIndex, 0, Revisions.Count - 1)] = (value ?? "").Replace("\r\n", "\n");
    }

    public bool HasOriginalDiff =>
        OriginalText != null && CurrentText != OriginalText.Replace("\r\n", "\n");

    public void CompleteReplace(string text)
    {
        if (Finished) return;
        Finished = true;
        OnReplace(text);
    }

    public void CompleteCopy(string text)
    {
        if (Finished) return;
        Finished = true;
        OnCopy(text);
    }

    public void CompleteCancel()
    {
        if (Finished) return;
        Finished = true;
        OnCancel?.Invoke();
    }

    public void CompleteClose()
    {
        if (Finished) return;
        Finished = true;
        OnClose?.Invoke();
    }

    public void Dispose()
    {
        Refinement?.Dispose();
        Refinement = null;
    }
}
