using System;
using System.Collections;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ArenaBoxBot;

/// <summary>Reads existing queue objects once a second. Never advances or cancels a task.</summary>
internal static class PowerQueueDiagnostics
{
    private static readonly PowerQueueProgressWindow s_progress = new PowerQueueProgressWindow();
    private static readonly FieldInfo s_pending = typeof(PowerTaskList).GetField("m_pendingTasks", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo s_artificialPause = typeof(PowerProcessor).GetField("m_artificialPauseFromMetadata", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo s_blockingSpells = typeof(GameState).GetField("m_serverBlockingSpells", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo s_blockingControllers = typeof(GameState).GetField("m_serverBlockingSpellControllers", BindingFlags.Instance | BindingFlags.NonPublic);
    private static float s_nextSample;

    internal static void ObserveFrame()
    {
        var now = Time.realtimeSinceStartup;
        if (now < s_nextSample) return;
        s_nextSample = now + 1f;
        try
        {
            var state = GameState.Get();
            var processor = state?.GetPowerProcessor();
            if (SceneMgr.Get()?.GetMode().ToString() != "GAMEPLAY" || processor == null || !state.IsGameCreated())
            {
                s_progress.Observe(null, now);
                return;
            }
            var queue = processor.GetPowerQueue();
            var current = processor.GetCurrentTaskList();
            var head = queue != null && queue.Count > 0 ? queue.Peek() : null;
            var pending = current == null ? null : s_pending?.GetValue(current);
            // Exclude queue length: receiving additional server events must
            // not hide a stalled head. Pending task completion is progress.
            var key = current == null && head == null ? null :
                GameMgr.Get()?.GetGameHandle() + ":" + state.GetTurn() + ":" +
                current?.GetId() + ":" + pending + ":" + head?.GetId();
            if (!s_progress.Observe(key, now)) return;
            var history = HistoryManager.Get();
            var choices = ChoiceCardMgr.Get();
            var friendlyId = state.GetFriendlyPlayerId();
            var opposing = state.GetOpposingSidePlayer();
            ArenaBoxBotPlugin.LogSource?.LogWarning("Power队列等待诊断: game=" + GameMgr.Get()?.GetGameHandle() +
                "; turn=" + state.GetTurn() + "; optionsId=" + state.GetOptionsPacket()?.ID +
                "; unchangedSeconds=" + s_progress.StalledSeconds.ToString("0.0") +
                "; queued=" + queue?.Count + "; current=" + Describe(current) + "; head=" + Describe(head) +
                "; pending=" + pending + "; clientBusy=" + state.IsBusy() +
                "; mulliganBusy=" + state.IsMulliganBusy() + "; historyBlocking=" + processor.IsHistoryBlocking() +
                "; bigCard=" + (history != null && history.IsShowingBigCard()) +
                "; historyTask=" + processor.GetHistoryBlockingTaskList()?.GetId() +
                "; artificialPause=" + s_artificialPause?.GetValue(processor) +
                "; blockingSpells=" + DescribeBlockers(s_blockingSpells?.GetValue(state) as IList) +
                "; blockingControllers=" + DescribeBlockers(s_blockingControllers?.GetValue(state) as IList) +
                "; friendlyChoice=" + state.GetFriendlyEntityChoices()?.ID +
                "; opposingChoice=" + state.GetOpponentEntityChoices()?.ID +
                "; friendlyPreChoiceTask=" + choices?.GetPreChoiceTaskList(friendlyId)?.GetId() +
                "; opposingPreChoiceTask=" + (opposing == null ? "unknown" : choices?.GetPreChoiceTaskList(opposing.GetPlayerId())?.GetId().ToString()) +
                "; friendlyChoiceWaiting=" + choices?.IsWaitingToStartChoices(friendlyId) +
                "; opposingChoiceWaiting=" + (opposing == null ? "unknown" : choices?.IsWaitingToStartChoices(opposing.GetPlayerId()).ToString()) +
                "; responseMode=" + state.GetResponseMode() +
                "; friendlyCurrent=" + state.GetFriendlySidePlayer()?.GetTag(GAME_TAG.CURRENT_PLAYER) +
                "; opposingCurrent=" + state.GetOpposingSidePlayer()?.GetTag(GAME_TAG.CURRENT_PLAYER) +
                "; readOnly=true; no queue or animation changes");
        }
        catch { /* Diagnostics never change gameplay or propagate failures into Update. */ }
    }

    private static string Describe(PowerTaskList list)
    {
        if (list == null) return "none";
        var block = list.GetBlockStart();
        var subSpell = list.GetSubSpellStart();
        var sourceId = block?.Entities != null && block.Entities.Count > 0 ? block.Entities[0].ToString() : "none";
        return list.GetId() + "/" + block?.BlockType + "/source=" + sourceId + "/" + subSpell?.SpellPrefabGUID;
    }

    private static string DescribeBlockers(IList blockers)
    {
        if (blockers == null) return "unavailable";
        var text = new StringBuilder().Append(blockers.Count).Append('[');
        // The game's existing blocking lists only: no scene-wide object scan
        // or full object dump. Keep destroyed entries visible without removing them.
        for (var i = 0; i < Math.Min(3, blockers.Count); ++i)
        {
            if (i > 0) text.Append('|');
            var item = blockers[i] as UnityEngine.Object;
            if (item == null) text.Append("destroyed-or-null");
            else text.Append(item.GetType().Name).Append(':').Append(item.name);
        }
        return text.Append(']').ToString();
    }
}
