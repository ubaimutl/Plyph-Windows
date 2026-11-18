using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Plyph.Core;

public sealed record ModelItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

public static class ModelsClient
{
    private static readonly Regex OpenAiModelPrefixRegex = new(@"^(gpt-|o\d)", RegexOptions.Compiled);
    private static readonly Regex OpenAiExcludedModelRegex = new(@"(audio|image|realtime|search|transcribe|tts)", RegexOptions.Compiled);
    private static readonly Regex GroqExcludedModelRegex = new(@"(guard|whisper|tts)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static int _requestGeneration;
    private static readonly object Sync = new();
    private static readonly HashSet<CancellationTokenSource> ActiveRequests = new();
    private static readonly HashSet<ChatGptClient> ActiveChatGptClients = new();

    public static void AbortModelRequests()
    {
        List<CancellationTokenSource> requests;
        List<ChatGptClient> clients;
        lock (Sync)
        {
            _requestGeneration++;
            requests = ActiveRequests.ToList();
            ActiveRequests.Clear();
            clients = ActiveChatGptClients.ToList();
            ActiveChatGptClients.Clear();
        }
        foreach (var cts in requests)
        {
            try { cts.Cancel(); } catch { }
        }
        foreach (var client in clients)
        {
            try { client.Destroy(); } catch { }
        }
    }

    private static Exception RequestError(int status, string provider, string? detail)
    {
        if (status is 401 or 403)
        {
            return new InvalidOperationException(provider == "Cloudflare Workers AI"
                ? "Cloudflare rejected the API token or Account ID. Check them and try again."
                : $"{provider} rejected the API key. Check it and try again.");
        }
        if (status == 404)
            return new InvalidOperationException($"{provider} model list is unavailable.");
        if (status == 408)
            return new InvalidOperationException($"{provider} timed out. Try again.");
        if (status == 429)
            return new InvalidOperationException($"{provider} rate limit reached. Wait and try again.");
        if (status >= 500)
            return new InvalidOperationException($"{provider} is temporarily unavailable ({status}).");

        return new InvalidOperationException(!string.IsNullOrEmpty(detail)
            ? detail
            : $"{provider} rejected the request ({status}).");
    }

    private static async Task<JsonElement> GetJsonAsync(
        string url,
        IDictionary<string, string> headers,
        string provider,
        CancellationToken cancellationToken,
        Func<string, IDictionary<string, string>, CancellationToken, Task<(int StatusCode, string Body)>>? httpGetter = null)
    {
        int statusCode;
        string text;

        try
        {
            if (httpGetter != null)
            {
                (statusCode, text) = await httpGetter(url, headers, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                foreach (var (name, val) in headers)
                    req.Headers.TryAddWithoutValidation(name, val);
                using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
                statusCode = (int)resp.StatusCode;
                text = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{provider} timed out. Try again.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException($"Could not connect to {provider}.");
        }

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch
        {
            if (statusCode < 400)
                throw new InvalidOperationException($"Invalid response ({statusCode})");
        }

        using (doc)
        {
            var data = doc?.RootElement ?? default;
            if (statusCode < 200 || statusCode >= 300)
            {
                string? detail = null;
                if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var errProp))
                {
                    if (errProp.ValueKind == JsonValueKind.String)
                        detail = errProp.GetString();
                    else if (errProp.ValueKind == JsonValueKind.Object &&
                             errProp.TryGetProperty("message", out var msgProp) &&
                             msgProp.ValueKind == JsonValueKind.String)
                        detail = msgProp.GetString();
                }
                throw RequestError(statusCode, provider, detail);
            }
            return data.ValueKind != JsonValueKind.Undefined ? data.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    private static List<ModelItem> UniqueModels(IEnumerable<ModelItem> models)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return models
            .Where(m => !string.IsNullOrEmpty(m.Id) && seen.Add(m.Id))
            .Select(m => new ModelItem(m.Id, string.IsNullOrEmpty(m.Name) ? m.Id : m.Name))
            .OrderBy(m => m.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public static async Task<List<ModelItem>> FetchModelsAsync(
        IPlyphSettings settings,
        ISecretBackend secretBackend,
        string provider,
        bool interactive = false,
        Func<string, IDictionary<string, string>, CancellationToken, Task<(int StatusCode, string Body)>>? httpGetter = null)
    {
        var info = ProvidersStore.ResolveProvider(settings, provider);
        int generation;
        var cts = new CancellationTokenSource();
        lock (Sync)
        {
            generation = _requestGeneration;
            ActiveRequests.Add(cts);
        }

        void EnsureCurrent()
        {
            lock (Sync)
            {
                if (generation != _requestGeneration || cts.IsCancellationRequested)
                    throw new InvalidOperationException("Request cancelled");
            }
        }

        try
        {
            if (provider == "chatgpt")
            {
                var client = new ChatGptClient(settings, secretBackend);
                lock (Sync) { ActiveChatGptClients.Add(client); }
                try
                {
                    var chatgptModels = await client.ModelsAsync(cts.Token).ConfigureAwait(false);
                    EnsureCurrent();
                    return chatgptModels;
                }
                finally
                {
                    lock (Sync) { ActiveChatGptClients.Remove(client); }
                    client.Destroy();
                }
            }

            async Task<string> RequiredKeyAsync(string provId)
            {
                var key = await SecretsStore.GetApiKeyAsync(settings, secretBackend, provId, cts.Token, interactive).ConfigureAwait(false);
                if (string.IsNullOrEmpty(key))
                {
                    throw new InvalidOperationException(provId == "cloudflare"
                        ? "Add a Cloudflare Workers AI API token first."
                        : $"Add a {ProvidersStore.ResolveProvider(settings, provId).Name} API key first.");
                }
                return key;
            }

            var models = new List<ModelItem>();

            if (provider == "ollama")
            {
                var baseUrl = settings.GetString("ollama-url").TrimEnd('/');
                var data = await GetJsonAsync($"{baseUrl}/api/tags", new Dictionary<string, string>(), "Ollama", cts.Token, httpGetter).ConfigureAwait(false);
                if (data.TryGetProperty("models", out var mArr) && mArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in mArr.EnumerateArray())
                    {
                        var name = m.TryGetProperty("name", out var nProp) && nProp.ValueKind == JsonValueKind.String ? nProp.GetString() ?? "" : "";
                        var id = m.TryGetProperty("model", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString() ?? name : name;
                        models.Add(new ModelItem(id, name));
                    }
                }
            }
            else if (provider == "cloudflare")
            {
                var key = await RequiredKeyAsync("cloudflare").ConfigureAwait(false);
                var accountId = settings.GetString("cloudflare-account-id").Trim();
                if (string.IsNullOrEmpty(accountId))
                    throw new InvalidOperationException("Add your Cloudflare Account ID first.");
                EnsureCurrent();

                var url = $"https://api.cloudflare.com/client/v4/accounts/{Uri.EscapeDataString(accountId)}/ai/models/search?task={Uri.EscapeDataString("Text Generation")}&hide_experimental=true&per_page=100";
                var data = await GetJsonAsync(url, new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" }, "Cloudflare Workers AI", cts.Token, httpGetter).ConfigureAwait(false);
                if (data.TryGetProperty("result", out var resArr) && resArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in resArr.EnumerateArray())
                    {
                        var id = m.TryGetProperty("name", out var nProp) && nProp.ValueKind == JsonValueKind.String
                            ? nProp.GetString()
                            : (m.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : null);
                        if (!string.IsNullOrEmpty(id))
                            models.Add(new ModelItem(id, id));
                    }
                }
            }
            else if (provider == "gemini")
            {
                var key = await RequiredKeyAsync("gemini").ConfigureAwait(false);
                EnsureCurrent();

                var url = $"https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000&key={Uri.EscapeDataString(key)}";
                var data = await GetJsonAsync(url, new Dictionary<string, string>(), "Gemini", cts.Token, httpGetter).ConfigureAwait(false);
                if (data.TryGetProperty("models", out var mArr) && mArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in mArr.EnumerateArray())
                    {
                        var supportsGen = m.TryGetProperty("supportedGenerationMethods", out var sgm) &&
                                          sgm.ValueKind == JsonValueKind.Array &&
                                          sgm.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "generateContent");
                        if (!supportsGen)
                            continue;

                        var rawName = m.TryGetProperty("name", out var nProp) && nProp.ValueKind == JsonValueKind.String ? nProp.GetString() ?? "" : "";
                        var id = rawName.StartsWith("models/", StringComparison.Ordinal) ? rawName["models/".Length..] : rawName;
                        var displayName = m.TryGetProperty("displayName", out var dnProp) && dnProp.ValueKind == JsonValueKind.String ? dnProp.GetString() ?? id : id;
                        models.Add(new ModelItem(id, displayName));
                    }
                }
            }
            else
            {
                var profile = ProvidersStore.ValidateProvider(info, requireModel: false);
                var key = await SecretsStore.GetApiKeyAsync(settings, secretBackend, info.Id, cts.Token, interactive).ConfigureAwait(false);
                var customHeaders = info.HasCustomHeaders == true
                    ? await SecretsStore.GetProviderHeadersAsync(secretBackend, info, cts.Token, interactive).ConfigureAwait(false)
                    : new Dictionary<string, string>();
                EnsureCurrent();

                var suffix = info.Adapter == "openrouter" ? "/models?output_modalities=text" : "/models";
                var defaultHeaders = !string.IsNullOrEmpty(key)
                    ? new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" }
                    : new Dictionary<string, string>();
                var headers = ProvidersStore.MergeProviderHeaders(defaultHeaders, customHeaders);
                var data = await GetJsonAsync($"{profile.Url}{suffix}", headers, info.Name, cts.Token, httpGetter).ConfigureAwait(false);

                if (data.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in dataArr.EnumerateArray())
                    {
                        if (info.Adapter == "bai" &&
                            m.TryGetProperty("supported_endpoint_types", out var setProp) &&
                            setProp.ValueKind == JsonValueKind.Array &&
                            !setProp.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "openai"))
                        {
                            continue;
                        }

                        if (m.TryGetProperty("type", out var typeProp) &&
                            typeProp.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrEmpty(typeProp.GetString()) &&
                            typeProp.GetString() != "language")
                        {
                            continue;
                        }

                        var id = m.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString() ?? "" : "";
                        var name = m.TryGetProperty("name", out var nProp) && nProp.ValueKind == JsonValueKind.String ? nProp.GetString() ?? id : id;
                        if (!string.IsNullOrEmpty(id))
                            models.Add(new ModelItem(id, name));
                    }
                }

                if (info.Adapter == "openai")
                {
                    models = models.Where(m =>
                        OpenAiModelPrefixRegex.IsMatch(m.Id) &&
                        !OpenAiExcludedModelRegex.IsMatch(m.Id)).ToList();
                }
                else if (info.Adapter == "groq")
                {
                    models = models.Where(m => !GroqExcludedModelRegex.IsMatch(m.Id)).ToList();
                }
            }

            EnsureCurrent();
            return UniqueModels(models);
        }
        finally
        {
            lock (Sync)
            {
                ActiveRequests.Remove(cts);
            }
            cts.Dispose();
        }
    }
}
