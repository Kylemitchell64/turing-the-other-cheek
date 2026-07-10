using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameApi.GameLoop;

// A custom prompt pack a host built with the AI category maker. Lives ONLY in memory
// (on the Lobby) and inside signed share-codes — it is never stored on the server.
public record CustomPack(string Name, bool Nsfw, string[] Prompts);

// Turns a CustomPack into a compact, tamper-proof share-code and back.
//
// Format:  TTOC1.<body>.<sig>
//   body = base64url( gzip( json({name,nsfw,prompts}) ) )
//   sig  = base64url( HMAC-SHA256(key, gzipBytes)[..16] )
//
// The signing key is DERIVED from JWT_KEY (HMAC of the constant "packcode") so a code
// can't be forged or edited by hand — which is the whole point: the AI safety filter
// runs at generation time, and the signature is what stops someone hand-crafting a code
// that smuggles banned content past it. A bad/edited signature => rejected.
public class PackCodec
{
    public const string Prefix = "TTOC1.";
    // Hard cap so a decode can't be handed a megabyte to decompress (zip-bomb guard) and
    // so codes stay pasteable. ~8KB of code comfortably holds a 20-prompt pack.
    public const int MaxCodeLength = 8 * 1024;
    private const int SigBytes = 16;
    private const int MaxPrompts = 60;
    private const int MaxNameLength = 20;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly byte[] _key;

    public PackCodec(string jwtKey)
    {
        // Derive a pack-code-specific key from the JWT signing key so the two secrets
        // are never the same bytes even though they share a root.
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(jwtKey));
        _key = h.ComputeHash(Encoding.UTF8.GetBytes("packcode"));
    }

    public string Encode(CustomPack pack)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(pack, JsonOpts);
        var gz = Gzip(json);
        var sig = Sign(gz);
        return Prefix + ToBase64Url(gz) + "." + ToBase64Url(sig);
    }

    // Verify + decompress a share-code. Returns null on ANY problem (bad prefix, bad
    // shape, tampered signature, oversize, junk) with a friendly reason in `error`.
    public CustomPack? TryDecode(string? code, out string error)
    {
        error = "";
        code = (code ?? "").Trim();

        if (code.Length == 0) { error = "paste a code first"; return null; }
        if (code.Length > MaxCodeLength) { error = "that code's too big"; return null; }
        if (!code.StartsWith(Prefix, StringComparison.Ordinal))
        {
            error = "that doesn't look like a pack code";
            return null;
        }

        var parts = code.Split('.');
        // ["TTOC1", body, sig]
        if (parts.Length != 3 || parts[0] != "TTOC1" || parts[1].Length == 0 || parts[2].Length == 0)
        {
            error = "that code's been messed with";
            return null;
        }

        byte[] gz, sig;
        try
        {
            gz = FromBase64Url(parts[1]);
            sig = FromBase64Url(parts[2]);
        }
        catch
        {
            error = "that code's been messed with";
            return null;
        }

        var expected = Sign(gz);
        if (!CryptographicOperations.FixedTimeEquals(sig, expected))
        {
            error = "that code's been messed with";
            return null;
        }

        CustomPack? pack;
        try
        {
            var json = Gunzip(gz);
            pack = JsonSerializer.Deserialize<CustomPack>(json, JsonOpts);
        }
        catch
        {
            error = "that code's been messed with";
            return null;
        }

        if (pack == null || string.IsNullOrWhiteSpace(pack.Name) ||
            pack.Prompts == null || pack.Prompts.Length == 0)
        {
            error = "that code's been messed with";
            return null;
        }
        if (pack.Name.Length > MaxNameLength || pack.Prompts.Length > MaxPrompts ||
            pack.Prompts.Any(string.IsNullOrWhiteSpace))
        {
            error = "that code's been messed with";
            return null;
        }

        return pack;
    }

    private byte[] Sign(byte[] payload)
    {
        using var h = new HMACSHA256(_key);
        var full = h.ComputeHash(payload);
        var truncated = new byte[SigBytes];
        Array.Copy(full, truncated, SigBytes);
        return truncated;
    }

    private static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        // Bound the decompressed size to keep a crafted code from exploding memory.
        var buffer = new byte[4096];
        int total = 0, read;
        while ((read = gz.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxCodeLength * 8) throw new InvalidDataException("decompressed too large");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var b64 = s.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
        }
        return Convert.FromBase64String(b64);
    }
}
