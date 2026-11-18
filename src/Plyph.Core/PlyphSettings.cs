using System.Text.Json;

namespace Plyph.Core;

public enum StorageMode
{
    Installed,
    Portable
}

public static class StorageModeResolver
{
    public const string PortableMarkerFileName = "portable.marker";
    public const string AltPortableMarkerFileName = "Plyph.portable";
    public const string PortableParentDataMarkerFileName = "portable-parent-data.marker";
    public const string PortableDataFolderName = "data";

    public static (StorageMode Mode, string DataDirectory) Resolve(
        string[]? args = null,
        string? baseDirectory = null,
        string? executableName = null,
        bool forcePortable = false)
    {
        var baseDir = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : Path.GetFullPath(baseDirectory);

        var exeName = executableName ?? Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "Plyph");
        var cliPortable = args?.Any(a =>
            string.Equals(a, "--portable", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/portable", StringComparison.OrdinalIgnoreCase)) == true;
        var cliInstalled = args?.Any(a =>
            string.Equals(a, "--installed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/installed", StringComparison.OrdinalIgnoreCase)) == true;

        if (!cliInstalled)
        {
            var trimmedBase = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parentDataMarker = Path.Combine(baseDir, PortableParentDataMarkerFileName);
            if (File.Exists(parentDataMarker))
            {
                var parentDir = Directory.GetParent(trimmedBase)?.FullName ?? baseDir;
                return (StorageMode.Portable, Path.Combine(parentDir, PortableDataFolderName));
            }

            var marker1 = Path.Combine(baseDir, PortableMarkerFileName);
            var marker2 = Path.Combine(baseDir, AltPortableMarkerFileName);
            var portableDataDir = Path.Combine(baseDir, PortableDataFolderName);
            var portableSettings = Path.Combine(portableDataDir, "settings.json");

            if (forcePortable ||
                cliPortable ||
                exeName.Contains("Portable", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(marker1) ||
                File.Exists(marker2) ||
                File.Exists(portableSettings))
            {
                return (StorageMode.Portable, portableDataDir);
            }
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return (StorageMode.Installed, Path.Combine(appData, "Plyph"));
    }
}

public interface IPlyphSettings
{
    StorageMode Mode { get; }
    string DataDirectory { get; }
    event EventHandler<string>? SettingChanged;

    string GetString(string key);
    bool SetString(string key, string value);
    int GetInt(string key);
    bool SetInt(string key, int value);
    long GetInt64(string key);
    bool SetInt64(string key, long value);
    double GetDouble(string key);
    bool SetDouble(string key, double value);
    bool GetBoolean(string key);
    bool SetBoolean(string key, bool value);
    string[] GetStrv(string key);
    bool SetStrv(string key, string[] value);
    object? GetUserValue(string key);
    void Save();
}

public sealed class PlyphSettings : IPlyphSettings
{
    private static readonly Dictionary<string, object> Defaults = new(StringComparer.Ordinal)
    {
        ["chatgpt-account-id"] = "",
        ["chatgpt-model"] = "",
        ["chatgpt-thinking-effort"] = "auto",
        ["chatgpt-welcome-shown"] = false,
        ["custom-providers"] = "[]",
        ["provider-migration-version"] = 0,
        ["provider"] = "groq",
        ["ollama-url"] = "http://127.0.0.1:11434",
        ["ollama-model"] = "qwen3:4b",
        ["cloudflare-account-id"] = "",
        ["cloudflare-api-key"] = "",
        ["cloudflare-model"] = "@cf/qwen/qwen3-30b-a3b-fp8",
        ["bai-api-key"] = "",
        ["bai-model"] = "deepseek-v4-flash",
        ["groq-api-key"] = "",
        ["groq-model"] = "openai/gpt-oss-20b",
        ["openai-api-key"] = "",
        ["openai-model"] = "gpt-4.1-mini",
        ["openai-compatible-url"] = "http://127.0.0.1:1234/v1",
        ["openai-compatible-api-key"] = "",
        ["openai-compatible-model"] = "local-model",
        ["gemini-api-key"] = "",
        ["gemini-model"] = "gemini-3.5-flash-lite",
        ["openrouter-api-key"] = "",
        ["openrouter-model"] = "openrouter/free",
        ["vercel-api-key"] = "",
        ["vercel-model"] = "openai/gpt-5.4-mini",
        ["cerebras-api-key"] = "",
        ["cerebras-model"] = "gpt-oss-120b",
        ["prompt-correct"] = "Correct grammar, spelling, punctuation, clarity, and style. Preserve the language, meaning, and tone. Return only the corrected text, unchanged if already correct.",
        ["prompt-rewrite"] = "Rewrite for clarity and natural flow. Preserve the language, meaning, and tone. Add no ideas or commentary. Return only the improved text.",
        ["prompt-run"] = "Follow the provided instruction precisely. Produce the requested result directly. Do not add introductory commentary unless requested.",
        ["prompt-run-provider"] = "",
        ["prompt-run-model"] = "",
        ["personal-preferences"] = "",
        ["correct-use-personal-preferences"] = true,
        ["rewrite-use-personal-preferences"] = true,
        ["ask-use-personal-preferences"] = true,
        ["prompt-use-personal-preferences"] = true,
        ["prompt-run-input-limit"] = 0L,
        ["prompt-run-output-limit"] = 0L,
        ["custom-actions"] = "[]",
        ["model-cache"] = "{}",
        ["preview-show-changes"] = false,
        ["preview-results"] = true,
        ["prefer-plain-text"] = true,
        ["clipboard-fallback"] = false,
        ["explicit-copy-apps"] = "",
        ["show-floating-button"] = true,
        ["floating-toolbar-layout"] = "button",
        ["floating-toolbar-position"] = "selection",
        ["floating-toolbar-scale"] = 1.0,
        ["toolbar-accent-icon"] = false,
        ["result-popup-mode"] = "auto",
        ["quick-actions"] = "[\"ask\", \"correct\", \"rewrite\"]",
        ["excluded-apps"] = "",
        ["pointer-feedback"] = true,
        ["debug-logging"] = false,
        ["feedback-position"] = "bottom-center",
        ["action-palette-position"] = "near-pointer",
        ["app-theme"] = "system",
        ["result-popup-appearance"] = "system",
        ["result-popup-dismiss-on-outside-click"] = false,
        ["variable-language"] = "English",
        ["variable-tone"] = "professional",
        ["variable-style"] = "clear and concise",
        ["ask-shortcut"] = Array.Empty<string>(),
        ["correct-shortcut"] = Array.Empty<string>(),
        ["rewrite-shortcut"] = Array.Empty<string>(),
        ["actions-shortcut"] = Array.Empty<string>(),
        ["launch-at-startup"] = false,
        ["selection-uia-enabled"] = true,
        ["selection-mouse-fallback"] = true
    };

    private readonly object _sync = new();
    private readonly Dictionary<string, object> _userValues = new(StringComparer.Ordinal);
    private readonly bool _persistToDisk;
    private readonly string _settingsFilePath;

    public StorageMode Mode { get; }
    public string DataDirectory { get; }
    public event EventHandler<string>? SettingChanged;

    public PlyphSettings(StorageMode mode = StorageMode.Installed, string? dataDirectory = null, bool persistToDisk = true)
    {
        Mode = mode;
        DataDirectory = dataDirectory ?? StorageModeResolver.Resolve(forcePortable: mode == StorageMode.Portable).DataDirectory;
        _persistToDisk = persistToDisk;
        _settingsFilePath = Path.Combine(DataDirectory, "settings.json");

        if (_persistToDisk)
            LoadFromDisk();
    }

    public static PlyphSettings CreateInMemory(Dictionary<string, object>? initialValues = null, IEnumerable<string>? userKeys = null)
    {
        var settings = new PlyphSettings(StorageMode.Portable, Path.Combine(Path.GetTempPath(), "plyph-mem"), persistToDisk: false);
        if (initialValues != null)
        {
            var explicitUserSet = userKeys != null ? new HashSet<string>(userKeys, StringComparer.Ordinal) : null;
            foreach (var (k, v) in initialValues)
            {
                if (explicitUserSet == null || explicitUserSet.Contains(k))
                    settings._userValues[k] = v;
                else
                    settings._nonUserOverrides[k] = v;
            }
        }
        return settings;
    }

    private readonly Dictionary<string, object> _nonUserOverrides = new(StringComparer.Ordinal);

    private object? GetRawValue(string key)
    {
        lock (_sync)
        {
            if (_userValues.TryGetValue(key, out var userVal))
                return userVal;
            if (_nonUserOverrides.TryGetValue(key, out var overrideVal))
                return overrideVal;
            if (Defaults.TryGetValue(key, out var defVal))
                return defVal;
            return null;
        }
    }

    public object? GetUserValue(string key)
    {
        lock (_sync)
        {
            return _userValues.TryGetValue(key, out var val) ? val : null;
        }
    }

    public string GetString(string key)
    {
        var val = GetRawValue(key);
        return val switch
        {
            string s => s,
            null => "",
            _ => Convert.ToString(val) ?? ""
        };
    }

    public bool SetString(string key, string value)
    {
        SetInternal(key, value ?? "");
        return true;
    }

    public int GetInt(string key)
    {
        var val = GetRawValue(key);
        return val switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => 0
        };
    }

    public bool SetInt(string key, int value)
    {
        SetInternal(key, value);
        return true;
    }

    public long GetInt64(string key)
    {
        var val = GetRawValue(key);
        return val switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            string s when long.TryParse(s, out var parsed) => parsed,
            _ => 0L
        };
    }

    public bool SetInt64(string key, long value)
    {
        SetInternal(key, value);
        return true;
    }

    public double GetDouble(string key)
    {
        var val = GetRawValue(key);
        return val switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            string s when double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0.0
        };
    }

    public bool SetDouble(string key, double value)
    {
        SetInternal(key, value);
        return true;
    }

    public bool GetBoolean(string key)
    {
        var val = GetRawValue(key);
        return val switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => false
        };
    }

    public bool SetBoolean(string key, bool value)
    {
        SetInternal(key, value);
        return true;
    }

    public string[] GetStrv(string key)
    {
        var val = GetRawValue(key);
        if (val is string[] arr)
            return arr.ToArray();
        if (val is IEnumerable<string> list)
            return list.ToArray();
        return Array.Empty<string>();
    }

    public bool SetStrv(string key, string[] value)
    {
        SetInternal(key, (value ?? Array.Empty<string>()).ToArray());
        return true;
    }

    private void SetInternal(string key, object value)
    {
        lock (_sync)
        {
            _userValues[key] = value;
            if (_persistToDisk)
                SaveToDiskLocked();
        }
        SettingChanged?.Invoke(this, key);
    }

    public void Save()
    {
        if (!_persistToDisk)
            return;
        lock (_sync)
        {
            SaveToDiskLocked();
        }
    }

    private void LoadFromDisk()
    {
        lock (_sync)
        {
            if (!File.Exists(_settingsFilePath))
                return;
            try
            {
                var json = File.ReadAllText(_settingsFilePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    _userValues[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString() ?? "",
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number when prop.Value.TryGetInt32(out var i) => i,
                        JsonValueKind.Number when prop.Value.TryGetInt64(out var l) => l,
                        JsonValueKind.Number => prop.Value.GetDouble(),
                        JsonValueKind.Array => prop.Value.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String)
                            .Select(e => e.GetString() ?? "")
                            .ToArray(),
                        _ => prop.Value.GetRawText()
                    };
                }
            }
            catch
            {
                // Keep defaults if settings file cannot be parsed
            }
        }
    }

    private void SaveToDiskLocked()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var json = JsonSerializer.Serialize(_userValues, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            var tempPath = _settingsFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _settingsFilePath, overwrite: true);
        }
        catch
        {
            // Ignore transient I/O errors during live updates
        }
    }
}
