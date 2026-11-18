using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Plyph.Core;

public sealed class JwkKey
{
    [JsonPropertyName("kid")]
    public string? Kid { get; set; }

    [JsonPropertyName("kty")]
    public string? Kty { get; set; }

    [JsonPropertyName("alg")]
    public string? Alg { get; set; }

    [JsonPropertyName("use")]
    public string? Use { get; set; }

    [JsonPropertyName("key_ops")]
    public List<string>? KeyOps { get; set; }

    [JsonPropertyName("n")]
    public string? N { get; set; }

    [JsonPropertyName("e")]
    public string? E { get; set; }
}

public sealed class IdTokenClaims
{
    public string Iss { get; init; } = "";
    public List<string> Aud { get; init; } = new();
    public string? Azp { get; init; }
    public double Exp { get; init; }
    public double? Nbf { get; init; }
    public string Sub { get; init; } = "";
    public string? Nonce { get; init; }
    public string? Email { get; init; }
    public bool? EmailVerified { get; init; }
}

public sealed class ChatGptServiceException : Exception
{
    public string? OAuthCode { get; set; }

    public ChatGptServiceException(string message, string? oauthCode = null) : base(message)
    {
        OAuthCode = oauthCode;
    }
}

public static class ChatGptProtocol
{
    public const string Issuer = "https://auth.openai.com";
    public const string Resource = "https://api.openai.com/v1";
    public const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";

    private static readonly Regex Base64UrlRegex = new(@"^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private static readonly Regex ClientIdRegex = new(@"^[A-Za-z0-9_-]{1,256}$", RegexOptions.Compiled);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Base64Url(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static byte[] Decode64(string? value)
    {
        if (string.IsNullOrEmpty(value) || !Base64UrlRegex.IsMatch(value) || value.Length % 4 == 1)
            throw new InvalidOperationException("Invalid sign-in token encoding.");

        var padded = value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(padded);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Invalid sign-in token encoding.", ex);
        }

        if (Base64Url(bytes) != value)
            throw new InvalidOperationException("Invalid sign-in token encoding.");

        return bytes;
    }

    public static byte[] Sha256(ReadOnlySpan<byte> bytes)
    {
        return SHA256.HashData(bytes);
    }

    private static BigInteger ToPositiveBigInteger(ReadOnlySpan<byte> bigEndianBytes)
    {
        return new BigInteger(bigEndianBytes, isUnsigned: true, isBigEndian: true);
    }

    public static IdTokenClaims VerifyIdToken(
        string? token,
        IEnumerable<JwkKey> keys,
        string clientId,
        string? nonce = null,
        string? subject = null,
        double? nowSeconds = null)
    {
        var now = nowSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        if (string.IsNullOrEmpty(token) || token.Length > 32768)
            throw new InvalidOperationException("Invalid sign-in token.");

        var parts = token.Split('.');
        if (parts.Length != 3)
            throw new InvalidOperationException("Invalid sign-in token.");

        using var headerDoc = JsonDocument.Parse(StrictUtf8.GetString(Decode64(parts[0])));
        var header = headerDoc.RootElement;
        if (header.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Invalid sign-in token.");

        var alg = header.TryGetProperty("alg", out var algProp) && algProp.ValueKind == JsonValueKind.String
            ? algProp.GetString()
            : null;
        var kid = header.TryGetProperty("kid", out var kidProp) && kidProp.ValueKind == JsonValueKind.String
            ? kidProp.GetString()
            : null;
        var hasCrit = header.TryGetProperty("crit", out var critProp) &&
                      critProp.ValueKind != JsonValueKind.Null &&
                      critProp.ValueKind != JsonValueKind.Undefined &&
                      critProp.ValueKind != JsonValueKind.False;
        var b64False = header.TryGetProperty("b64", out var b64Prop) && b64Prop.ValueKind == JsonValueKind.False;

        if (alg != "RS256" || string.IsNullOrEmpty(kid) || hasCrit || b64False)
            throw new InvalidOperationException("Unsupported sign-in signature.");

        var matches = (keys ?? Enumerable.Empty<JwkKey>()).Where(key =>
            key.Kid == kid &&
            key.Kty == "RSA" &&
            (string.IsNullOrEmpty(key.Alg) || key.Alg == "RS256") &&
            (string.IsNullOrEmpty(key.Use) || key.Use == "sig") &&
            (key.KeyOps == null || key.KeyOps.Contains("verify"))).ToList();

        if (matches.Count != 1)
            throw new InvalidOperationException("The sign-in signing key could not be verified.");

        var modulus = Decode64(matches[0].N);
        var exponent = Decode64(matches[0].E);
        var signature = Decode64(parts[2]);

        if (modulus.Length < 256 || modulus.Length > 1024 || modulus[0] < 128 ||
            signature.Length != modulus.Length || exponent.Length > 4)
        {
            throw new InvalidOperationException("Invalid sign-in signing key.");
        }

        var n = ToPositiveBigInteger(modulus);
        var e = ToPositiveBigInteger(exponent);
        var sigInt = ToPositiveBigInteger(signature);

        if (e < 3 || e.IsEven || sigInt >= n || n.IsEven)
            throw new InvalidOperationException("Invalid sign-in signature.");

        var verified = BigInteger.ModPow(sigInt, e, n);
        var digest = Sha256(Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"));
        var digestInfoHex = "3031300d060960864801650304020105000420" + Convert.ToHexString(digest).ToLowerInvariant();
        var ffCount = modulus.Length - digestInfoHex.Length / 2 - 3;
        var expectedHex = "0001" + string.Concat(Enumerable.Repeat("ff", ffCount)) + "00" + digestInfoHex;

        var verifiedBytes = verified.ToByteArray(isUnsigned: true, isBigEndian: true);
        var verifiedHex = Convert.ToHexString(verifiedBytes).ToLowerInvariant().PadLeft(modulus.Length * 2, '0');

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(verifiedHex),
                Encoding.ASCII.GetBytes(expectedHex)))
        {
            throw new InvalidOperationException("The sign-in signature is invalid.");
        }

        using var claimsDoc = JsonDocument.Parse(StrictUtf8.GetString(Decode64(parts[1])));
        var claimsEl = claimsDoc.RootElement;
        if (claimsEl.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The sign-in identity could not be verified. Please sign in again.");

        var iss = claimsEl.TryGetProperty("iss", out var issProp) && issProp.ValueKind == JsonValueKind.String
            ? issProp.GetString() ?? ""
            : "";

        var audiences = new List<string>();
        if (claimsEl.TryGetProperty("aud", out var audProp))
        {
            if (audProp.ValueKind == JsonValueKind.String && audProp.GetString() is { } s)
                audiences.Add(s);
            else if (audProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in audProp.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } a)
                        audiences.Add(a);
                    else
                        audiences.Add("__invalid__");
                }
            }
        }

        string? azp = null;
        var hasAzp = claimsEl.TryGetProperty("azp", out var azpProp) && azpProp.ValueKind != JsonValueKind.Undefined;
        if (hasAzp)
            azp = azpProp.ValueKind == JsonValueKind.String ? azpProp.GetString() : "__invalid__";

        double exp = double.NaN;
        if (claimsEl.TryGetProperty("exp", out var expProp) && expProp.ValueKind == JsonValueKind.Number)
            exp = expProp.GetDouble();

        double? nbf = null;
        if (claimsEl.TryGetProperty("nbf", out var nbfProp) && nbfProp.ValueKind != JsonValueKind.Undefined)
        {
            nbf = nbfProp.ValueKind == JsonValueKind.Number ? nbfProp.GetDouble() : double.NaN;
        }

        var sub = claimsEl.TryGetProperty("sub", out var subProp) && subProp.ValueKind == JsonValueKind.String
            ? subProp.GetString() ?? ""
            : "";

        var claimNonce = claimsEl.TryGetProperty("nonce", out var nonceProp) && nonceProp.ValueKind == JsonValueKind.String
            ? nonceProp.GetString()
            : null;

        var email = claimsEl.TryGetProperty("email", out var emailProp) && emailProp.ValueKind == JsonValueKind.String
            ? emailProp.GetString()
            : null;

        bool? emailVerified = null;
        if (claimsEl.TryGetProperty("email_verified", out var evProp) &&
            (evProp.ValueKind == JsonValueKind.True || evProp.ValueKind == JsonValueKind.False))
        {
            emailVerified = evProp.GetBoolean();
        }

        if (iss != Issuer ||
            !audiences.Contains(clientId) ||
            (hasAzp && azp != clientId) ||
            (audiences.Count > 1 && azp != clientId) ||
            !double.IsFinite(exp) || exp <= now ||
            (nbf.HasValue && (!double.IsFinite(nbf.Value) || nbf.Value > now + 60)) ||
            string.IsNullOrEmpty(sub) ||
            (nonce != null && claimNonce != nonce) ||
            (subject != null && sub != subject))
        {
            throw new InvalidOperationException("The sign-in identity could not be verified. Please sign in again.");
        }

        return new IdTokenClaims
        {
            Iss = iss,
            Aud = audiences,
            Azp = azp,
            Exp = exp,
            Nbf = nbf,
            Sub = sub,
            Nonce = claimNonce,
            Email = email,
            EmailVerified = emailVerified
        };
    }

    public static (string ClientId, string Code) CallbackResult(
        IDictionary<string, string>? query,
        string state,
        string? clientId = null)
    {
        if (query == null || !query.TryGetValue("state", out var qState) || qState != state)
            throw new InvalidOperationException("Invalid sign-in state.");

        if (query.TryGetValue("error", out var error) && !string.IsNullOrEmpty(error))
        {
            throw new InvalidOperationException(error == "access_denied"
                ? "ChatGPT sign-in was declined."
                : "ChatGPT sign-in failed.");
        }

        var issued = query.TryGetValue("client_id", out var qClient) && !string.IsNullOrEmpty(qClient)
            ? qClient
            : clientId;

        query.TryGetValue("code", out var code);
        if (string.IsNullOrEmpty(issued) ||
            issued == "dynamic_agent_client" ||
            !ClientIdRegex.IsMatch(issued) ||
            (!string.IsNullOrEmpty(clientId) && issued != clientId) ||
            string.IsNullOrEmpty(code))
        {
            throw new InvalidOperationException("ChatGPT registration was incomplete or changed accounts. Try signing in again.");
        }

        return (issued, code);
    }

    public static ChatGptServiceException ChatGptError(string? code, int status = 0)
    {
        if (code is "subscription_sharing_usage_limit_exceeded" or "subscription_sharing_usage_unavailable" || status == 429)
        {
            return new ChatGptServiceException(
                "ChatGPT usage is unavailable or a limit was reached. Open Manage usage in Plyph settings.",
                code);
        }
        if (code == "invalid_grant" || status == 401)
        {
            return new ChatGptServiceException(
                "Your ChatGPT connection expired. Continue with ChatGPT in Plyph settings.",
                code);
        }
        if (status == 403)
        {
            return new ChatGptServiceException(
                "ChatGPT plan access was denied. Check permissions in ChatGPT settings.",
                code);
        }
        return new ChatGptServiceException(
            status > 0
                ? $"ChatGPT could not complete the request ({status}). Try again."
                : "ChatGPT could not complete the response. Try again.",
            code);
    }
}

public sealed class Utf8StreamDecoder
{
    private byte[] _pending = Array.Empty<byte>();
    private bool _firstChunk = true;
    private static readonly Encoding FatalUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public string Decode(ReadOnlySpan<byte> chunk, bool final = false)
    {
        var bytes = new byte[_pending.Length + chunk.Length];
        _pending.CopyTo(bytes, 0);
        chunk.CopyTo(bytes.AsSpan(_pending.Length));

        var end = bytes.Length;
        if (!final && end > 0)
        {
            var start = end - 1;
            while (start > 0 && (bytes[start] & 0xc0) == 0x80)
                start--;
            var lead = bytes[start];
            var length = lead is >= 0xc2 and <= 0xdf ? 2
                : lead is >= 0xe0 and <= 0xef ? 3
                : lead is >= 0xf0 and <= 0xf4 ? 4 : 1;
            if (end - start < length)
                end = start;
        }

        var text = FatalUtf8.GetString(bytes, 0, end);
        if (_firstChunk && text.Length > 0)
        {
            _firstChunk = false;
            if (text[0] == '\uFEFF')
                text = text[1..];
        }
        else if (end > 0)
        {
            _firstChunk = false;
        }

        _pending = bytes[end..];
        return text;
    }

    public string Finish()
    {
        return Decode(ReadOnlySpan<byte>.Empty, final: true);
    }
}

public sealed class ResponsesStream
{
    private static readonly Regex FrameBoundaryRegex = new(@"\r?\n\r?\n", RegexOptions.Compiled);
    private string _buffer = "";
    private int _size;
    private string _output = "";
    private readonly SortedDictionary<int, JsonElement> _items = new();
    private bool _completed;

    public void Feed(string chunk)
    {
        _size += chunk.Length;
        if (_size > 8 * 1024 * 1024)
            throw new InvalidOperationException("The ChatGPT response is too large. Select less text.");

        _buffer += chunk;
        Match match;
        while ((match = FrameBoundaryRegex.Match(_buffer)).Success)
        {
            var frame = _buffer[..match.Index];
            _buffer = _buffer[(match.Index + match.Length)..];

            var lines = frame.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line =>
                {
                    var s = line[5..];
                    return s.StartsWith(' ') ? s[1..] : s;
                });

            var data = string.Join("\n", lines);
            if (string.IsNullOrEmpty(data) || data == "[DONE]")
                continue;

            using var doc = JsonDocument.Parse(data);
            var ev = doc.RootElement;
            var type = ev.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                ? typeProp.GetString()
                : null;

            if (type is "error" or "response.failed")
            {
                string? code = null;
                if (ev.TryGetProperty("code", out var c1) && c1.ValueKind == JsonValueKind.String)
                    code = c1.GetString();
                else if (ev.TryGetProperty("error", out var errObj) && errObj.ValueKind == JsonValueKind.Object &&
                         errObj.TryGetProperty("code", out var c2) && c2.ValueKind == JsonValueKind.String)
                    code = c2.GetString();
                else if (ev.TryGetProperty("response", out var respObj) && respObj.ValueKind == JsonValueKind.Object &&
                         respObj.TryGetProperty("error", out var rErr) && rErr.ValueKind == JsonValueKind.Object &&
                         rErr.TryGetProperty("code", out var c3) && c3.ValueKind == JsonValueKind.String)
                    code = c3.GetString();

                throw ChatGptProtocol.ChatGptError(code);
            }

            if (type == "response.incomplete")
                throw new InvalidOperationException("ChatGPT returned an incomplete response. Try a shorter request.");

            if (type == "response.output_item.done" &&
                ev.TryGetProperty("item", out var itemEl) &&
                itemEl.ValueKind == JsonValueKind.Object &&
                itemEl.TryGetProperty("type", out var itemType) &&
                itemType.GetString() == "message" &&
                ev.TryGetProperty("output_index", out var outIdx) &&
                outIdx.TryGetInt32(out var idx) &&
                idx >= 0)
            {
                _items[idx] = itemEl.Clone();
            }

            if (type == "response.completed")
            {
                if (!ev.TryGetProperty("response", out var resp) ||
                    resp.ValueKind != JsonValueKind.Object ||
                    !resp.TryGetProperty("status", out var statusProp) ||
                    statusProp.GetString() != "completed")
                {
                    throw new InvalidOperationException("ChatGPT did not complete the response.");
                }

                var outputItems = new List<JsonElement>();
                if (resp.TryGetProperty("output", out var outArr) &&
                    outArr.ValueKind == JsonValueKind.Array &&
                    outArr.GetArrayLength() > 0)
                {
                    foreach (var el in outArr.EnumerateArray())
                        outputItems.Add(el);
                }
                else
                {
                    outputItems.AddRange(_items.Values);
                }

                var sb = new StringBuilder();
                foreach (var item in outputItems)
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!item.TryGetProperty("type", out var it) || it.GetString() != "message")
                        continue;
                    if (!item.TryGetProperty("content", out var contentArr) || contentArr.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var part in contentArr.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.Object)
                            continue;
                        var pType = part.TryGetProperty("type", out var pt) ? pt.GetString() : null;
                        if (pType is "output_text" or "refusal")
                        {
                            if (part.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                                sb.Append(txt.GetString());
                            else if (part.TryGetProperty("refusal", out var refTxt) && refTxt.ValueKind == JsonValueKind.String)
                                sb.Append(refTxt.GetString());
                        }
                    }
                }

                _output = sb.ToString();
                _completed = true;
            }
        }
    }

    public string Finish()
    {
        if (!_completed)
            throw new InvalidOperationException("ChatGPT disconnected before completing the response. Nothing was replaced.");
        if (string.IsNullOrWhiteSpace(_output))
            throw new InvalidOperationException("ChatGPT returned no text.");
        return _output.Trim();
    }
}
