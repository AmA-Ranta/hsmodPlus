using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;

namespace ArenaBoxBot;

internal static partial class HsModNativeArenaPatch
{
    private static readonly int s_endTurnCurrentPid = ReadCurrentProcessId();
    private static Type s_endTurnAdapterType;
    private static FieldInfo s_endTurnLedgerField, s_endTurnShadowField;
    private static MethodInfo s_endTurnExecutionKey;

    private static int ReadCurrentProcessId()
    {
        using (var process = Process.GetCurrentProcess()) return process.Id;
    }

    private static bool CanPollUnsubmittedEndTurn(GameState state, object recommendation,
        Type adapter, int responseMode, out string holdReason)
    {
        holdReason = "unproven-request-or-pending-acknowledgement";
        try
        {
            if (state == null || recommendation == null || adapter == null) return false;
            if (s_endTurnAdapterType != adapter)
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                s_endTurnLedgerField = adapter.GetField("s_actionLedger", flags);
                s_endTurnShadowField = adapter.GetField("s_shadowPolicy", flags);
                s_endTurnExecutionKey = adapter.Assembly.GetType("HsMod.Utilities.BoxExecutionIdentity")?
                    .GetMethod("ExecutionKey", flags, null, new[] { recommendation.GetType() }, null);
                s_endTurnAdapterType = adapter;
            }
            // ExecutionKey reads recommendation fields only. Do not invoke
            // ReadCurrent again or call the stateful ledger IsBlocked here.
            var key = s_endTurnExecutionKey?.Invoke(null, new[] { recommendation }) as string;
            var ledger = s_endTurnLedgerField?.GetValue(null);
            var entries = ledger?.GetType().GetField("entries",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ledger) as IDictionary;
            var shadow = s_endTurnShadowField?.GetValue(null);
            var commit = shadow?.GetType().GetProperty("HasActiveCommit",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shadow, null);
            var friendly = state.GetFriendlySidePlayer();
            var opposing = state.GetOpposingSidePlayer();
            var packet = state.GetOptionsPacket();
            var type = recommendation.GetType();
            var captured = type.GetField("CapturedAt")?.GetValue(recommendation);
            var recPacket = type.GetField("OptionId")?.GetValue(recommendation);
            var recPid = type.GetField("GamePid")?.GetValue(recommendation);
            var requestId = type.GetField("RequestId")?.GetValue(recommendation) as string;
            var packetEntryPresent = false;
            if (entries != null)
            {
                foreach (var entry in entries.Values)
                {
                    var entryType = entry?.GetType();
                    const BindingFlags entryFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    var entryPacket = entryType?.GetField("PacketId", entryFlags)?.GetValue(entry);
                    var domain = entryType?.GetField("Domain", entryFlags)?.GetValue(entry) as string;
                    if (UnsubmittedEndTurnRecoveryPolicy.PacketEntryBlocks(packet?.ID ?? -1,
                        entryPacket is int && !string.IsNullOrEmpty(domain), domain,
                        entryPacket is int value ? value : -1))
                    {
                        packetEntryPresent = true;
                        break;
                    }
                }
            }
            var evidence = new EndTurnRecoveryEvidence
            {
                ActiveOwnTurn = state.IsGameCreated() && !state.IsGameOverNowOrPending() &&
                    !state.WasConcedeRequested() && !state.IsMulliganManagerActive() &&
                    friendly != null && opposing != null && friendly.GetTag(GAME_TAG.CURRENT_PLAYER) == 1 &&
                    opposing.GetTag(GAME_TAG.CURRENT_PLAYER) == 0 &&
                    s_boxStrategyMethod?.Invoke(null, null) is bool boxStrategy && boxStrategy,
                ResponseMode = responseMode,
                PacketId = packet?.ID ?? -1,
                RecommendationPacketId = recPacket is int packetId ? packetId : -1,
                GamePid = s_endTurnCurrentPid,
                RecommendationGamePid = recPid is int pid ? pid : 0,
                CapturedAgeSeconds = captured is DateTime timestamp
                    ? (DateTime.UtcNow - timestamp.ToUniversalTime()).TotalSeconds : double.NaN,
                RequestIdentityKnown = !string.IsNullOrEmpty(requestId) && !string.IsNullOrEmpty(key),
                LedgerKnown = entries != null,
                // Preserve even an expired/retry ledger entry: this helper
                // only releases requests never present in the native ledger.
                LedgerEntryPresent = entries != null && key != null && entries.Contains(key),
                CurrentPacketLedgerEntryPresent = packetEntryPresent,
                ShadowKnown = commit is bool,
                ShadowCommitActive = commit is bool active && active,
                ChoicePending = state.IsInChoiceMode() || state.GetFriendlyEntityChoices() != null
            };
            var permit = UnsubmittedEndTurnRecoveryPolicy.CanPoll(evidence);
            if (!permit)
                holdReason = "ledgerKnown=" + evidence.LedgerKnown + ",entry=" + evidence.LedgerEntryPresent +
                    ",packetEntry=" + evidence.CurrentPacketLedgerEntryPresent +
                    ",shadowKnown=" + evidence.ShadowKnown + ",commit=" + evidence.ShadowCommitActive +
                    ",choice=" + evidence.ChoicePending + ",packet=" + evidence.PacketId +
                    "/" + evidence.RecommendationPacketId + ",pid=" + evidence.GamePid +
                    "/" + evidence.RecommendationGamePid;
            return permit;
        }
        catch (Exception ex)
        {
            holdReason = "read-failed:" + ex.GetType().Name;
            return false;
        }
    }
}
