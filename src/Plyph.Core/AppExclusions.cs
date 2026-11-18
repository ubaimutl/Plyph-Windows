namespace Plyph.Core;

public static class AppExclusions
{
    private const string ProcessPrefix = "exe:";

    public static List<string> Read(IPlyphSettings settings) =>
        settings.GetString("excluded-apps")
            .Split([',', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string ForProcess(string name)
    {
        return ProcessPrefix + Uri.EscapeDataString(NormalizeProcessName(name));
    }

    public static string DisplayName(string rule) =>
        IsProcessRule(rule) ? DecodeProcessRule(rule) + ".exe" : rule;

    public static bool Matches(string rule, string? processName, string? windowClass)
    {
        if (IsProcessRule(rule))
            return processName != null && string.Equals(
                DecodeProcessRule(rule), NormalizeProcessName(processName), StringComparison.OrdinalIgnoreCase);

        // Keep the original window-class and partial process matching for saved rules.
        return processName?.Contains(rule, StringComparison.OrdinalIgnoreCase) == true ||
            windowClass?.Contains(rule, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string NormalizeProcessName(string name)
    {
        var executable = Path.GetFileName(name);
        return executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? executable[..^4] : executable;
    }

    private static string DecodeProcessRule(string rule) => Uri.UnescapeDataString(rule[ProcessPrefix.Length..]);

    private static bool IsProcessRule(string rule) => rule.StartsWith(ProcessPrefix, StringComparison.OrdinalIgnoreCase);
}
