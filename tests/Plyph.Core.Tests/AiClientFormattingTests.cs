using System.Text.Json;
using System.Text.Json.Nodes;
using Plyph.Core;
using Xunit;

namespace Plyph.Core.Tests;

public sealed class AiClientFormattingTests
{
    private static readonly string[] AllProviders =
    {
        "ollama", "cloudflare", "bai", "groq", "gemini", "openrouter",
        "cerebras", "openai", "openai-compatible", "vercel", "chatgpt"
    };

    private sealed class TestHarness : IDisposable
    {
        public PlyphSettings Settings { get; }
        public InMemorySecretBackend Secrets { get; }
        public AiClient Client { get; }
        public List<HttpTransformRequest> Requests { get; } = new();
        public List<string> Logs { get; } = new();
        public string ResponseText { get; set; } = "Result";
        public string FinishReason { get; set; } = "STOP";
        public long? ThoughtsTokenCount { get; set; }

        public TestHarness(string provider = "groq")
        {
            var values = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["custom-providers"] = "[]",
                ["provider"] = provider,
                [$"{provider}-model"] = "test-model",
                ["cloudflare-account-id"] = "test-account",
                ["ollama-url"] = "http://localhost:11434",
                ["openai-compatible-url"] = "http://localhost:1234/v1",
                ["prompt-correct"] = "Correct the text.",
                ["prompt-rewrite"] = "Rewrite the text.",
                ["prompt-run"] = "Follow the user request.",
                ["variable-language"] = "German",
                ["variable-tone"] = "friendly",
                ["variable-style"] = "concise",
                ["prefer-plain-text"] = true,
                ["debug-logging"] = false
            };

            Settings = PlyphSettings.CreateInMemory(values);
            Secrets = new InMemorySecretBackend();
            foreach (var p in AllProviders)
            {
                if (SecretsStore.KeySettings.ContainsKey(p))
                    Secrets.StoreSecretAsync(p, "test-key", "test").GetAwaiter().GetResult();
            }

            Client = new AiClient(Settings, Secrets)
            {
                DiagnosticLogger = line => Logs.Add(line),
                ChatGptTransformOverride = (text, instructions, model, _) =>
                {
                    var body = new JsonObject
                    {
                        ["model"] = model,
                        ["messages"] = new JsonArray
                        {
                            new JsonObject { ["role"] = "system", ["content"] = instructions },
                            new JsonObject { ["role"] = "user", ["content"] = text }
                        }
                    };
                    Requests.Add(new HttpTransformRequest("https://api.openai.com/v1/responses", new Dictionary<string, string>(), body));
                    return Task.FromResult(ResponseText);
                },
                HttpSenderOverride = (req, _) =>
                {
                    Requests.Add(req);
                    var respObj = new
                    {
                        choices = new[] { new { message = new { content = ResponseText }, finish_reason = "stop" } },
                        message = new { content = ResponseText },
                        candidates = new[]
                        {
                            new
                            {
                                content = new { parts = new[] { new { text = ResponseText } } },
                                finishReason = FinishReason
                            }
                        },
                        usageMetadata = ThoughtsTokenCount.HasValue
                            ? new { thoughtsTokenCount = ThoughtsTokenCount.Value, candidatesTokenCount = 37 }
                            : null
                    };
                    var doc = JsonDocument.Parse(JsonSerializer.Serialize(respObj));
                    return Task.FromResult(new HttpTransformResponse(200, doc.RootElement.Clone()));
                }
            };
        }

        public static string ExtractGuidance(JsonObject body)
        {
            if (body.TryGetPropertyValue("systemInstruction", out var sys) &&
                sys is JsonObject sysObj &&
                sysObj["parts"] is JsonArray parts &&
                parts.Count > 0)
            {
                return parts[0]?["text"]?.GetValue<string>() ?? "";
            }

            if (body.TryGetPropertyValue("messages", out var msgs) && msgs is JsonArray msgArr)
            {
                var sysMsg = msgArr.FirstOrDefault(m => m?["role"]?.GetValue<string>() == "system");
                return sysMsg?["content"]?.GetValue<string>() ?? "";
            }

            return "";
        }

        public static string ExtractUserText(JsonObject body)
        {
            if (body.TryGetPropertyValue("contents", out var contents) &&
                contents is JsonArray cArr &&
                cArr.Count > 0)
            {
                return cArr[0]?["parts"]?[0]?["text"]?.GetValue<string>() ?? "";
            }

            if (body.TryGetPropertyValue("messages", out var msgs) && msgs is JsonArray msgArr)
            {
                var userMsg = msgArr.FirstOrDefault(m => m?["role"]?.GetValue<string>() == "user");
                return userMsg?["content"]?.GetValue<string>() ?? "";
            }

            return "";
        }

        public void Dispose() => Client.Dispose();
    }

    [Theory]
    [InlineData("groq", "The meeting moved to Friday.")]
    [InlineData("groq", "")]
    [InlineData("gemini", "The meeting moved to Friday.")]
    [InlineData("chatgpt", "")]
    public async Task AskSendsTheQuestionAsTheRequestAndSelectionAsOptionalContext(string provider, string selection)
    {
        using var harness = new TestHarness(provider) { ResponseText = "The meeting is on Friday." };
        const string question = "What day is the meeting?";

        var result = await harness.Client.TransformAsync(selection, "ask", question);

        var request = Assert.Single(harness.Requests).Body;
        var userText = TestHarness.ExtractUserText(request);
        if (selection.Length == 0)
            Assert.Equal(question, userText);
        else
        {
            Assert.Contains(question, userText);
            Assert.Contains(selection, userText);
        }
        Assert.Contains("Answer the user's question", TestHarness.ExtractGuidance(request));
        Assert.DoesNotContain("Return only the transformed text", userText);
        Assert.Equal(harness.ResponseText, result);
    }

    [Theory]
    [InlineData("ollama")]
    [InlineData("cloudflare")]
    [InlineData("bai")]
    [InlineData("groq")]
    [InlineData("gemini")]
    [InlineData("openrouter")]
    [InlineData("cerebras")]
    [InlineData("openai")]
    [InlineData("openai-compatible")]
    [InlineData("vercel")]
    [InlineData("chatgpt")]
    public async Task Plain_Text_Guidance_And_Tag_Cleanup_Work_For_Every_Provider(string provider)
    {
        using var h = new TestHarness(provider);
        const string selection = "Please correct this sentence.";

        await h.Client.TransformAsync(selection, "correct");
        var enabledGuidance = TestHarness.ExtractGuidance(h.Requests[0].Body);
        Assert.StartsWith("Correct the text.\n\n", enabledGuidance);
        Assert.Contains("Use plain text by default", enabledGuidance);
        Assert.Equal(
            $"Transform only the text inside the tags.\nReturn only the transformed text.\n<text>\n{selection}\n</text>",
            TestHarness.ExtractUserText(h.Requests[0].Body));

        h.Settings.SetBoolean("prefer-plain-text", false);
        await h.Client.TransformAsync(selection, "correct");
        Assert.Equal("Correct the text.", TestHarness.ExtractGuidance(h.Requests[1].Body));

        h.ResponseText = "<text>\n```js\nconst a = 1;\n```\n</text>";
        var cleaned = await h.Client.TransformAsync("```js\nconst a = 1;\n```", "rewrite");
        Assert.Equal("```js\nconst a = 1;\n```", cleaned);
    }

    [Fact]
    public async Task Prompt_Variables_And_Personal_Preferences_Expand_Correctly()
    {
        using var h = new TestHarness("groq");
        h.Settings.SetBoolean("prefer-plain-text", false);
        h.Settings.SetString("personal-preferences", "I use Windows and C#.");

        await h.Client.TransformAsync("Hello", "custom", "Use ${language}; ${tone}; ${style}.");
        var guidance = TestHarness.ExtractGuidance(h.Requests[^1].Body);
        Assert.Contains("I use Windows and C#.", guidance);
        Assert.EndsWith("Action instructions:\nUse German; friendly; concise.", guidance);

        await h.Client.TransformAsync("Hello", "custom", "Use ${language}.", new TransformOptions { UsePersonalPreferences = false });
        Assert.Equal("Use German.", TestHarness.ExtractGuidance(h.Requests[^1].Body));
    }

    [Fact]
    public async Task Gemini_Explicit_Limit_Spent_On_Thinking_Reports_Clear_Error()
    {
        using var h = new TestHarness("gemini");
        h.FinishReason = "MAX_TOKENS";
        h.ThoughtsTokenCount = 959;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Client.TransformAsync(new string('a', 300), "custom", null, new TransformOptions { OutputLimit = 1000L }));

        Assert.Equal(
            "Response reached this action’s output limit, and the model used 959 tokens of it for thinking. Raise the limit or set it to Auto.",
            ex.Message);
    }

    [Fact]
    public async Task ChatGpt_Rejects_Explicit_Output_Limit_Before_Requesting()
    {
        using var h = new TestHarness("chatgpt");
        await h.Client.TransformAsync("Hello", "prompt", null, new TransformOptions { OutputLimit = 0L });
        Assert.Single(h.Requests);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Client.TransformAsync("Hello", "custom", null, new TransformOptions { OutputLimit = 1000L }));
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Sequential_Requests_Recover_After_Errors_And_Cancellation()
    {
        using var h = new TestHarness("groq");

        // 1. Trigger a provider error (401/500)
        h.Client.HttpSenderOverride = (_, _) =>
        {
            var doc = JsonDocument.Parse("{\"error\":\"rate limit\"}");
            return Task.FromResult(new HttpTransformResponse(429, doc.RootElement.Clone()));
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Client.TransformAsync("Hello", "correct"));

        // 2. Cancel via CancellationToken
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Client.TransformAsync("Hello", "correct", null, null, cts.Token));

        // 3. Subsequent request must succeed cleanly with zero leaked cancellation state
        h.Client.HttpSenderOverride = (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var doc = JsonDocument.Parse("{\"choices\":[{\"message\":{\"content\":\"Clean output\"},\"finish_reason\":\"stop\"}]}");
            return Task.FromResult(new HttpTransformResponse(200, doc.RootElement.Clone()));
        };
        var output = await h.Client.TransformAsync("Hello", "correct");
        Assert.Equal("Clean output", output);

        // 4. Calling Cancel() after completion must not throw
        h.Client.Cancel();
    }
}
