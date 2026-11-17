using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ArenaControl.Contracts;

using ArenaController.Ui.Design;

namespace ArenaController;

internal sealed partial class MainForm : Form
{

    private string HsModPath(string file) => Path.Combine(_settings.HsModDirectory, file);


    private async Task<HsModDeckSnapshot?> ProbeHsModDeckOptionsAsync()
    {
        var client = new HsModApiClient(TimeSpan.FromSeconds(2), AppendDeveloper);
        return await client.GetDeckSnapshotAsync().ConfigureAwait(true);
    }


    private static string? ExtractHsModToken(string html) => HsModApiParser.ExtractToken(html);


    private async Task<HsModStatusSnapshot?> ProbeHsModStatusAsync()
    {
        var client = new HsModApiClient(TimeSpan.FromMilliseconds(2500), AppendDeveloper);
        return await client.GetStatusAsync(SampleGameIdentity).ConfigureAwait(false);
    }


    /// <summary>Samples the running client so a status response is only bound to
    /// the process that produced it.</summary>
    private GameIdentitySample SampleGameIdentity()
    {
        try
        {
            var known = TryReadGameProcessIdentitySafe(out var running, out var processId, out var startedAt);
            return new GameIdentitySample(known, running, processId, startedAt);
        }
        catch { return new GameIdentitySample(false, false, 0, DateTime.MinValue); }
    }

}
