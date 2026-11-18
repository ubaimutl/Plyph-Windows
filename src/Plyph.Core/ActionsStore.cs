using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plyph.Core;

public sealed class PlyphAction
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("inputMode")]
    public string InputMode { get; set; } = "transform";

    [JsonPropertyName("usePersonalPreferences")]
    public bool UsePersonalPreferences { get; set; } = true;

    [JsonPropertyName("inputLimit")]
    public long InputLimit { get; set; }

    [JsonPropertyName("outputLimit")]
    public long OutputLimit { get; set; }

    [JsonIgnore]
    public string Mode { get; set; } = "custom";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "custom";

    [JsonIgnore]
    public bool SeparatorBefore { get; set; }
}

public static class ActionsStore
{
    private const long MaxSafeInteger = 9007199254740991L;

    public static List<PlyphAction> ReadActions(IPlyphSettings settings)
    {
        var raw = settings.GetString("custom-actions");
        if (string.IsNullOrWhiteSpace(raw))
            return new List<PlyphAction>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch
        {
            return new List<PlyphAction>();
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return new List<PlyphAction>();

            var result = new List<PlyphAction>();
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                if (elem.ValueKind != JsonValueKind.Object)
                    continue;

                if (!elem.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
                    continue;
                if (!elem.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                    continue;
                if (!elem.TryGetProperty("prompt", out var promptProp) || promptProp.ValueKind != JsonValueKind.String)
                    continue;

                var id = idProp.GetString() ?? "";
                var name = nameProp.GetString() ?? "";
                var prompt = promptProp.GetString() ?? "";

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var inputMode = elem.TryGetProperty("inputMode", out var modeProp) &&
                                modeProp.ValueKind == JsonValueKind.String &&
                                modeProp.GetString() == "prompt"
                    ? "prompt"
                    : "transform";

                if (string.IsNullOrWhiteSpace(prompt) && inputMode != "prompt")
                    continue;

                var enabled = true;
                if (elem.TryGetProperty("enabled", out var enabledProp) && enabledProp.ValueKind == JsonValueKind.False)
                    enabled = false;

                var provider = elem.TryGetProperty("provider", out var provProp) && provProp.ValueKind == JsonValueKind.String
                    ? provProp.GetString() ?? ""
                    : "";

                var model = elem.TryGetProperty("model", out var modProp) && modProp.ValueKind == JsonValueKind.String
                    ? modProp.GetString() ?? ""
                    : "";

                var usePersonalPreferences = true;
                if (elem.TryGetProperty("usePersonalPreferences", out var prefProp) &&
                    (prefProp.ValueKind == JsonValueKind.True || prefProp.ValueKind == JsonValueKind.False))
                {
                    usePersonalPreferences = prefProp.GetBoolean();
                }

                long inputLimit = 0;
                if (elem.TryGetProperty("inputLimit", out var inLimProp) &&
                    inLimProp.ValueKind == JsonValueKind.Number &&
                    inLimProp.TryGetInt64(out var parsedIn) &&
                    parsedIn > 0 && parsedIn <= MaxSafeInteger)
                {
                    inputLimit = parsedIn;
                }

                long outputLimit = 0;
                if (elem.TryGetProperty("outputLimit", out var outLimProp) &&
                    outLimProp.ValueKind == JsonValueKind.Number &&
                    outLimProp.TryGetInt64(out var parsedOut) &&
                    parsedOut > 0 && parsedOut <= MaxSafeInteger)
                {
                    outputLimit = parsedOut;
                }

                result.Add(new PlyphAction
                {
                    Id = id,
                    Name = name,
                    Prompt = prompt,
                    Enabled = enabled,
                    Provider = provider,
                    Model = model,
                    InputMode = inputMode,
                    UsePersonalPreferences = usePersonalPreferences,
                    InputLimit = inputLimit,
                    OutputLimit = outputLimit,
                    Mode = "custom",
                    Icon = elem.TryGetProperty("icon", out var iconProp) && iconProp.ValueKind == JsonValueKind.String
                        ? iconProp.GetString() ?? "custom"
                        : "custom"
                });
            }

            return result;
        }
    }

    public static void WriteActions(IPlyphSettings settings, IEnumerable<PlyphAction> actions)
    {
        var json = JsonSerializer.Serialize(actions);
        settings.SetString("custom-actions", json);
    }

    public static List<string> GetQuickActionIds(IPlyphSettings settings)
    {
        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(settings.GetString("quick-actions"));
            if (ids != null)
                return ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Take(4).ToList();
        }
        catch (JsonException)
        {
        }

        return ["ask", "correct", "rewrite"];
    }

    public static List<PlyphAction> GetAvailableActions(IPlyphSettings settings, bool includeDisabled = false)
    {
        var list = new List<PlyphAction>
        {
            new() { Id = "ask", Name = "Ask...", Mode = "ask", Icon = "ask" },
            new() { Id = "correct", Name = "Correct selected text", Mode = "correct", Icon = "correct" },
            new() { Id = "rewrite", Name = "Rewrite selected text", Mode = "rewrite", Icon = "rewrite" },
            new() { Id = "prompt", Name = "Run selected prompt", Mode = "prompt", Icon = "prompt" }
        };

        var custom = ReadActions(settings).Where(a => includeDisabled || a.Enabled).ToList();
        for (var i = 0; i < custom.Count; i++)
        {
            custom[i].Mode = "custom";
            custom[i].SeparatorBefore = i == 0;
            list.Add(custom[i]);
        }

        return list;
    }
}
