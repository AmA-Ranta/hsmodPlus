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

    private bool EnableConstructedScript()
    {
        if (ArenaRunStopBindingPolicy.EnableAfterBinding(_arenaRunStopPending,
            PublishArenaRunStop, () => TrySetHsModScriptRuntime(true)))
        { _scriptEnabledConfirmedAt = DateTime.Now; Append("已通过 HsMod 本地接口开启脚本。"); return true; }
        throw new InvalidOperationException("HsMod 本地接口未确认开启脚本。请检查插件加载状态；具体连接错误已保存到开发者日志。");
    }


    private bool DisableConstructedScript()
    {
        if (!IsHearthstoneRunning()) return true;
        if (TrySetHsModScriptRuntime(false)) { Append("已通过 HsMod 本地接口关闭脚本。"); return true; }
        // A toggle sent to an unknown/stale state can start a stopped script.
        // Keep the stop pending until an absolute API command is acknowledged.
        AppendDeveloper("HsMod 本地接口未确认关闭；保持停止请求，不发送不确定的 F12 切换。");
        return false;
    }


    private bool IsConstructedScriptEnabled()
    {
        try
        {
            if (!Directory.Exists(_settings.HsModDirectory)) return false;
            var paths = new[] { PreferredHsModLogPath }.Concat(Directory.Exists(_settings.HsModDirectory) ? Directory.EnumerateFiles(_settings.HsModDirectory, "LogOutput.log", SearchOption.AllDirectories).Where(p => !string.Equals(p, PreferredHsModLogPath, StringComparison.OrdinalIgnoreCase)) : Enumerable.Empty<string>());
            var lines = paths.SelectMany(p => File.ReadLines(p).TakeLast(120));
            var last = lines.LastOrDefault(x => x.Contains("脚本开关", StringComparison.OrdinalIgnoreCase));
            return last != null && (last.Contains("开启") || last.Contains("启用"));
        }
        catch { return false; }
    }


    private bool TrySetHsModScriptRuntime(bool enabled)
    {
        var client = new HsModApiClient(TimeSpan.FromSeconds(2), AppendDeveloper);
        return client.TrySetScriptEnabled(enabled);
    }




    /// <summary>
    /// HsMod can expose an empty deck list for a short period while the game is
    /// still loading the collection.  The old controller enabled the script
    /// once and then left it at the "choose a deck" warning forever.  Poll the
    /// same local read-only config endpoint that the HsMod page uses, then
    /// select the configured deck by its stable deck value after the list is
    /// ready.  This keeps the controller usable when it starts Hearthstone
    /// before the collection page has finished loading.
    /// </summary>
    private Task EnsureConfiguredConstructedDeckAsync() => EnsureConfiguredConstructedDeckCoreAsync(false);


    private async Task EnsureConfiguredConstructedDeckCoreAsync(bool force)
    {
        var limitGeneration = _taskGeneration;
        await EnsureNativeGameLimitAsync().ConfigureAwait(true);
        ThrowIfTaskInterrupted(limitGeneration);
        if (IsArenaMode || string.Equals(_settings.Mode, "酒馆战棋", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(_settings.DeckName))
            return;

        // Select the deck through HsMod's per-mode deck list (HsMod decorates
        // names with the format, so a bare name is not enough in every mode).
        var wanted = _settings.DeckName.Trim();
        // 卡组名称是用户手填文本：不依赖游戏内卡组列表（不开游戏也能配置），
        // 默认沿用上次填写的名称；为空时明确提示。
        if (string.IsNullOrWhiteSpace(wanted))
            throw new InvalidOperationException("请先填写卡组名称（会沿用上次填写的名称，如：快攻 / 海盗战）。");
        // A reconnect can land directly inside a resumed match.  Deck selection
        // only exists in the constructed lobby, so enabling the script during a
        // live match must not wait for the collection UI (it is unavailable and
        // the old code stalled until timeout, leaving the bot idle in a match).
        // A stale "Gameplay.Awake" marker in a long-lived HsMod log can make the
        // gate believe a match is running while the client sits at the hub with
        // "deck missing".  Only honour the gameplay skip when the native status
        // does not report that the configured deck is missing.
        if (!force && IsConstructedGameplayActive() && HeartbeatBlocksStopBoundary() && !await IsNativeDeckMissingAsync().ConfigureAwait(true))
        {
            AppendDeveloper("deck-gate.skipped;reason=gameplay-active");
            return;
        }
        var taskGeneration = _taskGeneration;
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        var nextWaitLog = DateTime.MinValue;
        while (DateTime.UtcNow < deadline && !_stopRequested)
        {
            ThrowIfTaskInterrupted(taskGeneration);
            if (!force && IsConstructedGameplayActive() && HeartbeatBlocksStopBoundary() && !await IsNativeDeckMissingAsync().ConfigureAwait(true))
            {
                AppendDeveloper("deck-gate.skipped;reason=gameplay-active-midwait");
                return;
            }
            var nativeStatus = await ProbeHsModStatusAsync().ConfigureAwait(true);
            var expectedMode = HsModModeMapping.ToNative(_settings.Mode);
            if (nativeStatus == null || !nativeStatus.Ready || !string.Equals(nativeStatus.Mode, expectedMode, StringComparison.OrdinalIgnoreCase))
            {
                if (DateTime.UtcNow >= nextWaitLog)
                {
                    nextWaitLog = DateTime.UtcNow.AddSeconds(15);
                    Append("等待 HsMod 应用" + _settings.Mode + "配置，再校验指定卡组…");
                }
                await Task.Delay(1500).ConfigureAwait(true);
                continue;
            }
            var snapshot = await ProbeHsModDeckOptionsAsync().ConfigureAwait(true);
            if (snapshot == null)
            {
                if (DateTime.UtcNow >= nextWaitLog)
                {
                    nextWaitLog = DateTime.UtcNow.AddSeconds(10);
                    Append("等待 HsMod 卡组接口就绪…");
                }
                await Task.Delay(1500).ConfigureAwait(true);
                continue;
            }

            if (snapshot.Pending || snapshot.Options.Count == 0)
            {
                if (DateTime.UtcNow >= nextWaitLog)
                {
                    nextWaitLog = DateTime.UtcNow.AddSeconds(10);
                    Append("等待炉石收藏卡组加载后选择“" + wanted + "”…");
                }
                await Task.Delay(1500).ConfigureAwait(true);
                continue;
            }

            var option = ResolveConfiguredConstructedDeck(snapshot, wanted, _settings.Mode ?? "");

            if (string.Equals(snapshot.CurrentValue, option.Value, StringComparison.OrdinalIgnoreCase))
            {
                Append("HsMod 已选择卡组：" + option.Label.Trim());
                return;
            }

            ThrowIfTaskInterrupted(taskGeneration);
            if (!await TrySetHsModDeckAsync(option.Value).ConfigureAwait(true))
                throw new InvalidOperationException("HsMod 卡组接口拒绝选择“" + option.Label.Trim() + "”，请检查炉石是否仍在登录或加载收藏。");
            // HTTP acceptance is not proof that Unity applied the selection.
            // Read the native selection back before allowing any script action.
            await Task.Delay(500).ConfigureAwait(true);
            var confirmed = await ProbeHsModDeckOptionsAsync().ConfigureAwait(true);
            if (confirmed != null && !confirmed.Pending && string.Equals(confirmed.CurrentValue, option.Value, StringComparison.OrdinalIgnoreCase))
            {
                Append("已确认 HsMod 卡组：" + option.Label.Trim());
                return;
            }
            await Task.Delay(1000).ConfigureAwait(true);
        }

        if (_stopRequested) throw new OperationCanceledException("选择卡组期间已停止任务。");
        throw new TimeoutException("等待炉石收藏卡组超过 3 分钟，未开启有效对局。请先进入一次收藏或检查战网登录状态。");
    }


    private async Task<bool> IsNativeDeckMissingAsync()
    {
        try
        {
            var status = await ProbeHsModStatusAsync().ConfigureAwait(true);
            var activity = status?.Activity ?? "";
            return activity.Contains("ladderStatus.deckMissing", StringComparison.OrdinalIgnoreCase) ||
                   activity.Contains("请在设置中选择当前模式的卡组", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }


    private static HsModDeckOption ResolveConfiguredConstructedDeck(HsModDeckSnapshot snapshot, string wanted, string mode)
    {
        var resolved = ConstructedDeckSelection.Resolve(
            snapshot.Options.Select(x => new ConstructedDeckOption(x.Value, x.Label)).ToArray(), wanted, mode,
            allowSingleFallback: true);
        return snapshot.Options.First(x => string.Equals(x.Value, resolved.Value, StringComparison.OrdinalIgnoreCase));
    }




    private async Task RefreshDeckSuggestionsAsync()
    {
        if (_deckChoicesRefreshPending || _deckName.DroppedDown || _running || _starting || IsArenaMode ||
            string.Equals(_settings.Mode, "酒馆战棋", StringComparison.Ordinal) || !IsHearthstoneRunning()) return;
        var mode = _settings.Mode ?? "";
        var expectedNativeMode = HsModModeMapping.ToNative(mode);
        var generation = _taskGeneration;
        _deckChoicesRefreshPending = true;
        try
        {
            var snapshot = await ProbeHsModDeckOptionsAsync().ConfigureAwait(true);
            if (IsDisposed || Disposing || _deckName.DroppedDown || _running || _starting || generation != _taskGeneration ||
                mode != _settings.Mode || !IsHearthstoneRunning() || snapshot == null || snapshot.Pending ||
                !string.Equals(snapshot.NativeMode, expectedNativeMode, StringComparison.OrdinalIgnoreCase)) return;
            var labels = snapshot.Options.Select(x => x.Label.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (_deckName.Items.Cast<string>().SequenceEqual(labels, StringComparer.Ordinal)) return;
            // Suggestions are read-only. Preserve whatever the user typed
            // while the request was in flight; never select a different deck.
            var typed = _deckName.Text;
            _deckName.BeginUpdate();
            try
            {
                _deckName.Items.Clear();
                _deckName.Items.AddRange(labels);
                _deckName.Text = typed;
                _deckChoicesNativeMode = expectedNativeMode;
            }
            finally { _deckName.EndUpdate(); }
        }
        catch (Exception ex) { AppendDeveloper("卡组列表刷新失败：" + ex.GetType().Name); }
        finally { _deckChoicesRefreshPending = false; }
    }


    private async Task<bool> TrySetHsModDeckAsync(string value)
    {
        var client = new HsModApiClient(TimeSpan.FromSeconds(3), AppendDeveloper);
        return await client.SetDeckAsync(value).ConfigureAwait(true);
    }


    private async Task EnsureNativeGameLimitAsync()
    {
        var remaining = 0;
        if (!IsArenaMode)
        {
            var current = ReadLadderSummary(CurrentLadderMode);
            remaining = ConstructedStopContract.RemainingGames(_settings.MaxTotalGames,
                _baselineLadderGames, current.Games, current.Reliable);
            if (_settings.MaxTotalGames > 0 && remaining == 0)
            {
                RequestStop("达到任务总局数");
                throw new OperationCanceledException("任务场数已完成，未重新开启脚本。");
            }
        }
        // Arena has its own persisted per-run and task quotas. Clear the
        // previous constructed native quota before activation, so a Casual
        // two-game task cannot leak its stop flag/summary into Arena.
        // Persisting HsMod.cfg does not update an already loaded client.
        // Apply and read back the native limit before every activation. On
        // recovery the native session count resets, so use remaining games.
        // Runtime configuration is the plugin's responsibility: one atomic
        // configure command, and the script is enabled only after the plugin
        // acknowledges that the native limit / Arena settings were applied.
        var configureReceipt = await ApplyRuntimeConfigurationThroughPluginAsync(
            HsModModeMapping.ToRuntime(_settings.Mode), remaining, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
        if (configureReceipt == null || !configureReceipt.Ok)
            throw new InvalidOperationException("运行配置未能通过插件应用并确认，未开启脚本。" +
                (configureReceipt == null ? "（插件未在 20 秒内回执）" : "（插件回执：" + configureReceipt.Detail + "）"));
        if (IsArenaMode) ValidateArenaRuntimeStateBeforeStart();
        Append(IsArenaMode
            ? "已通过插件应用竞技场运行配置：" + _settings.ArenaMode + "；每轮对局=" + _settings.MaxGamesPerRun +
              "；门票与金币停止保护已核验。"
            : "已通过插件应用游戏内场数限制：" + (remaining == 0 ? "不限" : "本次还可完成 " + remaining + " 局"));
    }

}
