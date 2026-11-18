using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Plyph.Core;

public sealed class TransformOptions
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? InputMode { get; set; }
    public bool? UsePersonalPreferences { get; set; }
    public long? InputLimit { get; set; }
    public object? OutputLimit { get; set; }
}

public sealed record HttpTransformRequest(
    string Url,
    Dictionary<string, string> Headers,
    JsonObject Body);

public sealed record HttpTransformResponse(
    int Status,
    JsonElement Data);

public sealed class AiClient : IDisposable
{
    private const long MaxSafeInteger = 9007199254740991L;

    private static readonly IReadOnlyDictionary<string, string> ProviderNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ollama"] = "Ollama",
        ["chatgpt"] = "ChatGPT Account",
        ["cloudflare"] = "Cloudflare Workers AI",
        ["bai"] = "B.AI",
        ["groq"] = "Groq",
        ["gemini"] = "Gemini",
        ["openrouter"] = "OpenRouter",
        ["cerebras"] = "Cerebras",
        ["openai"] = "OpenAI",
        ["openai-compatible"] = "OpenAI-compatible",
        ["vercel"] = "Vercel AI Gateway"
    };

    public const string PlainTextGuidance =
        "Output formatting: Use plain text by default. " +
        "Do not add Markdown formatting such as headings, emphasis, links, tables, or code fences " +
        "unless the user request or action instructions explicitly ask for Markdown, " +
        "or the source text being transformed already contains Markdown. " +
        "When transforming existing Markdown, preserve its formatting unless asked to change it. " +
        "An explicit request for another output format, such as code, JSON, or HTML, takes precedence; " +
        "return that format directly without surrounding Markdown fences unless requested. " +
        "Keep normal paragraph breaks and do not mention these formatting instructions.";

    private static readonly Regex TaggedOutputRegex = new(@"^<text>\s*([\s\S]*?)\s*<\/text>$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VariableRegex = new(@"\$\{(selection|language|tone|style)\}", RegexOptions.Compiled);
    private static readonly HashSet<string> CloudPresetAdapters = new(StringComparer.Ordinal)
    {
        "groq", "bai", "openrouter", "cerebras", "openai", "vercel"
    };

    private IPlyphSettings? _settings;
    private readonly ISecretBackend _secretBackend;
    private HttpClient? _httpClient;
    private CancellationTokenSource? _cancellable;
    private ChatGptClient? _chatGptClient;

    public Func<HttpTransformRequest, CancellationToken, Task<HttpTransformResponse>>? HttpSenderOverride { get; set; }
    public Func<string, string, string, CancellationToken, Task<string>>? ChatGptTransformOverride { get; set; }
    public Action<string>? DiagnosticLogger { get; set; }

    public AiClient(IPlyphSettings settings, ISecretBackend secretBackend)
    {
        _settings = settings;
        _secretBackend = secretBackend;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    }

    public void Cancel()
    {
        var cts = Interlocked.Exchange(ref _cancellable, null);
        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
        }
    }

    public void Destroy() => Dispose();

    public void Dispose()
    {
        Cancel();
        _chatGptClient?.Destroy();
        _chatGptClient = null;
        _httpClient?.Dispose();
        _httpClient = null;
        _settings = null;
    }

    public static string Payload(string text) =>
        $"Transform only the text inside the tags.\nReturn only the transformed text.\n<text>\n{text}\n</text>";

    private static JsonArray BuildMessages(string prompt, string text, string inputMode)
    {
        var items = new JsonArray();
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            items.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = prompt
            });
        }
        items.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = inputMode == "prompt" ? text : Payload(text)
        });
        return items;
    }

    private static long TokenLimit(object? value)
    {
        return value switch
        {
            long l when l > 0 && l <= MaxSafeInteger => l,
            int i when i > 0 => i,
            _ => 0
        };
    }

    public static long EstimateTokens(string text) =>
        (long)Math.Ceiling(text.Length / 4.0);

    public static long MaxTokens(string text, long outputLimit = 0)
    {
        var explicitLim = TokenLimit(outputLimit);
        if (explicitLim > 0)
            return explicitLim;
        return Math.Min(2000L, Math.Max(1000L, EstimateTokens(text) + 180L));
    }

    public static string CleanOutput(string text, string inputMode = "transform")
    {
        var output = text.Trim();
        if (inputMode == "prompt")
            return output;

        var match = TaggedOutputRegex.Match(output);
        if (match.Success)
            output = match.Groups[1].Value.Trim();
        return output;
    }

    private static string DebugEndpoint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "invalid endpoint";
        var portPart = !uri.IsDefaultPort ? $":{uri.Port}" : "";
        return $"{uri.Scheme}://{uri.Host}{portPart}{uri.AbsolutePath}";
    }

    private void DebugLog(string message)
    {
        if (_settings?.GetBoolean("debug-logging") == true)
        {
            var line = $"[Plyph] {message}";
            DiagnosticLogger?.Invoke(line);
            System.Diagnostics.Debug.WriteLine(line);
        }
    }

    private static JsonObject OpenAiBody(string model, string prompt, string text, string inputMode, long outputLimit = 0, bool cerebras = false)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = BuildMessages(prompt, text, inputMode)
        };
        body[cerebras ? "max_completion_tokens" : "max_tokens"] = MaxTokens(text, outputLimit);
        return body;
    }

    private static Exception ProviderError(HttpTransformResponse result, string provider, string model, string? displayName = null)
    {
        var name = displayName ?? (ProviderNames.TryGetValue(provider, out var pn) ? pn : "Provider");
        string detail = "";
        if (result.Data.ValueKind == JsonValueKind.Object && result.Data.TryGetProperty("error", out var errProp))
        {
            if (errProp.ValueKind == JsonValueKind.String)
                detail = errProp.GetString() ?? "";
            else if (errProp.ValueKind == JsonValueKind.Object &&
                     errProp.TryGetProperty("message", out var msgProp) &&
                     msgProp.ValueKind == JsonValueKind.String)
                detail = msgProp.GetString() ?? "";
        }

        if (result.Status is 401 or 403)
        {
            return new InvalidOperationException(provider == "cloudflare"
                ? "Cloudflare rejected the API token or Account ID. Check them in Settings."
                : $"{name} rejected the API key. Check it in Settings.");
        }
        if (result.Status == 404)
        {
            return new InvalidOperationException(provider == "cloudflare"
                ? $"Cloudflare could not find the account or model “{model}”."
                : $"{name} could not find model “{model}”.");
        }
        if (result.Status == 408)
            return new InvalidOperationException($"{name} timed out. Try again.");
        if (result.Status == 429)
            return new InvalidOperationException($"{name} rate limit reached. Wait and try again.");
        if (result.Status >= 500)
            return new InvalidOperationException($"{name} is temporarily unavailable ({result.Status}).");
        if (result.Status >= 400)
            return new InvalidOperationException(!string.IsNullOrEmpty(detail) ? detail : $"{name} rejected the request ({result.Status}).");

        return new InvalidOperationException(!string.IsNullOrEmpty(detail) ? detail : $"{name} returned an unexpected response.");
    }

    private static Exception OutputLimitError(long thinkingTokens = 0)
    {
        if (TokenLimit(thinkingTokens) > 0)
        {
            return new InvalidOperationException(
                $"Response reached this action’s output limit, and the model used {thinkingTokens} tokens of it " +
                "for thinking. Raise the limit or set it to Auto.");
        }
        return new InvalidOperationException(
            "Response reached the output limit. Try a custom action with a higher output limit, or select less text.");
    }

    private static string OutputOrError(HttpTransformResponse result, string provider, string model, string inputMode, string? displayName = null)
    {
        if (result.Data.ValueKind == JsonValueKind.Object &&
            result.Data.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.ValueKind == JsonValueKind.Object)
            {
                if (first.TryGetProperty("finish_reason", out var fr) &&
                    fr.ValueKind == JsonValueKind.String &&
                    fr.GetString() == "length")
                {
                    throw OutputLimitError();
                }

                if (first.TryGetProperty("message", out var msg) &&
                    msg.ValueKind == JsonValueKind.Object &&
                    msg.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                {
                    var output = (content.GetString() ?? "").Trim();
                    if (!string.IsNullOrEmpty(output))
                        return CleanOutput(output, inputMode);
                }
            }
        }

        throw ProviderError(result, provider, model, displayName);
    }

    private async Task<HttpTransformResponse> RequestJsonAsync(
        string url,
        Dictionary<string, string> headers,
        JsonObject body,
        CancellationToken cancellationToken)
    {
        if (HttpSenderOverride != null)
        {
            var overrideRes = await HttpSenderOverride(new HttpTransformRequest(url, headers, body), cancellationToken).ConfigureAwait(false);
            DebugLog($"AI response: status={overrideRes.Status}, endpoint={DebugEndpoint(url)}");
            return overrideRes;
        }

        var client = _httpClient ?? throw new InvalidOperationException("Client is disposed.");
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        foreach (var (name, val) in headers)
            req.Headers.TryAddWithoutValidation(name, val);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        try
        {
            using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            var text = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(text);
            }
            catch
            {
                if (status < 400)
                    throw new InvalidOperationException($"Invalid response ({status})");
            }

            using (doc)
            {
                var data = doc != null ? doc.RootElement.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                DebugLog($"AI response: status={status}, endpoint={DebugEndpoint(url)}");
                return new HttpTransformResponse(status, data);
            }
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The request timed out. Try again.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Could not connect. Check your internet connection or local server.");
        }
    }

    public async Task<string> TransformAsync(
        string text,
        string mode,
        string? customPrompt = null,
        TransformOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new TransformOptions();
        Cancel();

        var settings = _settings ?? throw new InvalidOperationException("Client is disposed.");
        var inputMode = mode == "prompt" || options.InputMode == "prompt" ? "prompt" : "transform";
        var hasActionLimits = mode is "custom" or "prompt";
        var inputLimit = hasActionLimits ? TokenLimit(options.InputLimit) : 0;
        var estimatedTokens = EstimateTokens(text);

        if (inputLimit > 0 && estimatedTokens > inputLimit)
        {
            throw new InvalidOperationException(
                $"Selected text is about {estimatedTokens} tokens, above this action's " +
                $"{inputLimit}-token input limit. Select less text or raise the limit.");
        }

        var explicitOutputLimit = hasActionLimits ? TokenLimit(options.OutputLimit) : 0;
        var outputLimit = explicitOutputLimit > 0 ? explicitOutputLimit : (inputMode == "prompt" ? 2000 : 0);

        var selectedProvider = !string.IsNullOrEmpty(options.Provider) ? options.Provider! : settings.GetString("provider");
        var info = ProvidersStore.ResolveProvider(settings, selectedProvider);
        var provider = info.Id;
        var model = !string.IsNullOrEmpty(options.Model) ? options.Model! : ProvidersStore.ProviderModel(settings, provider);

        if (info.Url != null && string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException($"Choose a model for “{info.Name}” in Settings before running an action.");

        var promptKey = mode == "rewrite"
            ? "prompt-rewrite"
            : mode == "prompt" ? "prompt-run" : "prompt-correct";
        var storedPrompt = settings.GetString(promptKey);
        var prompt = ExpandPrompt(settings, customPrompt ?? storedPrompt, text);

        var usePersonalPreferences = options.UsePersonalPreferences ?? (mode == "custom"
            ? true
            : settings.GetBoolean($"{mode}-use-personal-preferences"));
        var personalPreferences = usePersonalPreferences ? settings.GetString("personal-preferences").Trim() : "";
        if (!string.IsNullOrEmpty(personalPreferences))
        {
            prompt = "Personal context about the user: " + JsonSerializer.Serialize(personalPreferences) + "\n" +
                     "Use this background only when relevant to the current task; ignore unrelated details. " +
                     "Explicit action instructions and user requests take priority. " +
                     "Preserve meaning, facts, code and required output formats.\n\nAction instructions:\n" + prompt;
        }

        if (settings.GetBoolean("prefer-plain-text"))
        {
            var parts = new[] { prompt, PlainTextGuidance }.Where(p => !string.IsNullOrWhiteSpace(p));
            prompt = string.Join("\n\n", parts);
        }

        var loggedOutputLimit = provider == "chatgpt"
            ? "managed by ChatGPT"
            : provider == "gemini" && explicitOutputLimit == 0
                ? "model default"
                : MaxTokens(text, outputLimit).ToString();

        DebugLog(
            $"AI request: provider={provider}, model={model}, action={mode}, " +
            $"input-tokens≈{estimatedTokens}, output-limit={loggedOutputLimit}");

        var cts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _cancellable, cts);
        if (previous != null)
        {
            try { previous.Cancel(); } catch { }
        }

        try
        {
            cts.Token.ThrowIfCancellationRequested();

            if (info.Url != null)
                return await CompatibleAsync(settings, info, text, prompt, model, inputMode, outputLimit, cts).ConfigureAwait(false);

            if (provider == "chatgpt")
            {
                if (hasActionLimits && TokenLimit(options.OutputLimit) > 0)
                    throw new InvalidOperationException("ChatGPT does not support output token limits. Set this action’s output limit to Auto.");

                var userPayload = inputMode == "prompt" ? text : Payload(text);
                string result;
                if (ChatGptTransformOverride != null)
                {
                    result = await ChatGptTransformOverride(userPayload, prompt, model, cts.Token).ConfigureAwait(false);
                }
                else
                {
                    _chatGptClient ??= new ChatGptClient(settings, _secretBackend);
                    result = await _chatGptClient.TransformAsync(userPayload, prompt, model, cts.Token).ConfigureAwait(false);
                }
                return CleanOutput(result, inputMode);
            }

            if (provider == "ollama")
                return await OllamaAsync(settings, text, prompt, model, inputMode, outputLimit, cts.Token).ConfigureAwait(false);
            if (provider == "cloudflare")
                return await CloudflareAsync(settings, text, prompt, model, inputMode, outputLimit, cts.Token).ConfigureAwait(false);
            if (provider == "gemini")
                return await GeminiAsync(settings, text, prompt, model, inputMode, explicitOutputLimit, cts.Token).ConfigureAwait(false);

            throw new InvalidOperationException("Unsupported provider connection.");
        }
        catch (Exception error)
        {
            DebugLog($"AI request failed: provider={provider}, model={model}, error={error.GetType().Name}");
            throw;
        }
        finally
        {
            Interlocked.CompareExchange(ref _cancellable, null, cts);
            cts.Dispose();
        }
    }

    private static string ExpandPrompt(IPlyphSettings settings, string prompt, string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["selection"] = text,
            ["language"] = settings.GetString("variable-language"),
            ["tone"] = settings.GetString("variable-tone"),
            ["style"] = settings.GetString("variable-style")
        };
        return VariableRegex.Replace(prompt, match => values[match.Groups[1].Value]);
    }

    private async Task<string> RequiredKeyAsync(IPlyphSettings settings, string provider, CancellationToken cancellationToken)
    {
        var value = await SecretsStore.GetApiKeyAsync(settings, _secretBackend, provider, cancellationToken, interactive: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(provider == "cloudflare"
                ? "Add a Cloudflare Workers AI API token in Settings."
                : $"Add a {ProviderNames[provider]} API key in Settings.");
        }
        return value;
    }

    private async Task<string> CloudflareAsync(
        IPlyphSettings settings,
        string text,
        string prompt,
        string model,
        string inputMode,
        long outputLimit,
        CancellationToken cancellationToken)
    {
        var key = await RequiredKeyAsync(settings, "cloudflare", cancellationToken).ConfigureAwait(false);
        var accountId = settings.GetString("cloudflare-account-id").Trim();
        if (string.IsNullOrEmpty(accountId))
            throw new InvalidOperationException("Add your Cloudflare Account ID in Settings.");

        var result = await RequestJsonAsync(
            $"https://api.cloudflare.com/client/v4/accounts/{Uri.EscapeDataString(accountId)}/ai/v1/chat/completions",
            new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" },
            OpenAiBody(model, prompt, text, inputMode, outputLimit),
            cancellationToken).ConfigureAwait(false);

        return OutputOrError(result, "cloudflare", model, inputMode);
    }

    private async Task<string> OllamaAsync(
        IPlyphSettings settings,
        string text,
        string prompt,
        string model,
        string inputMode,
        long outputLimit,
        CancellationToken cancellationToken)
    {
        var baseUrl = settings.GetString("ollama-url").TrimEnd('/');
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = BuildMessages(prompt, text, inputMode),
            ["stream"] = false
        };
        if (outputLimit > 0)
        {
            body["options"] = new JsonObject { ["num_predict"] = outputLimit };
        }

        var result = await RequestJsonAsync($"{baseUrl}/api/chat", new Dictionary<string, string>(), body, cancellationToken).ConfigureAwait(false);
        if (result.Data.ValueKind == JsonValueKind.Object)
        {
            if (result.Data.TryGetProperty("done_reason", out var dr) &&
                dr.ValueKind == JsonValueKind.String &&
                dr.GetString() == "length")
            {
                throw OutputLimitError();
            }

            if (result.Data.TryGetProperty("message", out var msg) &&
                msg.ValueKind == JsonValueKind.Object &&
                msg.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                var output = (content.GetString() ?? "").Trim();
                if (!string.IsNullOrEmpty(output))
                    return CleanOutput(output, inputMode);
            }
        }

        throw ProviderError(result, "ollama", model);
    }

    private async Task<string> CompatibleAsync(
        IPlyphSettings settings,
        ProviderProfile info,
        string text,
        string prompt,
        string model,
        string inputMode,
        long outputLimit,
        CancellationTokenSource cts)
    {
        var profileInput = info.Clone();
        profileInput.Model = model;
        var profile = ProvidersStore.ValidateProvider(profileInput);

        var key = await SecretsStore.GetApiKeyAsync(settings, _secretBackend, info.Id, cts.Token, interactive: true).ConfigureAwait(false);
        cts.Token.ThrowIfCancellationRequested();

        var defaults = !string.IsNullOrEmpty(key)
            ? new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = $"Bearer {key}" }
            : new Dictionary<string, string>(StringComparer.Ordinal);

        var customHeaders = info.HasCustomHeaders == true
            ? await SecretsStore.GetProviderHeadersAsync(_secretBackend, info, cts.Token, interactive: true).ConfigureAwait(false)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        cts.Token.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(key) && customHeaders.Count == 0 &&
            !string.IsNullOrEmpty(info.Adapter) && CloudPresetAdapters.Contains(info.Adapter))
        {
            throw new InvalidOperationException($"Add a {info.Name} API key in Settings.");
        }

        if (info.Adapter == "openrouter")
            defaults["X-Title"] = "Plyph";

        var body = OpenAiBody(model, prompt, text, inputMode, outputLimit, info.Adapter == "cerebras");
        if (info.Adapter == "groq" && model.StartsWith("openai/gpt-oss-", StringComparison.Ordinal))
        {
            body["reasoning_effort"] = "low";
            body["include_reasoning"] = false;
        }

        ProvidersStore.ApplyProviderOptions(profile, body);
        var headers = ProvidersStore.MergeProviderHeaders(defaults, customHeaders);
        var result = await RequestJsonAsync($"{profile.Url}/chat/completions", headers, body, cts.Token).ConfigureAwait(false);
        return OutputOrError(result, info.Id, model, inputMode, info.Name);
    }

    private async Task<string> GeminiAsync(
        IPlyphSettings settings,
        string text,
        string prompt,
        string model,
        string inputMode,
        long explicitOutputLimit,
        CancellationToken cancellationToken)
    {
        var key = await RequiredKeyAsync(settings, "gemini", cancellationToken).ConfigureAwait(false);
        var encodedModel = Uri.EscapeDataString(model);
        var body = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray
                    {
                        new JsonObject { ["text"] = inputMode == "prompt" ? text : Payload(text) }
                    }
                }
            }
        };

        if (explicitOutputLimit > 0)
        {
            body["generationConfig"] = new JsonObject { ["maxOutputTokens"] = explicitOutputLimit };
        }
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray
                {
                    new JsonObject { ["text"] = prompt }
                }
            };
        }

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{encodedModel}:generateContent?key={Uri.EscapeDataString(key)}";
        var result = await RequestJsonAsync(url, new Dictionary<string, string>(), body, cancellationToken).ConfigureAwait(false);

        if (result.Data.ValueKind == JsonValueKind.Object &&
            result.Data.TryGetProperty("candidates", out var candidates) &&
            candidates.ValueKind == JsonValueKind.Array &&
            candidates.GetArrayLength() > 0)
        {
            var cand = candidates[0];
            if (cand.ValueKind == JsonValueKind.Object)
            {
                if (cand.TryGetProperty("finishReason", out var fr) &&
                    fr.ValueKind == JsonValueKind.String &&
                    fr.GetString() == "MAX_TOKENS")
                {
                    long thoughtsTokens = 0;
                    if (explicitOutputLimit > 0 &&
                        result.Data.TryGetProperty("usageMetadata", out var um) &&
                        um.ValueKind == JsonValueKind.Object &&
                        um.TryGetProperty("thoughtsTokenCount", out var ttc) &&
                        ttc.ValueKind == JsonValueKind.Number &&
                        ttc.TryGetInt64(out var parsedTtc))
                    {
                        thoughtsTokens = parsedTtc;
                    }
                    throw OutputLimitError(thoughtsTokens);
                }

                if (cand.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.Object &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var p in parts.EnumerateArray())
                    {
                        if (p.ValueKind == JsonValueKind.Object &&
                            p.TryGetProperty("text", out var tProp) &&
                            tProp.ValueKind == JsonValueKind.String)
                        {
                            sb.Append(tProp.GetString());
                        }
                    }
                    var output = sb.ToString().Trim();
                    if (!string.IsNullOrEmpty(output))
                        return CleanOutput(output, inputMode);
                }
            }
        }

        throw ProviderError(result, "gemini", model);
    }
}
