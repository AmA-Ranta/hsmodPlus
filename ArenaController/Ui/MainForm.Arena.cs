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
    private string ArenaLogPath => HsModPath(ArenaLogFileName);

    private string ArenaRuntimeStatePath => HsModPath(ArenaRuntimeStateFileName);


    private bool TryReadArenaRuntimeState(out ArenaRuntimeSnapshot state)
    {
        state = new ArenaRuntimeSnapshot();
        _arenaRuntimeReadUnavailable = true;
        try
        {
            if (!File.Exists(ArenaRuntimeStatePath)) return false;
            using var stream = new FileStream(ArenaRuntimeStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            state = JsonSerializer.Deserialize<ArenaRuntimeSnapshot>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ArenaRuntimeSnapshot();
            if (state.updatedAtUnixMs <= 0) return false;
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(state.updatedAtUnixMs);
            if (age.TotalSeconds < -5 || age.TotalSeconds > 45) return false;
            _lastArenaRuntime = state;
            _lastArenaRuntimePath = ArenaRuntimeStatePath;
            _arenaRuntimeReadUnavailable = false;
            return true;
        }
        catch
        {
            state = new ArenaRuntimeSnapshot();
            return false;
        }
    }


    private bool TryReadArenaStopTransactionFacts(out bool rewardsPending, out bool purchasePending, out string? entryPhase)
    {
        rewardsPending = purchasePending = true;
        entryPhase = null;
        try
        {
            using var stream = new FileStream(StatsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (!root.TryGetProperty("RewardPending", out var rewards) ||
                !root.TryGetProperty("PurchasePending", out var purchase) ||
                !root.TryGetProperty("EntryReceipt", out var receipt) ||
                !receipt.TryGetProperty("Phase", out var phase) || phase.ValueKind != JsonValueKind.String)
                return false;
            rewardsPending = rewards.GetBoolean();
            purchasePending = purchase.GetBoolean();
            entryPhase = phase.GetString();
            return true;
        }
        catch { return false; } // Unknown receipt/IO state never authorizes closing the client.
    }

}
