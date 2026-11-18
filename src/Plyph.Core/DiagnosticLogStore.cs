using System.Text;

namespace Plyph.Core;

public static class DiagnosticLogStore
{
    private static readonly object Sync = new();
    private static readonly List<string> Lines = new();

    public static void Append(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        lock (Sync)
        {
            Lines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (Lines.Count > 250)
                Lines.RemoveAt(0);
        }
    }

    public static string GetSnapshot()
    {
        lock (Sync)
        {
            return Lines.Count == 0
                ? "No diagnostic logs recorded yet.\r\nEnable “Log request diagnostics” and run an action to capture sanitized provider, model, token, and HTTP status metadata."
                : string.Join(Environment.NewLine, Lines);
        }
    }

    public static void Clear()
    {
        lock (Sync)
            Lines.Clear();
    }
}
