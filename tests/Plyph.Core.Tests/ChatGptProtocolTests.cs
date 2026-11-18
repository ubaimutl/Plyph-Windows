using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Plyph.Core;
using Xunit;

namespace Plyph.Core.Tests;

public sealed class ChatGptProtocolTests
{
    private static (RSA Rsa, JwkKey Jwk) CreateTestRsaKey(string kid = "test-key")
    {
        var rsa = RSA.Create(2048);
        var pub = rsa.ExportParameters(false);
        var jwk = new JwkKey
        {
            Kid = kid,
            Kty = "RSA",
            Use = "sig",
            Alg = "RS256",
            N = ChatGptProtocol.Base64Url(pub.Modulus!),
            E = ChatGptProtocol.Base64Url(pub.Exponent!)
        };
        return (rsa, jwk);
    }

    private static string CreateJwt(
        RSA rsa,
        Dictionary<string, object?>? claimOverrides = null,
        Dictionary<string, object?>? headerOverrides = null)
    {
        var header = new Dictionary<string, object?>
        {
            ["alg"] = "RS256",
            ["kid"] = "test-key"
        };
        if (headerOverrides != null)
        {
            foreach (var (k, v) in headerOverrides)
                header[k] = v;
        }

        var claims = new Dictionary<string, object?>
        {
            ["iss"] = "https://auth.openai.com",
            ["aud"] = "client-test",
            ["sub"] = "user-test",
            ["exp"] = 2000,
            ["nonce"] = "test-nonce"
        };
        if (claimOverrides != null)
        {
            foreach (var (k, v) in claimOverrides)
                claims[k] = v;
        }

        var hB64 = ChatGptProtocol.Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header)));
        var cB64 = ChatGptProtocol.Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(claims)));
        var input = $"{hB64}.{cB64}";
        var sig = rsa.SignData(Encoding.UTF8.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{input}.{ChatGptProtocol.Base64Url(sig)}";
    }

    [Fact]
    public void Native_Protocol_Verifies_Independently_Signed_Rsa_Jwts()
    {
        var (rsa, jwk) = CreateTestRsaKey();
        using (rsa)
        {
            var token1 = CreateJwt(rsa);
            var claims1 = ChatGptProtocol.VerifyIdToken(token1, new[] { jwk }, "client-test", "test-nonce", "user-test", 1000);
            Assert.Equal("user-test", claims1.Sub);

            var token2 = CreateJwt(rsa, new Dictionary<string, object?>
            {
                ["aud"] = new[] { "client-test", "other" },
                ["azp"] = "client-test"
            });
            var claims2 = ChatGptProtocol.VerifyIdToken(token2, new[] { jwk }, "client-test", "test-nonce", "user-test", 1000);
            Assert.Equal("user-test", claims2.Sub);
        }
    }

    [Fact]
    public void Id_Token_Rejects_Invalid_Claims_Headers_And_Tampering()
    {
        var (rsa, jwk) = CreateTestRsaKey();
        using (rsa)
        {
            foreach (var (field, val) in new (string, object?)[]
                     {
                         ("iss", "https://evil.test"),
                         ("aud", "other"),
                         ("sub", "different"),
                         ("exp", 1000),
                         ("nonce", "wrong"),
                         ("nbf", 1061),
                         ("azp", "other")
                     })
            {
                var badToken = CreateJwt(rsa, new Dictionary<string, object?> { [field] = val });
                Assert.Throws<InvalidOperationException>(() =>
                    ChatGptProtocol.VerifyIdToken(badToken, new[] { jwk }, "client-test", "test-nonce", "user-test", 1000));
            }

            // Multiple audiences without azp
            var noAzp = CreateJwt(rsa, new Dictionary<string, object?> { ["aud"] = new[] { "client-test", "other" } });
            Assert.Throws<InvalidOperationException>(() =>
                ChatGptProtocol.VerifyIdToken(noAzp, new[] { jwk }, "client-test", "test-nonce", "user-test", 1000));

            // Signature tampering
            var valid = CreateJwt(rsa);
            var parts = valid.Split('.');
            var sigBytes = ChatGptProtocol.Decode64(parts[2]);
            sigBytes[50] ^= 1;
            var tampered = $"{parts[0]}.{parts[1]}.{ChatGptProtocol.Base64Url(sigBytes)}";
            Assert.Throws<InvalidOperationException>(() =>
                ChatGptProtocol.VerifyIdToken(tampered, new[] { jwk }, "client-test", "test-nonce", "user-test", 1000));
        }
    }

    [Fact]
    public void Pkce_Sha256_Matches_Rfc_7636_Vector()
    {
        var input = Encoding.UTF8.GetBytes("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");
        var actual = ChatGptProtocol.Base64Url(ChatGptProtocol.Sha256(input));
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", actual);
    }

    [Fact]
    public void Base64Url_Rejects_Noncanonical_Encodings()
    {
        foreach (var bad in new string?[] { "A", "YQ==", "Y R", "YR", "", null })
        {
            Assert.Throws<InvalidOperationException>(() => ChatGptProtocol.Decode64(bad));
        }
    }

    [Fact]
    public void Sse_Handles_Every_Chunk_Boundary_And_Returns_Only_Completed_Output()
    {
        static string EventFrame(object data) => $"data: {JsonSerializer.Serialize(data)}\r\n\r\n";

        var completedFrame = EventFrame(new
        {
            type = "response.completed",
            response = new
            {
                status = "completed",
                output = new object[]
                {
                    new { type = "reasoning", summary = new[] { new { text = "Do not return this" } } },
                    new { type = "message", content = new[] { new { type = "output_text", text = "Grüße 🌍" } } }
                }
            }
        });

        var input = ": keepalive\r\n\r\n" +
                    EventFrame(new { type = "response.output_text.delta", delta = "partial" }) +
                    completedFrame +
                    "data: [DONE]\r\n\r\n";

        for (var boundary = 1; boundary < input.Length; boundary++)
        {
            var parser = new ResponsesStream();
            parser.Feed(input[..boundary]);
            parser.Feed(input[boundary..]);
            Assert.Equal("Grüße 🌍", parser.Finish());
        }
    }

    [Fact]
    public void Utf8_Streaming_Preserves_Every_Byte_Split_Including_Emoji_And_Bom()
    {
        var bytes = Encoding.UTF8.GetBytes("Hello Grüße 日本語 🌍 \uFEFF done");
        for (var split = 0; split <= bytes.Length; split++)
        {
            var decoder = new Utf8StreamDecoder();
            var result = decoder.Decode(bytes.AsSpan(0, split)) +
                         decoder.Decode(bytes.AsSpan(split)) +
                         decoder.Finish();
            Assert.Equal("Hello Grüße 日本語 🌍 \uFEFF done", result);
        }
    }
}
