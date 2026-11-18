using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Plyph.Core;

public sealed class ProviderAdvancedOptions
{
    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("reasoningEffort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningEffort { get; set; }

    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Parameters { get; set; }

    public bool IsEmpty =>
        Temperature == null &&
        string.IsNullOrEmpty(ReasoningEffort) &&
        (Parameters == null || Parameters.Count == 0);
}

public sealed class ProviderProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    [JsonPropertyName("adapter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Adapter { get; set; }

    [JsonPropertyName("advanced")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProviderAdvancedOptions? Advanced { get; set; }

    [JsonPropertyName("hasCustomHeaders")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasCustomHeaders { get; set; }

    public ProviderProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Url = Url,
        Model = Model,
        Adapter = Adapter,
        HasCustomHeaders = HasCustomHeaders,
        Advanced = Advanced == null ? null : new ProviderAdvancedOptions
        {
            Temperature = Advanced.Temperature,
            ReasoningEffort = Advanced.ReasoningEffort,
            Parameters = Advanced.Parameters != null ? JsonNode.Parse(Advanced.Parameters.ToJsonString())?.AsObject() : null
        }
    };
}

public static class ProvidersStore
{
    public static readonly IReadOnlyList<ProviderProfile> BuiltinProviders = new List<ProviderProfile>
    {
        new() { Id = "ollama", Name = "Ollama (local)" },
        new() { Id = "chatgpt", Name = "ChatGPT Account" },
        new() { Id = "cloudflare", Name = "Cloudflare Workers AI" },
        new() { Id = "gemini", Name = "Gemini" }
    };

    public static readonly IReadOnlyList<ProviderProfile> ProviderPresets = new List<ProviderProfile>
    {
        new() { Id = "groq", Name = "Groq", Url = "https://api.groq.com/openai/v1", Adapter = "groq" },
        new() { Id = "bai", Name = "B.AI", Url = "https://api.b.ai/v1", Adapter = "bai" },
        new() { Id = "openrouter", Name = "OpenRouter", Url = "https://openrouter.ai/api/v1", Adapter = "openrouter" },
        new() { Id = "cerebras", Name = "Cerebras", Url = "https://api.cerebras.ai/v1", Adapter = "cerebras" },
        new() { Id = "openai", Name = "OpenAI", Url = "https://api.openai.com/v1", Adapter = "openai" },
        new() { Id = "openai-compatible", Name = "LM Studio (local)", Url = "http://127.0.0.1:1234/v1", Adapter = "" },
        new() { Id = "vercel", Name = "Vercel AI Gateway", Url = "https://ai-gateway.vercel.sh/v1", Adapter = "vercel" }
    };

    public static readonly IReadOnlyList<string> ReasoningEfforts = new[]
    {
        "", "none", "minimal", "low", "medium", "high", "xhigh"
    };

    private static readonly HashSet<string> ReservedParameters = new(StringComparer.Ordinal)
    {
        "model", "messages", "stream", "stream_options", "max_tokens",
        "max_completion_tokens", "input", "prompt", "tools", "tool_choice", "functions",
        "function_call", "n", "__proto__", "constructor", "prototype"
    };

    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "content-type", "transfer-encoding", "connection"
    };

    private static readonly Regex HeaderNameRegex = new(@"^[!#$%&'*+.^_`|~0-9A-Za-z-]+$", RegexOptions.Compiled);
    private static readonly Regex UrlValidationRegex = new(@"^https?:\/\/(\[[0-9a-f:.]+\]|[^\s\/:?#@]+)(?::([0-9]+))?(?:\/[^\s?#]*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EndpointAuthorityRegex = new(@"^(https?):\/\/([^\/]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ProviderAdapter(ProviderProfile profile)
    {
        if (string.IsNullOrEmpty(profile.Adapter) || string.IsNullOrEmpty(profile.Url))
            return "";

        var preset = ProviderPresets.FirstOrDefault(item =>
            !string.IsNullOrEmpty(item.Adapter) && item.Adapter == profile.Adapter);
        if (preset == null || string.IsNullOrEmpty(preset.Url))
            return "";

        static string NormalizeEndpoint(string url)
        {
            var trimmed = url.Trim().TrimEnd('/');
            return EndpointAuthorityRegex.Replace(trimmed, match =>
            {
                var protocol = match.Groups[1].Value.ToLowerInvariant();
                var authority = match.Groups[2].Value.ToLowerInvariant();
                if (protocol == "https" && authority.EndsWith(":443", StringComparison.Ordinal))
                    authority = authority[..^4];
                else if (protocol == "http" && authority.EndsWith(":80", StringComparison.Ordinal))
                    authority = authority[..^3];
                return $"{protocol}://{authority}";
            });
        }

        return NormalizeEndpoint(profile.Url) == NormalizeEndpoint(preset.Url)
            ? preset.Adapter!
            : "";
    }

    public static ProviderAdvancedOptions ValidateAdvanced(ProviderAdvancedOptions? value)
    {
        if (value == null)
            return new ProviderAdvancedOptions();

        var serialized = JsonSerializer.Serialize(value);
        if (serialized.Length > 16384)
            throw new InvalidOperationException("Advanced settings is too large.");

        var advanced = new ProviderAdvancedOptions();
        if (value.Temperature.HasValue)
        {
            var temp = value.Temperature.Value;
            if (!double.IsFinite(temp) || temp < 0 || temp > 2)
                throw new InvalidOperationException("Temperature must be a number from 0 to 2, or left blank.");
            advanced.Temperature = temp;
        }

        if (!string.IsNullOrEmpty(value.ReasoningEffort))
        {
            if (!ReasoningEfforts.Contains(value.ReasoningEffort))
                throw new InvalidOperationException("Choose a supported reasoning effort.");
            advanced.ReasoningEffort = value.ReasoningEffort;
        }

        if (value.Parameters != null)
        {
            var paramJson = value.Parameters.ToJsonString();
            if (paramJson.Length > 16384)
                throw new InvalidOperationException("Extra request parameters is too large.");

            var cloned = new JsonObject();
            foreach (var kvp in value.Parameters)
            {
                if (ReservedParameters.Contains(kvp.Key))
                    throw new InvalidOperationException($"“{kvp.Key}” is controlled by Plyph and cannot be overridden here.");
                cloned[kvp.Key] = kvp.Value?.DeepClone();
            }

            if (cloned.Count > 0)
                advanced.Parameters = cloned;
        }

        return advanced;
    }

    public static Dictionary<string, string> ValidateHeaders(IDictionary<string, string>? value)
    {
        if (value == null)
            throw new InvalidOperationException("Custom headers must be a JSON object.");

        var json = JsonSerializer.Serialize(value);
        if (json.Length > 16384)
            throw new InvalidOperationException("Custom headers is too large.");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, content) in value)
        {
            if (string.IsNullOrEmpty(name) || !HeaderNameRegex.IsMatch(name) || ForbiddenHeaders.Contains(name))
                throw new InvalidOperationException($"“{name}” is not an allowed custom header.");
            if (!names.Add(name))
                throw new InvalidOperationException($"The header “{name}” is duplicated.");
            if (content == null || content.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new InvalidOperationException("Header values must be strings without line breaks.");

            result[name] = content;
        }

        return result;
    }

    public static Dictionary<string, string> ValidateHeadersJson(string jsonText)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(string.IsNullOrWhiteSpace(jsonText) ? "{}" : jsonText);
        }
        catch
        {
            throw new InvalidOperationException("Custom headers must contain valid JSON.");
        }

        if (node is not JsonObject obj)
            throw new InvalidOperationException("Custom headers must be a JSON object.");

        if (obj.ToJsonString().Length > 16384)
            throw new InvalidOperationException("Custom headers is too large.");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in obj)
        {
            var name = kvp.Key;
            if (string.IsNullOrEmpty(name) || !HeaderNameRegex.IsMatch(name) || ForbiddenHeaders.Contains(name))
                throw new InvalidOperationException($"“{name}” is not an allowed custom header.");
            if (!names.Add(name))
                throw new InvalidOperationException($"The header “{name}” is duplicated.");
            if (kvp.Value is not JsonValue jVal || !jVal.TryGetValue<string>(out var content) ||
                content.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            {
                throw new InvalidOperationException("Header values must be strings without line breaks.");
            }
            result[name] = content;
        }

        return result;
    }

    public static Dictionary<string, string> MergeProviderHeaders(
        IDictionary<string, string> defaults,
        IDictionary<string, string> custom)
    {
        var map = new Dictionary<string, (string Name, string Value)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, val) in defaults)
            map[name] = (name, val);
        foreach (var (name, val) in ValidateHeaders(custom))
            map[name] = (name, val);

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, pair) in map)
            merged[pair.Name] = pair.Value;
        return merged;
    }

    public static JsonObject ApplyProviderOptions(ProviderProfile profile, JsonObject body)
    {
        var advanced = ValidateAdvanced(profile.Advanced);
        if (advanced.Parameters != null)
        {
            foreach (var kvp in advanced.Parameters)
                body[kvp.Key] = kvp.Value?.DeepClone();
        }
        if (advanced.Temperature.HasValue)
            body["temperature"] = advanced.Temperature.Value;
        if (!string.IsNullOrEmpty(advanced.ReasoningEffort))
            body["reasoning_effort"] = advanced.ReasoningEffort;
        return body;
    }

    public static List<ProviderProfile> ReadProviders(IPlyphSettings settings)
    {
        var raw = settings.GetString("custom-providers");
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(raw);
        }
        catch
        {
            throw new InvalidOperationException("Saved providers could not be read. Your settings have been kept.");
        }

        if (parsed is not JsonArray array)
            throw new InvalidOperationException("Saved providers have an invalid format. Your settings have been kept.");

        var ids = new HashSet<string>(BuiltinProviders.Select(item => item.Id), StringComparer.Ordinal);
        var list = new List<ProviderProfile>();

        foreach (var item in array)
        {
            if (item is not JsonObject obj)
                throw new InvalidOperationException("A saved provider is invalid. Your settings have been kept.");

            var id = obj["id"]?.GetValueKind() == JsonValueKind.String ? obj["id"]!.GetValue<string>() : null;
            var name = obj["name"]?.GetValueKind() == JsonValueKind.String ? obj["name"]!.GetValue<string>() : null;
            var url = obj["url"]?.GetValueKind() == JsonValueKind.String ? obj["url"]!.GetValue<string>() : null;
            var model = obj["model"]?.GetValueKind() == JsonValueKind.String ? obj["model"]!.GetValue<string>() : null;
            var adapter = obj["adapter"]?.GetValueKind() == JsonValueKind.String ? obj["adapter"]!.GetValue<string>() : "";

            if (string.IsNullOrEmpty(id) || ids.Contains(id) ||
                name == null || url == null || model == null ||
                !(id.StartsWith("custom:", StringComparison.Ordinal) || ProviderPresets.Any(p => p.Id == id)))
            {
                throw new InvalidOperationException("A saved provider is invalid. Your settings have been kept.");
            }

            ids.Add(id);

            ProviderAdvancedOptions? advanced = null;
            if (obj.TryGetPropertyValue("advanced", out var advNode) && advNode != null)
            {
                if (advNode is not JsonObject advObj)
                    throw new InvalidOperationException("Advanced settings must be a JSON object.");

                var rawAdv = new ProviderAdvancedOptions();
                if (advObj.TryGetPropertyValue("temperature", out var tempNode) && tempNode != null)
                {
                    if (tempNode.GetValueKind() != JsonValueKind.Number)
                        throw new InvalidOperationException("Temperature must be a number from 0 to 2, or left blank.");
                    rawAdv.Temperature = tempNode.GetValue<double>();
                }
                if (advObj.TryGetPropertyValue("reasoningEffort", out var reNode) && reNode != null)
                {
                    if (reNode.GetValueKind() != JsonValueKind.String)
                        throw new InvalidOperationException("Choose a supported reasoning effort.");
                    rawAdv.ReasoningEffort = reNode.GetValue<string>();
                }
                if (advObj.TryGetPropertyValue("parameters", out var paramNode) && paramNode != null)
                {
                    if (paramNode is not JsonObject paramObj)
                        throw new InvalidOperationException("Extra request parameters must be a JSON object.");
                    rawAdv.Parameters = paramObj;
                }
                advanced = ValidateAdvanced(rawAdv);
            }

            var hasCustomHeaders = obj.TryGetPropertyValue("hasCustomHeaders", out var hchNode) &&
                                   hchNode?.GetValueKind() == JsonValueKind.True
                ? true
                : (bool?)null;

            var candidate = new ProviderProfile
            {
                Id = id,
                Name = id == "openai-compatible" && name == "OpenAI-compatible"
                    ? "OpenAI-compatible (legacy)"
                    : name,
                Url = url,
                Model = model,
                Adapter = adapter
            };
            candidate.Adapter = ProviderAdapter(candidate);
            if (advanced != null && !advanced.IsEmpty)
                candidate.Advanced = advanced;
            if (hasCustomHeaders == true)
                candidate.HasCustomHeaders = true;

            list.Add(candidate);
        }

        return list;
    }

    public static ProviderProfile ValidateProvider(ProviderProfile profile, bool requireModel = true)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("Enter a provider name.");

        var url = (profile.Url ?? "").Trim().TrimEnd('/');
        var match = UrlValidationRegex.Match(url);
        if (!match.Success)
            throw new InvalidOperationException("Enter an HTTP or HTTPS base URL without credentials, a query, or a fragment.");

        if (match.Groups[2].Success)
        {
            if (!int.TryParse(match.Groups[2].Value, out var port) || port < 1 || port > 65535)
                throw new InvalidOperationException("Enter an HTTP or HTTPS base URL without credentials, a query, or a fragment.");
        }

        var model = (profile.Model ?? "").Trim();
        if (requireModel && string.IsNullOrEmpty(model))
            throw new InvalidOperationException("Enter a default model.");

        var validatedAdv = ValidateAdvanced(profile.Advanced);
        var result = new ProviderProfile
        {
            Id = profile.Id,
            Name = profile.Name.Trim(),
            Url = url,
            Model = model,
            Adapter = profile.Adapter,
            HasCustomHeaders = profile.HasCustomHeaders == true ? true : null,
            Advanced = validatedAdv.IsEmpty ? null : validatedAdv
        };
        result.Adapter = ProviderAdapter(result);
        return result;
    }

    public static ProviderProfile LegacyProfile(IPlyphSettings settings, ProviderProfile preset)
    {
        var url = preset.Id == "openai-compatible"
            ? settings.GetString("openai-compatible-url")
            : preset.Url ?? "";

        return new ProviderProfile
        {
            Id = preset.Id,
            Name = preset.Id == "openai-compatible" ? "OpenAI-compatible (legacy)" : preset.Name,
            Url = url,
            Model = settings.GetString($"{preset.Id}-model"),
            Adapter = preset.Adapter ?? ""
        };
    }

    public static void MigrateProviders(IPlyphSettings settings, IEnumerable<string>? credentialIds = null, bool complete = false)
    {
        if (settings.GetInt("provider-migration-version") >= 1)
            return;

        var profiles = ReadProviders(settings);
        var referenced = new HashSet<string>(StringComparer.Ordinal)
        {
            settings.GetString("provider"),
            settings.GetString("prompt-run-provider")
        };
        foreach (var action in ActionsStore.ReadActions(settings))
            referenced.Add(action.Provider);
        if (credentialIds != null)
        {
            foreach (var cid in credentialIds)
                referenced.Add(cid);
        }

        foreach (var preset in ProviderPresets)
        {
            var suffixes = preset.Id == "openai-compatible"
                ? new[] { "model", "api-key", "url" }
                : new[] { "model", "api-key" };
            var configured = suffixes.Any(suffix => settings.GetUserValue($"{preset.Id}-{suffix}") != null);
            if ((referenced.Contains(preset.Id) || configured) && !profiles.Any(item => item.Id == preset.Id))
                profiles.Add(LegacyProfile(settings, preset));
        }

        var serialized = JsonSerializer.Serialize(profiles);
        if (serialized != settings.GetString("custom-providers") &&
            !settings.SetString("custom-providers", serialized))
        {
            throw new InvalidOperationException("Providers could not be saved. Your previous settings have been kept.");
        }

        if (complete && !settings.SetInt("provider-migration-version", 1))
            throw new InvalidOperationException("Provider migration could not be marked complete. It will retry next time.");
    }

    public static List<ProviderProfile> ListProviders(IPlyphSettings settings)
    {
        var profiles = ReadProviders(settings);
        var list = new List<ProviderProfile>(BuiltinProviders.Count + profiles.Count);
        list.AddRange(BuiltinProviders.Select(p => p.Clone()));
        list.AddRange(profiles);
        return list;
    }

    public static List<string> ProviderLabels(IReadOnlyList<ProviderProfile> providers)
    {
        return providers.Select(profile =>
        {
            var duplicates = providers.Where(item => item.Name == profile.Name).ToList();
            if (duplicates.Count == 1)
                return profile.Name;

            var label = $"{profile.Name} — {profile.Url ?? profile.Id}";
            var sameEndpoint = duplicates.Where(item => item.Url == profile.Url).ToList();
            if (sameEndpoint.Count == 1)
                return label;

            var length = 8;
            while (sameEndpoint.Any(item =>
                       item.Id != profile.Id &&
                       TailSlice(item.Id, length) == TailSlice(profile.Id, length)))
            {
                length++;
            }

            return $"{profile.Name} · {TailSlice(profile.Id, length)} — {profile.Url ?? profile.Id}";
        }).ToList();
    }

    private static string TailSlice(string value, int length)
    {
        return value.Length <= length ? value : value[^length..];
    }

    public static ProviderProfile ResolveProvider(IPlyphSettings settings, string id)
    {
        var info = ListProviders(settings).FirstOrDefault(item => item.Id == id);
        if (info == null && settings.GetInt("provider-migration-version") < 1)
        {
            var preset = ProviderPresets.FirstOrDefault(item => item.Id == id);
            if (preset != null)
                info = LegacyProfile(settings, preset);
        }

        if (info == null)
            throw new InvalidOperationException("The selected provider no longer exists. Choose a provider in Settings or edit this action.");

        return info;
    }

    public static string ProviderModel(IPlyphSettings settings, string id)
    {
        var info = ResolveProvider(settings, id);
        return info.Url != null ? (info.Model ?? "") : settings.GetString($"{id}-model");
    }

    public static void SaveProvider(IPlyphSettings settings, ProviderProfile profile)
    {
        var value = ValidateProvider(profile, requireModel: false);
        var profiles = ReadProviders(settings);
        var index = profiles.FindIndex(item => item.Id == value.Id);
        if (index >= 0)
            profiles[index] = value;
        else
            profiles.Add(value);

        if (!settings.SetString("custom-providers", JsonSerializer.Serialize(profiles)))
            throw new InvalidOperationException("Providers could not be saved.");
    }

    public static void SetProviderModel(IPlyphSettings settings, string id, string model)
    {
        var profile = ResolveProvider(settings, id);
        if (profile.Url != null)
        {
            var updated = profile.Clone();
            updated.Model = model;
            SaveProvider(settings, updated);
        }
        else
        {
            settings.SetString($"{id}-model", model);
        }
    }

    public static List<string> ProviderReferences(IPlyphSettings settings, string id)
    {
        var references = new List<string>();
        if (settings.GetString("provider") == id)
            references.Add("Active provider");
        if (settings.GetString("prompt-run-provider") == id)
            references.Add("Run selected prompt");
        foreach (var action in ActionsStore.ReadActions(settings))
        {
            if (action.Provider == id)
                references.Add(action.Name);
        }
        return references;
    }

    public static void RemoveProvider(IPlyphSettings settings, string id)
    {
        var references = ProviderReferences(settings, id);
        if (references.Count > 0)
            throw new InvalidOperationException($"Choose another provider first for: {string.Join(", ", references)}.");

        var remaining = ReadProviders(settings).Where(item => item.Id != id).ToList();
        if (!settings.SetString("custom-providers", JsonSerializer.Serialize(remaining)))
            throw new InvalidOperationException("The provider could not be removed.");
    }

    public static string FormatProviderBadge(IPlyphSettings settings, string? providerOverride = null, string? modelOverride = null)
    {
        try
        {
            var providerId = string.IsNullOrWhiteSpace(providerOverride)
                ? settings.GetString("provider")
                : providerOverride.Trim();
            if (string.IsNullOrWhiteSpace(providerId))
                return "AI";

            var profile = ResolveProvider(settings, providerId);
            var model = !string.IsNullOrWhiteSpace(modelOverride)
                ? modelOverride.Trim()
                : ProviderModel(settings, providerId);

            if (providerId == "chatgpt" && string.IsNullOrWhiteSpace(model))
                model = "Auto";

            if (string.IsNullOrWhiteSpace(model))
                return profile.Name;

            var shortModel = model.Contains('/') ? model[(model.LastIndexOf('/') + 1)..] : model;
            if (shortModel.Length > 26)
                shortModel = shortModel[..25] + "…";
            return $"{profile.Name} · {shortModel}";
        }
        catch
        {
            return "AI";
        }
    }
}
