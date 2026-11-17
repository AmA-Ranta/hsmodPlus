using System.Text;

namespace ArenaController;

/// <summary>Read each new record once, including equal timestamps and lines without a date prefix.</summary>
public sealed class NativeLogReader
{
    private string _path = "";
    private long _position;
    private DateTime _creation;
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private string _pending = "";
    public bool SessionChanged { get; private set; }
    public bool HasUnreadData { get; private set; }

    public IReadOnlyList<string> Read(string path)
    {
        SessionChanged = false;
        HasUnreadData = false;
        var info = new FileInfo(path);
        if (!info.Exists) return Array.Empty<string>();
        var reset = !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) || info.CreationTimeUtc != _creation || info.Length < _position;
        if (reset)
        {
            SessionChanged = true;
            // Existing history belongs to the source log. Only new records may
            // drive timeout recovery after attaching/restarting the controller.
            _path = path; _creation = info.CreationTimeUtc; _position = info.Length;
            _decoder = Encoding.UTF8.GetDecoder(); _pending = "";
            return Array.Empty<string>();
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Position = _position;
        var bytes = new byte[128 * 1024];
        var count = stream.Read(bytes, 0, bytes.Length);
        _position += count;
        HasUnreadData = _position < stream.Length;
        if (count == 0) return Array.Empty<string>();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(count)];
        var charCount = _decoder.GetChars(bytes, 0, count, chars, 0, false);
        var text = _pending + new string(chars, 0, charCount);
        var lastBreak = text.LastIndexOf('\n');
        if (lastBreak < 0) { _pending = text; return Array.Empty<string>(); }
        _pending = text[(lastBreak + 1)..];
        return text[..lastBreak].Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
    }
}
