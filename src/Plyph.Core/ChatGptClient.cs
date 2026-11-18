using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Plyph.Core;

public sealed class ChatGptAccountProfile
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = "";

    [JsonPropertyName("subject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subject { get; set; }

    [JsonPropertyName("email")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Email { get; set; }

    [JsonPropertyName("access_token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("id_token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IdToken { get; set; }

    [JsonPropertyName("expires_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ExpiresAt { get; set; }

    [JsonPropertyName("scopes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Scopes { get; set; }
}

public sealed class ChatGptCredentialRecord
{
    [JsonPropertyName("host_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HostId { get; set; }

    [JsonPropertyName("profiles")]
    public Dictionary<string, ChatGptAccountProfile> Profiles { get; set; } = new(StringComparer.Ordinal);
}

public sealed record ChatGptAccountSummary(string Id, string Label, bool Connected, bool Authorized);

public sealed class ChatGptClient : IDisposable
{
    public const string SecretStoreKey = "dev.ubai.Plyph.ChatGPT";
    private static readonly SemaphoreSlim CredentialGate = new(1, 1);

    private IPlyphSettings? _settings;
    private readonly ISecretBackend _secretBackend;
    private HttpClient? _httpClient;
    private readonly HashSet<CancellationTokenSource> _pending = new();
    private readonly object _sync = new();

    public Func<string, object?, CancellationToken, string?, bool, Task<JsonElement>>? JsonRequesterOverride { get; set; }
    public Func<string, JsonObject, string, CancellationToken, Task<(int StatusCode, Stream Stream, Action OnClose)>>? StreamRequesterOverride { get; set; }

    public ChatGptClient(IPlyphSettings settings, ISecretBackend secretBackend, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _secretBackend = secretBackend;
        _httpClient = handler != null
            ? new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(90) }
            : new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
    }

    public void Destroy() => Dispose();

    public void Dispose()
    {
        List<CancellationTokenSource> toCancel;
        lock (_sync)
        {
            toCancel = _pending.ToList();
            _pending.Clear();
            _httpClient?.Dispose();
            _httpClient = null;
            _settings = null;
        }
        foreach (var cts in toCancel)
        {
            try { cts.Cancel(); } catch { }
        }
    }

    private async Task<T> RunOperationAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken external = default)
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            if (_httpClient == null || _settings == null)
                throw new InvalidOperationException("The ChatGPT connection was closed.");
            cts = CancellationTokenSource.CreateLinkedTokenSource(external);
            _pending.Add(cts);
        }

        try
        {
            return await action(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _pending.Remove(cts);
            }
            cts.Dispose();
        }
    }

    private static async Task<T> WithCredentialsLockAsync<T>(CancellationToken cancellationToken, Func<Task<T>> action)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await CredentialGate.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Another ChatGPT connection update is in progress. Try again.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await action().ConfigureAwait(false);
        }
        finally
        {
            CredentialGate.Release();
        }
    }

    private async Task<ChatGptCredentialRecord> ReadCredentialsAsync(CancellationToken cancellationToken)
    {
        var raw = await _secretBackend.LookupSecretAsync(SecretStoreKey, interactive: true, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
            return new ChatGptCredentialRecord();

        try
        {
            var record = JsonSerializer.Deserialize<ChatGptCredentialRecord>(raw);
            if (record?.Profiles == null)
                throw new InvalidOperationException();
            return record;
        }
        catch
        {
            throw new InvalidOperationException("The saved ChatGPT connection is invalid. Remove the Plyph ChatGPT item in Windows Credential Manager.");
        }
    }

    private async Task SaveCredentialsAsync(ChatGptCredentialRecord record, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(record);
        await _secretBackend.StoreSecretAsync(SecretStoreKey, json, "Plyph ChatGPT accounts", cancellationToken).ConfigureAwait(false);
    }

    private static string RandomToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return ChatGptProtocol.Base64Url(bytes);
    }

    private static string FormEncode(IEnumerable<KeyValuePair<string, string>> values)
    {
        return string.Join("&", values.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
    }

    private static void ValidateServiceUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" ||
            (uri.Host != "auth.openai.com" && uri.Host != "api.openai.com") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (uri.Port != -1 && uri.Port != 443))
        {
            throw new InvalidOperationException("Invalid ChatGPT service address.");
        }
    }

    public async Task<JsonElement> RequestJsonAsync(
        string url,
        object? data,
        CancellationToken cancellationToken,
        string? token = null,
        bool formEncoded = false)
    {
        if (JsonRequesterOverride != null)
            return await JsonRequesterOverride(url, data, cancellationToken, token, formEncoded).ConfigureAwait(false);

        ValidateServiceUri(url);
        var client = _httpClient ?? throw new InvalidOperationException("The ChatGPT connection was closed.");

        using var req = new HttpRequestMessage(data == null ? HttpMethod.Get : HttpMethod.Post, url);
        if (!string.IsNullOrEmpty(token))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        if (data != null)
        {
            if (formEncoded && data is IEnumerable<KeyValuePair<string, string>> kvps)
            {
                req.Content = new StringContent(FormEncode(kvps), Encoding.UTF8, "application/x-www-form-urlencoded");
            }
            else
            {
                var jsonText = data is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(data);
                req.Content = new StringContent(jsonText, Encoding.UTF8, "application/json");
            }
        }

        using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var status = (int)resp.StatusCode;
        var text = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        JsonDocument doc;
        try
        {
            doc = string.IsNullOrWhiteSpace(text)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(text);
        }
        catch
        {
            throw ChatGptProtocol.ChatGptError(null, status);
        }

        using (doc)
        {
            if (status < 200 || status >= 300)
            {
                string? code = null;
                if (doc.RootElement.TryGetProperty("error", out var errProp))
                {
                    if (errProp.ValueKind == JsonValueKind.String)
                        code = errProp.GetString();
                    else if (errProp.ValueKind == JsonValueKind.Object &&
                             errProp.TryGetProperty("code", out var codeProp) &&
                             codeProp.ValueKind == JsonValueKind.String)
                        code = codeProp.GetString();
                }
                throw ChatGptProtocol.ChatGptError(code, status);
            }

            return doc.RootElement.Clone();
        }
    }

    private async Task<IdTokenClaims> ValidateTokensAsync(
        JsonElement tokens,
        string clientId,
        string? nonce,
        string? subject,
        CancellationToken cancellationToken)
    {
        var jwksEl = await RequestJsonAsync($"{ChatGptProtocol.Issuer}/.well-known/jwks.json", null, cancellationToken).ConfigureAwait(false);
        var keys = new List<JwkKey>();
        if (jwksEl.TryGetProperty("keys", out var keysArr) && keysArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var k in keysArr.EnumerateArray())
            {
                var parsed = JsonSerializer.Deserialize<JwkKey>(k.GetRawText());
                if (parsed != null)
                    keys.Add(parsed);
            }
        }

        var idToken = tokens.TryGetProperty("id_token", out var idTokProp) && idTokProp.ValueKind == JsonValueKind.String
            ? idTokProp.GetString()
            : null;
        return ChatGptProtocol.VerifyIdToken(idToken, keys, clientId, nonce, subject);
    }

    private static ChatGptAccountProfile BuildTokenProfile(
        JsonElement tokens,
        ChatGptAccountProfile previous,
        IdTokenClaims? identity = null)
    {
        List<string> scopes;
        if (tokens.TryGetProperty("scope", out var scopeProp) && scopeProp.ValueKind == JsonValueKind.String)
        {
            scopes = (scopeProp.GetString() ?? "")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }
        else
        {
            scopes = previous.Scopes?.ToList() ?? new List<string>();
        }

        var tokenType = tokens.TryGetProperty("token_type", out var ttProp) && ttProp.ValueKind == JsonValueKind.String
            ? ttProp.GetString()
            : null;
        var accessToken = tokens.TryGetProperty("access_token", out var atProp) && atProp.ValueKind == JsonValueKind.String
            ? atProp.GetString()
            : null;
        var expiresIn = tokens.TryGetProperty("expires_in", out var expProp) && expProp.ValueKind == JsonValueKind.Number
            ? expProp.GetDouble()
            : double.NaN;
        var refreshToken = tokens.TryGetProperty("refresh_token", out var rtProp) && rtProp.ValueKind == JsonValueKind.String
            ? rtProp.GetString()
            : previous.RefreshToken;
        var idToken = tokens.TryGetProperty("id_token", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString()
            : previous.IdToken;

        if (!string.Equals(tokenType, "bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(accessToken) ||
            !double.IsFinite(expiresIn) || expiresIn <= 0 ||
            string.IsNullOrEmpty(refreshToken))
        {
            throw new InvalidOperationException("ChatGPT returned an incomplete connection. Please sign in again.");
        }

        var email = identity?.EmailVerified == false
            ? ""
            : identity?.Email ?? previous.Email ?? "";

        return new ChatGptAccountProfile
        {
            ClientId = previous.ClientId,
            Subject = identity?.Sub ?? previous.Subject,
            Email = email,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            IdToken = idToken,
            ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + expiresIn,
            Scopes = scopes
        };
    }

    public Task<List<ChatGptAccountSummary>> AccountsAsync(CancellationToken external = default)
    {
        return RunOperationAsync(async cancellationToken =>
        {
            var record = await ReadCredentialsAsync(cancellationToken).ConfigureAwait(false);
            return record.Profiles.Select(kvp =>
            {
                var id = kvp.Key;
                var profile = kvp.Value;
                var suffix = id.Length <= 6 ? id : id[^6..];
                var label = $"{(!string.IsNullOrEmpty(profile.Email) ? profile.Email : "ChatGPT account")} · {suffix}";
                var connected = !string.IsNullOrEmpty(profile.RefreshToken);
                var authorized = profile.Scopes?.Contains("chatgpt.tokens.use.direct") == true;
                return new ChatGptAccountSummary(id, label, connected, authorized);
            }).ToList();
        }, external);
    }

    public Task<(string Id, bool Authorized)> SignInAsync(
        string? clientId = null,
        CancellationToken external = default,
        Action<string>? browserLauncher = null)
    {
        return RunOperationAsync(async cancellationToken =>
        {
            var prepared = await WithCredentialsLockAsync(cancellationToken, async () =>
            {
                var record = await ReadCredentialsAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(record.HostId))
                {
                    record.HostId = $"urn:uuid:{Guid.NewGuid():D}";
                    await SaveCredentialsAsync(record, cancellationToken).ConfigureAwait(false);
                }

                ChatGptAccountProfile? prev = null;
                if (!string.IsNullOrEmpty(clientId) && !record.Profiles.TryGetValue(clientId, out prev))
                    throw new InvalidOperationException("Select a saved ChatGPT account or add a new account.");

                return (Host: record.HostId, Previous: prev);
            }).ConfigureAwait(false);

            var state = RandomToken();
            var nonce = RandomToken();
            var verifier = RandomToken();

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirectUri = $"http://127.0.0.1:{port}/auth/callback";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(180));

            var queryParams = new Dictionary<string, string>
            {
                ["client_id"] = clientId ?? "dynamic_agent_client",
                ["ext_agent_host_id"] = prepared.Host,
                ["response_type"] = "code",
                ["redirect_uri"] = redirectUri,
                ["scope"] = ChatGptProtocol.Scopes,
                ["resource"] = ChatGptProtocol.Resource,
                ["state"] = state,
                ["nonce"] = nonce,
                ["code_challenge_method"] = "S256",
                ["code_challenge"] = ChatGptProtocol.Base64Url(ChatGptProtocol.Sha256(Encoding.UTF8.GetBytes(verifier)))
            };

            if (string.IsNullOrEmpty(clientId))
                queryParams["agent_name_hint"] = "Plyph";
            else if (!string.IsNullOrEmpty(prepared.Previous?.Email))
                queryParams["login_hint"] = prepared.Previous!.Email!;

            var authUrl = $"{ChatGptProtocol.Issuer}/api/accounts/authorize?{FormEncode(queryParams)}";
            try
            {
                if (browserLauncher != null)
                    browserLauncher(authUrl);
                else
                    Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
            }
            catch
            {
                listener.Stop();
                throw new InvalidOperationException("Could not open your browser.");
            }

            (string ClientId, string Code) callback;
            try
            {
                callback = await WaitForOAuthCallbackAsync(listener, port, state, clientId, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("ChatGPT sign-in timed out. Try again.");
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("ChatGPT sign-in cancelled.");
            }
            finally
            {
                listener.Stop();
            }

            var tokenRequest = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = callback.ClientId,
                ["code"] = callback.Code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirectUri,
                ["resource"] = ChatGptProtocol.Resource
            };

            var tokens = await RequestJsonAsync(
                $"{ChatGptProtocol.Issuer}/api/accounts/oauth/token",
                tokenRequest,
                cancellationToken,
                token: null,
                formEncoded: true).ConfigureAwait(false);

            var identity = await ValidateTokensAsync(
                tokens,
                callback.ClientId,
                nonce,
                prepared.Previous?.Subject,
                cancellationToken).ConfigureAwait(false);

            var profile = BuildTokenProfile(tokens, new ChatGptAccountProfile { ClientId = callback.ClientId }, identity);

            await WithCredentialsLockAsync(cancellationToken, async () =>
            {
                var record = await ReadCredentialsAsync(cancellationToken).ConfigureAwait(false);
                if (record.Profiles.TryGetValue(callback.ClientId, out var existing) &&
                    !string.IsNullOrEmpty(existing.Subject) &&
                    existing.Subject != profile.Subject)
                {
                    throw new InvalidOperationException("The ChatGPT account identity changed. Add it as a new account.");
                }

                record.Profiles[callback.ClientId] = profile;
                await SaveCredentialsAsync(record, cancellationToken).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);

            var authorized = profile.Scopes?.Contains("chatgpt.tokens.use.direct") == true;
            return (callback.ClientId, authorized);
        }, external);
    }

    private static async Task<(string ClientId, string Code)> WaitForOAuthCallbackAsync(
        TcpListener listener,
        int port,
        string state,
        string? clientId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(requestLine))
                continue;

            var parts = requestLine.Split(' ');
            var method = parts.Length > 0 ? parts[0] : "";
            var rawTarget = parts.Length > 1 ? parts[1] : "/";

            string? hostHeader = null;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
            {
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                    hostHeader = line[5..].Trim();
            }

            var qIdx = rawTarget.IndexOf('?');
            var path = qIdx >= 0 ? rawTarget[..qIdx] : rawTarget;
            var queryString = qIdx >= 0 ? rawTarget[(qIdx + 1)..] : "";
            var query = ParseQueryString(queryString);

            var valid = method == "GET" &&
                        path == "/auth/callback" &&
                        query.TryGetValue("state", out var qState) &&
                        qState == state &&
                        hostHeader == $"127.0.0.1:{port}";

            var bodyText = valid
                ? "Return to Plyph to finish connecting your ChatGPT account."
                : "Invalid sign-in callback.";
            var bodyBytes = Encoding.UTF8.GetBytes(bodyText);
            var statusHeader = valid ? "200 OK" : "400 Bad Request";
            var responseHeader =
                $"HTTP/1.1 {statusHeader}\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                "Cache-Control: no-store\r\n" +
                "Content-Security-Policy: default-src 'none'\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n\r\n";

            var headerBytes = Encoding.ASCII.GetBytes(responseHeader);
            await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (!valid)
                continue;

            return ChatGptProtocol.CallbackResult(query, state, clientId);
        }
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
            return dict;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx >= 0)
            {
                var k = Uri.UnescapeDataString(pair[..idx].Replace('+', ' '));
                var v = Uri.UnescapeDataString(pair[(idx + 1)..].Replace('+', ' '));
                dict[k] = v;
            }
            else
            {
                dict[Uri.UnescapeDataString(pair)] = "";
            }
        }
        return dict;
    }

    public async Task<(string Id, string Token)> GetAccessAsync(CancellationToken cancellationToken)
    {
        var settings = _settings ?? throw new InvalidOperationException("The ChatGPT connection was closed.");
        var id = settings.GetString("chatgpt-account-id");
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException("Continue with ChatGPT in Plyph settings first.");

        return await WithCredentialsLockAsync(cancellationToken, async () =>
        {
            var record = await ReadCredentialsAsync(cancellationToken).ConfigureAwait(false);
            if (!record.Profiles.TryGetValue(id, out var profile) || string.IsNullOrEmpty(profile.RefreshToken))
                throw new InvalidOperationException("Continue with ChatGPT in Plyph settings to reconnect.");

            if (profile.Scopes?.Contains("chatgpt.tokens.use.direct") != true)
                throw new InvalidOperationException("ChatGPT plan access was not granted. Continue with ChatGPT and allow plan usage.");

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            if (string.IsNullOrEmpty(profile.AccessToken) || !profile.ExpiresAt.HasValue || profile.ExpiresAt.Value < now + 60)
            {
                try
                {
                    var refreshData = new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = id,
                        ["refresh_token"] = profile.RefreshToken,
                        ["resource"] = ChatGptProtocol.Resource
                    };
                    var tokens = await RequestJsonAsync(
                        $"{ChatGptProtocol.Issuer}/api/accounts/oauth/token",
                        refreshData,
                        cancellationToken,
                        token: null,
                        formEncoded: true).ConfigureAwait(false);

                    IdTokenClaims? identity = null;
                    if (tokens.TryGetProperty("id_token", out var idTok) && idTok.ValueKind == JsonValueKind.String)
                        identity = await ValidateTokensAsync(tokens, id, null, profile.Subject, cancellationToken).ConfigureAwait(false);

                    profile = BuildTokenProfile(tokens, profile, identity);
                    record.Profiles[id] = profile;
                    await SaveCredentialsAsync(record, cancellationToken).ConfigureAwait(false);
                }
                catch (ChatGptServiceException ex) when (ex.OAuthCode == "invalid_grant")
                {
                    record.Profiles[id] = new ChatGptAccountProfile
                    {
                        ClientId = id,
                        Subject = profile.Subject,
                        Email = profile.Email
                    };
                    await SaveCredentialsAsync(record, cancellationToken).ConfigureAwait(false);
                    throw;
                }
            }

            if (profile.Scopes?.Contains("chatgpt.tokens.use.direct") != true)
                throw new InvalidOperationException("ChatGPT plan access is not enabled for this connection.");

            return (id, profile.AccessToken!);
        }).ConfigureAwait(false);
    }

    public Task<bool> SignOutAsync(string id, CancellationToken external = default)
    {
        return RunOperationAsync(cancellationToken => WithCredentialsLockAsync(cancellationToken, async () =>
        {
            var record = await ReadCredentialsAsync(cancellationToken).ConfigureAwait(false);
            if (!record.Profiles.TryGetValue(id, out var profile))
                return true;

            var revoked = string.IsNullOrEmpty(profile.RefreshToken);
            if (!string.IsNullOrEmpty(profile.RefreshToken))
            {
                try
                {
                    var discovery = await RequestJsonAsync(
                        $"{ChatGptProtocol.Issuer}/.well-known/openid-configuration",
                        null,
                        cancellationToken).ConfigureAwait(false);

                    var revEndpoint = discovery.TryGetProperty("revocation_endpoint", out var revProp) &&
                                      revProp.ValueKind == JsonValueKind.String
                        ? revProp.GetString()
                        : null;

                    if (string.IsNullOrEmpty(revEndpoint) ||
                        !revEndpoint.StartsWith($"{ChatGptProtocol.Issuer}/", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Invalid revocation endpoint.");
                    }

                    await RequestJsonAsync(
                        revEndpoint,
                        new Dictionary<string, string>
                        {
                            ["token"] = profile.RefreshToken,
                            ["token_type_hint"] = "refresh_token",
                            ["client_id"] = id
                        },
                        cancellationToken,
                        token: null,
                        formEncoded: true).ConfigureAwait(false);

                    revoked = true;
                }
                catch
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            record.Profiles[id] = new ChatGptAccountProfile
            {
                ClientId = id,
                Subject = profile.Subject,
                Email = profile.Email
            };
            await SaveCredentialsAsync(record, cancellationToken).ConfigureAwait(false);
            return revoked;
        }), external);
    }

    public Task<List<ModelItem>> ModelsAsync(CancellationToken external = default)
    {
        return RunOperationAsync(async cancellationToken =>
        {
            var access = await GetAccessAsync(cancellationToken).ConfigureAwait(false);
            var data = await RequestJsonAsync(
                $"{ChatGptProtocol.Resource}/models",
                null,
                cancellationToken,
                access.Token).ConfigureAwait(false);

            if (_settings?.GetString("chatgpt-account-id") != access.Id)
                throw new InvalidOperationException("The ChatGPT account changed. Refresh models again.");

            var result = new List<ModelItem>();
            if (data.TryGetProperty("models", out var modelsArr) && modelsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in modelsArr.EnumerateArray())
                {
                    var visibility = model.TryGetProperty("visibility", out var visProp) && visProp.ValueKind == JsonValueKind.String
                        ? visProp.GetString()
                        : null;
                    var slug = model.TryGetProperty("slug", out var slugProp) && slugProp.ValueKind == JsonValueKind.String
                        ? slugProp.GetString()
                        : null;
                    if (visibility == "list" && !string.IsNullOrEmpty(slug))
                    {
                        var displayName = model.TryGetProperty("display_name", out var dnProp) && dnProp.ValueKind == JsonValueKind.String
                            ? dnProp.GetString() ?? slug
                            : slug;
                        result.Add(new ModelItem(slug, displayName));
                    }
                }
            }
            return result;
        }, external);
    }

    public Task<string> TransformAsync(
        string text,
        string instructions,
        string model,
        CancellationToken external = default)
    {
        if (string.IsNullOrEmpty(model))
            throw new InvalidOperationException("Choose a ChatGPT model in Plyph settings first.");

        return RunOperationAsync(async cancellationToken =>
        {
            var access = await GetAccessAsync(cancellationToken).ConfigureAwait(false);
            var body = new JsonObject
            {
                ["model"] = model,
                ["instructions"] = instructions,
                ["input"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = text
                    }
                },
                ["store"] = false,
                ["stream"] = true
            };

            var effort = _settings?.GetString("chatgpt-thinking-effort") ?? "auto";
            if (effort is "low" or "medium" or "high")
            {
                body["reasoning"] = new JsonObject { ["effort"] = effort };
            }

            var url = $"{ChatGptProtocol.Resource}/responses";
            int statusCode;
            Stream responseStream;
            Action onClose;
            HttpResponseMessage? httpResponse = null;

            if (StreamRequesterOverride != null)
            {
                (statusCode, responseStream, onClose) = await StreamRequesterOverride(url, body, access.Token, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ValidateServiceUri(url);
                var client = _httpClient ?? throw new InvalidOperationException("The ChatGPT connection was closed.");
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {access.Token}");
                req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                httpResponse = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                statusCode = (int)httpResponse.StatusCode;
                responseStream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                onClose = () =>
                {
                    responseStream.Dispose();
                    httpResponse.Dispose();
                };
            }

            try
            {
                var buffer = new byte[8192];
                if (statusCode < 200 || statusCode >= 300)
                {
                    var errBuilder = new StringBuilder();
                    var errDecoder = new Utf8StreamDecoder();
                    while (errBuilder.Length < 65536)
                    {
                        var read = await responseStream.ReadAsync(buffer.AsMemory(0, 4096), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                            break;
                        errBuilder.Append(errDecoder.Decode(buffer.AsSpan(0, read)));
                    }

                    string? errParam = null;
                    string? errCode = null;
                    try
                    {
                        using var errDoc = JsonDocument.Parse(errBuilder.ToString());
                        if (errDoc.RootElement.TryGetProperty("error", out var errObj) && errObj.ValueKind == JsonValueKind.Object)
                        {
                            if (errObj.TryGetProperty("param", out var pProp) && pProp.ValueKind == JsonValueKind.String)
                                errParam = pProp.GetString();
                            if (errObj.TryGetProperty("code", out var cProp) && cProp.ValueKind == JsonValueKind.String)
                                errCode = cProp.GetString();
                        }
                    }
                    catch
                    {
                    }

                    if (statusCode == 400 && (errParam == "reasoning" || errParam == "reasoning.effort"))
                    {
                        throw new InvalidOperationException(
                            "This model could not use the selected thinking effort. Set Thinking effort to Auto in Plyph settings or choose another model.");
                    }

                    throw ChatGptProtocol.ChatGptError(errCode, statusCode);
                }

                var parser = new ResponsesStream();
                var decoder = new Utf8StreamDecoder();
                while (true)
                {
                    var read = await responseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    parser.Feed(decoder.Decode(buffer.AsSpan(0, read)));
                }
                parser.Feed(decoder.Finish());
                cancellationToken.ThrowIfCancellationRequested();

                if (_settings?.GetString("chatgpt-account-id") != access.Id)
                    throw new InvalidOperationException("The ChatGPT account changed. Run the action again.");

                return parser.Finish();
            }
            finally
            {
                onClose();
            }
        }, external);
    }
}
