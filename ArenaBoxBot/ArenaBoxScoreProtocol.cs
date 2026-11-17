using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ArenaBoxBot;

public enum ArenaScoreReplyKind { Success, SessionKeyMissing, Rejected, InvalidFormat }
public sealed class ArenaScoreReply
{
    public ArenaScoreReplyKind Kind;
    public string Json = "";
    public string Detail = "";
    public bool Success => Kind == ArenaScoreReplyKind.Success;
}

public static class ArenaBoxScoreProtocol
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static ArenaScoreReply Decode(string wire, byte[] key)
    {
        var text = (wire ?? "").Trim().TrimStart('\uFEFF').Trim();
        if (text.Length == 0 || text.Length > MaximumBytes * 2) return Invalid("empty_or_oversized_response");
        try
        {
            // Rejected requests are plain JSON, not encrypted score data.
            if (text.StartsWith("{", StringComparison.Ordinal)) return ValidateJson(text);
            var b64 = text.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var input = Convert.FromBase64String(b64);
            if (key == null || key.Length != 16) return Invalid("invalid_local_key");
            var decoded = ApplyCipher(input, key);
            try { return ValidateJson(StrictUtf8.GetString(decoded)); }
            catch (DecoderFallbackException) { }
            catch (Newtonsoft.Json.JsonException) { }
            return ValidateJson(StrictUtf8.GetString(Decompress(decoded)));
        }
        catch (Exception ex) when (ex is FormatException || ex is InvalidDataException ||
                                   ex is DecoderFallbackException || ex is Newtonsoft.Json.JsonException || ex is IOException)
        { return Invalid("unrecognized_response_format"); }
    }

    private static ArenaScoreReply ValidateJson(string json)
    {
        var root = JObject.Parse((json ?? "").TrimStart('\uFEFF').Trim());
        var status = Property(root, "status");
        if (status == null || status.Type != JTokenType.Boolean) return Invalid("missing_or_invalid_status");
        if (!bool.TryParse(status.ToString(), out var succeeded)) return Invalid("missing_or_invalid_status");
        if (!succeeded)
        {
            var message = Property(root, "msg")?.ToString().Trim();
            var missingKey = string.Equals(message, "key null", StringComparison.OrdinalIgnoreCase);
            return new ArenaScoreReply { Kind = missingKey ? ArenaScoreReplyKind.SessionKeyMissing : ArenaScoreReplyKind.Rejected,
                Detail = missingKey ? "盒子评分会话失效：服务端返回 key null，等待盒子刷新会话" : "盒子评分接口拒绝请求" };
        }
        return new ArenaScoreReply { Kind = ArenaScoreReplyKind.Success, Json = root.ToString(Newtonsoft.Json.Formatting.None), Detail = "status=true" };
    }

    private static JToken Property(JObject root, string name)
    {
        foreach (var property in root.Properties())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static byte[] ApplyCipher(byte[] input, byte[] key)
    {
        var state = new byte[256];
        for (var i = 0; i < 256; i++) state[i] = (byte)i;
        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 255;
            var swap = state[i]; state[i] = state[j]; state[j] = swap;
        }
        var x = 0; var y = 0; var output = new byte[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            x = (x + 1) & 255; y = (y + state[x]) & 255;
            var swap = state[x]; state[x] = state[y]; state[y] = swap;
            output[i] = (byte)(input[i] ^ state[(state[x] + state[y]) & 255]);
        }
        return output;
    }

    private static byte[] Decompress(byte[] data)
    {
        var gzip = data.Length >= 2 && data[0] == 0x1f && data[1] == 0x8b;
        var zlib = data.Length >= 6 && (data[0] & 15) == 8 &&
                   ((data[0] << 8) + data[1]) % 31 == 0 && (data[1] & 32) == 0;
        if (!gzip && !zlib) throw new InvalidDataException();
        using (var input = new MemoryStream(data, zlib ? 2 : 0, zlib ? data.Length - 6 : data.Length))
        using (Stream stream = gzip ? (Stream)new GZipStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            var buffer = new byte[4096]; int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + count > MaximumBytes) throw new InvalidDataException();
                output.Write(buffer, 0, count);
            }
            var bytes = output.ToArray();
            if (zlib)
            {
                uint a = 1, b = 0;
                foreach (var value in bytes) { a = (a + value) % 65521; b = (b + a) % 65521; }
                var expected = ((uint)data[data.Length - 4] << 24) | ((uint)data[data.Length - 3] << 16) |
                               ((uint)data[data.Length - 2] << 8) | data[data.Length - 1];
                if (((b << 16) | a) != expected) throw new InvalidDataException();
            }
            return bytes;
        }
    }
    private static ArenaScoreReply Invalid(string detail) => new ArenaScoreReply
        { Kind = ArenaScoreReplyKind.InvalidFormat, Detail = detail };
}
