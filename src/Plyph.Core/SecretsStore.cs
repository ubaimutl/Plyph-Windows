using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Plyph.Core;

public interface ISecretBackend
{
    Task<string?> LookupSecretAsync(string key, bool interactive, CancellationToken cancellationToken = default);
    Task<bool> HasSecretAsync(string key, CancellationToken cancellationToken = default);
    Task StoreSecretAsync(string key, string secret, string label, CancellationToken cancellationToken = default);
    Task ClearSecretAsync(string key, CancellationToken cancellationToken = default);
}

public sealed class InMemorySecretBackend : ISecretBackend
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    public bool IsLocked { get; set; }
    public bool CanUnlock { get; set; } = true;
    public bool FailLookup { get; set; }
    public bool FailClear { get; set; }
    public List<(string Operation, string Key)> Operations { get; } = new();

    public Task<string?> LookupSecretAsync(string key, bool interactive, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add(("lookup", key));
        if (FailLookup)
            throw new IOException("Credential store unavailable");

        if (IsLocked)
        {
            if (!interactive)
                throw new SecretLockedException(key);

            Operations.Add(("unlock", key));
            if (!CanUnlock)
                throw new SecretLockedException(key);
            IsLocked = false;
        }

        return Task.FromResult(_secrets.TryGetValue(key, out var val) ? val : null);
    }

    public Task<bool> HasSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add(("search", key));
        if (FailLookup)
            throw new IOException("Credential store unavailable");
        return Task.FromResult(_secrets.ContainsKey(key));
    }

    public Task StoreSecretAsync(string key, string secret, string label, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add(("store", key));
        if (FailLookup)
            throw new IOException("Credential store unavailable");
        _secrets[key] = secret;
        return Task.CompletedTask;
    }

    public Task ClearSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add(("clear", key));
        if (FailClear)
            throw new IOException("Credential store unavailable");
        _secrets.Remove(key);
        return Task.CompletedTask;
    }
}

public sealed class SecretLockedException : Exception
{
    public SecretLockedException(string provider)
        : base($"Your saved {(provider.EndsWith(":headers", StringComparison.Ordinal) ? "custom headers are" : "API key is")} locked. Unlock Windows Credential Manager and try again.")
    {
    }
}

/// <summary>
/// Stores secrets using Windows Credential Manager in Installed mode, or DPAPI-encrypted local file
/// in Portable mode (leaving zero trace in the host system's Credential Manager when running portable).
/// Falls back gracefully if running on non-Windows during tests.
/// </summary>
public sealed class WindowsSecretBackend : ISecretBackend
{
    private readonly StorageMode _mode;
    private readonly string _secretsFilePath;
    private readonly object _sync = new();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("dev.ubai.Plyph.Windows.v1");

    public WindowsSecretBackend(StorageMode mode, string dataDirectory)
    {
        _mode = mode;
        _secretsFilePath = Path.Combine(dataDirectory, "secrets.dat");
    }

    public Task<string?> LookupSecretAsync(string key, bool interactive, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_mode == StorageMode.Installed && OperatingSystem.IsWindows())
            {
                var target = GetTargetName(key);
                if (Win32CredManager.TryReadCredential(target, out var secret))
                    return Task.FromResult<string?>(secret);
            }

            var map = ReadLocalSecretsLocked();
            return Task.FromResult(map.TryGetValue(key, out var value) ? value : null);
        }
    }

    public Task<bool> HasSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_mode == StorageMode.Installed && OperatingSystem.IsWindows())
            {
                var target = GetTargetName(key);
                if (Win32CredManager.TryReadCredential(target, out _))
                    return Task.FromResult(true);
            }

            var map = ReadLocalSecretsLocked();
            return Task.FromResult(map.ContainsKey(key));
        }
    }

    public Task StoreSecretAsync(string key, string secret, string label, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_mode == StorageMode.Installed && OperatingSystem.IsWindows())
            {
                var target = GetTargetName(key);
                if (Win32CredManager.WriteCredential(target, secret, label))
                    return Task.CompletedTask;
            }

            var map = ReadLocalSecretsLocked();
            map[key] = secret;
            WriteLocalSecretsLocked(map);
            return Task.CompletedTask;
        }
    }

    public Task ClearSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_mode == StorageMode.Installed && OperatingSystem.IsWindows())
            {
                var target = GetTargetName(key);
                Win32CredManager.DeleteCredential(target);
            }

            var map = ReadLocalSecretsLocked();
            if (map.Remove(key))
                WriteLocalSecretsLocked(map);
            return Task.CompletedTask;
        }
    }

    private static string GetTargetName(string key) => $"dev.ubai.Plyph:{key}";

    private Dictionary<string, string> ReadLocalSecretsLocked()
    {
        if (!File.Exists(_secretsFilePath))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var rawBytes = File.ReadAllBytes(_secretsFilePath);
            byte[] plainBytes;
            if (OperatingSystem.IsWindows())
            {
                plainBytes = ProtectedData.Unprotect(rawBytes, Entropy, DataProtectionScope.CurrentUser);
            }
            else
            {
                plainBytes = rawBytes;
            }

            var json = Encoding.UTF8.GetString(plainBytes);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void WriteLocalSecretsLocked(Dictionary<string, string> secrets)
    {
        var dir = Path.GetDirectoryName(_secretsFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(secrets);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var outBytes = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser)
            : plainBytes;

        var tmp = _secretsFilePath + ".tmp";
        File.WriteAllBytes(tmp, outBytes);
        File.Move(tmp, _secretsFilePath, overwrite: true);
    }

    private static class Win32CredManager
    {
        private const uint CRED_TYPE_GENERIC = 1;
        private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite([In] ref CREDENTIAL userCredential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, int flags);

        [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
        private static extern void CredFree([In] IntPtr cred);

        public static bool TryReadCredential(string target, out string secret)
        {
            secret = "";
            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr) || ptr == IntPtr.Zero)
                return false;

            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
                if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
                    return false;
                secret = Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2) ?? "";
                return true;
            }
            finally
            {
                CredFree(ptr);
            }
        }

        public static bool WriteCredential(string target, string secret, string comment)
        {
            var byteCount = Encoding.Unicode.GetByteCount(secret);
            var blob = Marshal.StringToCoTaskMemUni(secret);
            try
            {
                var cred = new CREDENTIAL
                {
                    Type = CRED_TYPE_GENERIC,
                    TargetName = target,
                    Comment = comment,
                    CredentialBlobSize = (uint)byteCount,
                    CredentialBlob = blob,
                    Persist = CRED_PERSIST_LOCAL_MACHINE,
                    UserName = Environment.UserName
                };
                return CredWrite(ref cred, 0);
            }
            finally
            {
                Marshal.FreeCoTaskMem(blob);
            }
        }

        public static void DeleteCredential(string target)
        {
            CredDelete(target, CRED_TYPE_GENERIC, 0);
        }
    }
}

public static class SecretsStore
{
    public static readonly IReadOnlyDictionary<string, string> KeySettings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["cloudflare"] = "cloudflare-api-key",
        ["bai"] = "bai-api-key",
        ["groq"] = "groq-api-key",
        ["gemini"] = "gemini-api-key",
        ["openrouter"] = "openrouter-api-key",
        ["cerebras"] = "cerebras-api-key",
        ["openai"] = "openai-api-key",
        ["openai-compatible"] = "openai-compatible-api-key",
        ["vercel"] = "vercel-api-key"
    };

    private static Exception KeyringError(Exception error)
    {
        if (error is OperationCanceledException or SecretLockedException)
            return error;
        return new InvalidOperationException("Windows Credential Store is unavailable. Check permissions and try again.", error);
    }

    public static async Task<string> GetApiKeyAsync(
        IPlyphSettings settings,
        ISecretBackend backend,
        string provider,
        CancellationToken cancellationToken = default,
        bool interactive = false)
    {
        var hasSetting = KeySettings.TryGetValue(provider, out var setting);
        if (!hasSetting && !provider.StartsWith("custom:", StringComparison.Ordinal))
            throw new InvalidOperationException("Unknown provider credential.");

        string? saved;
        try
        {
            saved = await backend.LookupSecretAsync(provider, interactive, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not SecretLockedException)
        {
            throw KeyringError(ex);
        }

        if (!string.IsNullOrEmpty(saved))
        {
            if (setting != null && !string.IsNullOrEmpty(settings.GetString(setting)))
                settings.SetString(setting, "");
            return saved;
        }

        var legacy = setting != null ? settings.GetString(setting).Trim() : "";
        if (string.IsNullOrEmpty(legacy))
            return "";
        if (!interactive)
            return legacy;

        try
        {
            var label = $"Plyph {provider} {(provider == "cloudflare" ? "API token" : "API key")}";
            await backend.StoreSecretAsync(provider, legacy, label, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw KeyringError(ex);
        }

        if (setting != null)
            settings.SetString(setting, "");
        return legacy;
    }

    public static async Task SetApiKeyAsync(
        IPlyphSettings settings,
        ISecretBackend backend,
        string provider,
        string password,
        CancellationToken cancellationToken = default)
    {
        var hasSetting = KeySettings.TryGetValue(provider, out var setting);
        if (!hasSetting && !provider.StartsWith("custom:", StringComparison.Ordinal))
            throw new InvalidOperationException("Unknown provider credential.");

        var value = (password ?? "").Trim();
        try
        {
            if (!string.IsNullOrEmpty(value))
            {
                var label = $"Plyph {provider} {(provider == "cloudflare" ? "API token" : "API key")}";
                await backend.StoreSecretAsync(provider, value, label, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await backend.ClearSecretAsync(provider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw KeyringError(ex);
        }

        if (setting != null)
            settings.SetString(setting, "");
    }

    public static async Task MigrateProviderSettingsAsync(
        IPlyphSettings settings,
        ISecretBackend backend,
        CancellationToken cancellationToken = default)
    {
        ProvidersStore.MigrateProviders(settings);
        if (settings.GetInt("provider-migration-version") >= 1)
            return;

        var configured = new List<string>();
        foreach (var preset in ProvidersStore.ProviderPresets)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            var hasSecret = await backend.HasSecretAsync(preset.Id, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                return;
            if (hasSecret)
                configured.Add(preset.Id);
        }

        if (backend is InMemorySecretBackend { IsLocked: true })
            return;

        ProvidersStore.MigrateProviders(settings, configured, complete: true);
    }

    public static async Task<Dictionary<string, string>> GetProviderHeadersAsync(
        ISecretBackend backend,
        ProviderProfile profile,
        CancellationToken cancellationToken = default,
        bool interactive = false)
    {
        if (profile.HasCustomHeaders != true)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var id = $"{profile.Id}:headers";
        var saved = await backend.LookupSecretAsync(id, interactive, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(saved))
            throw new InvalidOperationException("Saved custom headers are missing. Open this provider in Settings and save them again.");

        try
        {
            return ProvidersStore.ValidateHeadersJson(saved);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("valid JSON", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Saved custom headers could not be read. Open this provider in Settings.");
        }
    }

    public static async Task SetProviderHeadersAsync(
        ISecretBackend backend,
        string id,
        IDictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        var value = ProvidersStore.ValidateHeaders(headers);
        try
        {
            if (value.Count > 0)
            {
                await backend.StoreSecretAsync(
                    $"{id}:headers",
                    JsonSerializer.Serialize(value),
                    "Plyph custom provider headers",
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await backend.ClearSecretAsync($"{id}:headers", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw KeyringError(ex);
        }
    }

    public static async Task SaveProviderConnectionAsync(
        IPlyphSettings settings,
        ISecretBackend backend,
        ProviderProfile profile,
        string apiKey,
        IDictionary<string, string> customHeaders,
        CancellationToken cancellationToken = default)
    {
        var headers = ProvidersStore.ValidateHeaders(customHeaders);
        var candidateInput = profile.Clone();
        candidateInput.HasCustomHeaders = headers.Count > 0 ? true : null;
        var candidate = ProvidersStore.ValidateProvider(candidateInput, requireModel: false);

        var previous = ProvidersStore.ReadProviders(settings).FirstOrDefault(item => item.Id == profile.Id);
        var oldKey = previous != null
            ? await GetApiKeyAsync(settings, backend, profile.Id, cancellationToken, interactive: true).ConfigureAwait(false)
            : "";
        var oldHeaders = previous != null
            ? await GetProviderHeadersAsync(backend, previous, cancellationToken, interactive: true).ConfigureAwait(false)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        var keyChanged = oldKey != (apiKey ?? "").Trim();
        var headersChanged = JsonSerializer.Serialize(oldHeaders) != JsonSerializer.Serialize(headers);
        var keyAttempted = false;
        var headersAttempted = false;

        try
        {
            if (keyChanged)
            {
                keyAttempted = true;
                await SetApiKeyAsync(settings, backend, profile.Id, apiKey ?? "", cancellationToken).ConfigureAwait(false);
            }
            if (headersChanged)
            {
                headersAttempted = true;
                await SetProviderHeadersAsync(backend, profile.Id, headers, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            ProvidersStore.SaveProvider(settings, candidate);
        }
        catch
        {
            var restored = true;
            if (keyAttempted)
            {
                try { await SetApiKeyAsync(settings, backend, profile.Id, oldKey, CancellationToken.None).ConfigureAwait(false); }
                catch { restored = false; }
            }
            if (headersAttempted)
            {
                try { await SetProviderHeadersAsync(backend, profile.Id, oldHeaders, CancellationToken.None).ConfigureAwait(false); }
                catch { restored = false; }
            }
            if (!restored)
                throw new InvalidOperationException("The provider could not be saved and some credentials could not be restored. Check this connection in Settings.");
            throw;
        }
    }

    public static async Task DeleteProviderAsync(
        IPlyphSettings settings,
        ISecretBackend backend,
        string id,
        CancellationToken cancellationToken = default)
    {
        var profile = ProvidersStore.ReadProviders(settings).FirstOrDefault(item => item.Id == id);
        if (profile == null)
            throw new InvalidOperationException("This provider has already been removed.");

        var oldKey = await GetApiKeyAsync(settings, backend, id, cancellationToken, interactive: true).ConfigureAwait(false);
        var oldHeaders = await GetProviderHeadersAsync(backend, profile, cancellationToken, interactive: true).ConfigureAwait(false);

        ProvidersStore.RemoveProvider(settings, id);
        try
        {
            await SetApiKeyAsync(settings, backend, id, "", cancellationToken).ConfigureAwait(false);
            if (profile.HasCustomHeaders == true)
                await SetProviderHeadersAsync(backend, id, new Dictionary<string, string>(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                if (!string.IsNullOrEmpty(oldKey))
                    await SetApiKeyAsync(settings, backend, id, oldKey, CancellationToken.None).ConfigureAwait(false);
                if (profile.HasCustomHeaders == true)
                    await SetProviderHeadersAsync(backend, id, oldHeaders, CancellationToken.None).ConfigureAwait(false);
                if (!ProvidersStore.ReadProviders(settings).Any(item => item.Id == id))
                    ProvidersStore.SaveProvider(settings, profile);
            }
            catch
            {
                throw new InvalidOperationException("The provider was removed, but its credentials could not be cleaned up or its profile restored.");
            }
            throw new InvalidOperationException("The saved credentials could not be removed. The provider has been restored. Try again after unlocking Credential Manager.");
        }
    }
}
