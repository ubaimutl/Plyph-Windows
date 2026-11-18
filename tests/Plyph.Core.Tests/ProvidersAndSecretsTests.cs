using System.Text.Json;
using System.Text.Json.Nodes;
using Plyph.Core;
using Xunit;

namespace Plyph.Core.Tests;

public sealed class ProvidersAndSecretsTests
{
    private static PlyphSettings SetupSettings(Dictionary<string, object>? overrides = null, IEnumerable<string>? userKeys = null)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["provider"] = "groq",
            ["custom-actions"] = "[]",
            ["custom-providers"] = "[]",
            ["provider-migration-version"] = 0,
            ["openai-compatible-url"] = "http://localhost:1234/v1",
            ["groq-model"] = "groq-saved-model",
            ["bai-model"] = "bai-saved-model",
            ["openrouter-model"] = "openrouter-saved-model",
            ["cerebras-model"] = "cerebras-saved-model",
            ["openai-model"] = "openai-saved-model",
            ["vercel-model"] = "vercel-saved-model",
            ["openai-compatible-model"] = "openai-compatible-saved-model"
        };
        if (overrides != null)
        {
            foreach (var (k, v) in overrides)
                values[k] = v;
        }
        return PlyphSettings.CreateInMemory(values, userKeys ?? Array.Empty<string>());
    }

    [Fact]
    public void Migration_Imports_Selected_Referenced_Configured_And_Keyring_Profiles()
    {
        var action = new PlyphAction { Id = "summary", Name = "Summary", Prompt = "Summarize", Provider = "cerebras", Model = "action-model" };
        var s = SetupSettings(new Dictionary<string, object>
        {
            ["custom-actions"] = JsonSerializer.Serialize(new[] { action }),
            ["prompt-run-provider"] = "openrouter",
            ["openai-compatible-url"] = "https://private.example/custom/api"
        }, new[] { "openai-compatible-url", "bai-model" });

        ProvidersStore.MigrateProviders(s, new[] { "openai", "vercel" }, complete: true);
        var profiles = ProvidersStore.ReadProviders(s);
        var ids = profiles.Select(p => p.Id).OrderBy(x => x).ToArray();
        var expected = new[] { "groq", "openrouter", "cerebras", "openai-compatible", "bai", "openai", "vercel" }.OrderBy(x => x).ToArray();

        Assert.Equal(expected, ids);
        Assert.Equal("https://private.example/custom/api", profiles.First(p => p.Id == "openai-compatible").Url);
        Assert.Equal("cerebras-saved-model", ProvidersStore.ProviderModel(s, "cerebras"));
    }

    [Fact]
    public void Missing_Provider_Fails_Explicitly_And_Deletion_Names_All_References()
    {
        var action = new PlyphAction { Id = "a", Name = "My action", Prompt = "Explain", Provider = "groq" };
        var s = SetupSettings(new Dictionary<string, object>
        {
            ["prompt-run-provider"] = "groq",
            ["custom-actions"] = JsonSerializer.Serialize(new[] { action })
        });

        ProvidersStore.MigrateProviders(s, Array.Empty<string>(), complete: true);
        var ex = Assert.Throws<InvalidOperationException>(() => ProvidersStore.RemoveProvider(s, "groq"));
        Assert.Contains("Active provider, Run selected prompt, My action", ex.Message);

        s.SetString("custom-providers", "[]");
        var resEx = Assert.Throws<InvalidOperationException>(() => ProvidersStore.ResolveProvider(s, "groq"));
        Assert.Contains("no longer exists", resEx.Message);
    }

    [Fact]
    public void Profile_Validation_Rejects_Unsafe_Urls_And_Accepts_Local_Or_Custom_Base_Paths()
    {
        foreach (var badUrl in new[]
                 {
                     "file:///tmp/key",
                     "https://user:password@example.com/v1",
                     "https://example.com?q=key",
                     "https://example.com/#secret",
                     "https://example.com/a b"
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                ProvidersStore.ValidateProvider(new ProviderProfile { Name = "Test", Model = "m", Url = badUrl }));
            Assert.Contains("URL", ex.Message);
        }

        foreach (var goodUrl in new[]
                 {
                     "http://127.0.0.1:1234/v1",
                     "http://[::1]:8080/v1",
                     "https://example.com/custom/api"
                 })
        {
            var validated = ProvidersStore.ValidateProvider(new ProviderProfile { Name = "Test", Model = "m", Url = goodUrl });
            Assert.Equal(goodUrl, validated.Url);
        }
    }

    [Fact]
    public void ValidateAdvanced_And_Headers_Enforce_Security_Rules()
    {
        var exReserved = Assert.Throws<InvalidOperationException>(() =>
            ProvidersStore.ValidateAdvanced(new ProviderAdvancedOptions
            {
                Parameters = new JsonObject { ["messages"] = "override" }
            }));
        Assert.Contains("controlled by Plyph", exReserved.Message);

        var exTemp = Assert.Throws<InvalidOperationException>(() =>
            ProvidersStore.ValidateAdvanced(new ProviderAdvancedOptions { Temperature = 2.5 }));
        Assert.Contains("Temperature", exTemp.Message);

        var exHeader = Assert.Throws<InvalidOperationException>(() =>
            ProvidersStore.ValidateHeaders(new Dictionary<string, string> { ["Host"] = "evil.com" }));
        Assert.Contains("not an allowed custom header", exHeader.Message);

        var merged = ProvidersStore.MergeProviderHeaders(
            new Dictionary<string, string> { ["Authorization"] = "Bearer default", ["X-Title"] = "Plyph" },
            new Dictionary<string, string> { ["authorization"] = "Bearer custom" });
        Assert.Equal("Bearer custom", merged["authorization"]);
        Assert.Equal("Plyph", merged["X-Title"]);
    }

    [Fact]
    public async Task Custom_Profile_Secrets_Are_Isolated_From_Settings_And_Other_Profiles()
    {
        var s = SetupSettings();
        var backend = new InMemorySecretBackend();

        await SecretsStore.SetApiKeyAsync(s, backend, "custom:a", "first-key");
        await SecretsStore.SetApiKeyAsync(s, backend, "custom:b", "second-key");

        Assert.Equal("first-key", await SecretsStore.GetApiKeyAsync(s, backend, "custom:a"));
        Assert.Equal("second-key", await SecretsStore.GetApiKeyAsync(s, backend, "custom:b"));

        await SecretsStore.SetApiKeyAsync(s, backend, "custom:a", "");
        Assert.Equal("", await SecretsStore.GetApiKeyAsync(s, backend, "custom:a"));
        Assert.Equal("second-key", await SecretsStore.GetApiKeyAsync(s, backend, "custom:b"));
    }

    [Fact]
    public void FormatProviderBadge_And_UiLayoutDefaults_Resolve_Cleanly()
    {
        var defaults = PlyphSettings.CreateInMemory();
        Assert.Equal("button", defaults.GetString("floating-toolbar-layout"));
        Assert.Equal("auto", defaults.GetString("result-popup-mode"));

        defaults.SetString("provider", "groq");
        defaults.SetString("groq-model", "openai/gpt-oss-20b");
        Assert.Equal("Groq · gpt-oss-20b", ProvidersStore.FormatProviderBadge(defaults));

        Assert.Equal("ChatGPT Account · Auto", ProvidersStore.FormatProviderBadge(defaults, "chatgpt", ""));
        Assert.Equal("Gemini · gemini-2.5-flash", ProvidersStore.FormatProviderBadge(defaults, "gemini", "gemini-2.5-flash"));
    }
}
