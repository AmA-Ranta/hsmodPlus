#nullable enable
using System;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ArenaBoxBot;

/// <summary>
/// HsMod's real runtime script switch, read from its local API on a background
/// thread.  PluginConfig.scriptRuntimeEnabled only follows the arena path, so a
/// wild-ladder task reports it as false even while HsMod is playing; every
/// automation gate that relied on it would silently stay closed in wild mode.
/// </summary>
internal static class HsModScriptState
{
    private static readonly object Gate = new object();
    private static volatile bool s_enabled;
    private static volatile bool s_running;
    private static string? s_token;
    private static int s_port;

    internal static bool Enabled => s_enabled;

    /// <summary>Starts the background poller once; safe to call every frame.</summary>
    internal static void EnsureStarted()
    {
        if (s_running) return;
        lock (Gate)
        {
            if (s_running) return;
            s_running = true;
        }
        Task.Run(async () =>
        {
            while (true)
            {
                try { Poll(); } catch { }
                await Task.Delay(2000).ConfigureAwait(false);
            }
        });
    }

    private static void Poll()
    {
        var token = s_token;
        var port = s_port;
        if (token != null && port != 0)
        {
            var status = Get("http://127.0.0.1:" + port + "/api/status?token=" + token);
            if (status != null)
            {
                s_enabled = Regex.IsMatch(status, "\"scriptEnabled\"\\s*:\\s*true");
                return;
            }
            s_token = null;
        }
        for (var candidate = 58744; candidate <= 58748; candidate++)
        {
            var root = Get("http://127.0.0.1:" + candidate + "/");
            if (root == null) continue;
            var match = Regex.Match(root, "name=\"hsmod-token\"\\s+content=\"([^\"]+)\"");
            if (!match.Success) continue;
            s_token = match.Groups[1].Value;
            s_port = candidate;
            var fresh = Get("http://127.0.0.1:" + candidate + "/api/status?token=" + s_token);
            if (fresh != null) s_enabled = Regex.IsMatch(fresh, "\"scriptEnabled\"\\s*:\\s*true");
            return;
        }
        s_enabled = false;
    }

    private static string? Get(string url)
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 1500;
            request.ReadWriteTimeout = 1500;
            request.Proxy = null;
            using var response = (HttpWebResponse)request.GetResponse();
            using var reader = new System.IO.StreamReader(response.GetResponseStream());
            return reader.ReadToEnd();
        }
        catch { return null; }
    }
}