using System.Text;
using System.Text.Json;
using ArenaControl.Contracts;

namespace ArenaController;

/// <summary>
/// The controller only performs atomic operations through this single command
/// file: stop the script (with a boundary), or return to the main lobby.
/// Everything else is monitoring plus those actions.
/// </summary>
internal sealed partial class MainForm
{
    private ControllerCommand? _controllerCommand;

    private string ControllerCommandPath => HsModPath(ControllerCommandContract.CommandFile);
    private string ControllerCommandReceiptPath => HsModPath(ControllerCommandContract.ReceiptFile);

    /// <summary>The controller asks the plugin to apply runtime configuration
    /// (native game limit, Arena run settings) and waits for its receipt.</summary>
    private async Task<ControllerCommand?> ApplyRuntimeConfigurationThroughPluginAsync(
        string mode, int gameLimit, TimeSpan timeout)
    {
        try
        {
            if (!TryReadGameProcessIdentity(out var running, out var pid, out var started) || !running) return null;
            RemoveControlFile(ControllerCommandReceiptPath);
            var command = new ControllerCommand
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Action = ControllerCommandContract.ActionConfigure,
                Boundary = ControllerCommandContract.BoundaryImmediate,
                Note = "运行期配置",
                Mode = mode,
                GameLimit = gameLimit,
                ArenaMode = _settings.ArenaMode,
                ArenaMaxGamesPerRun = _settings.MaxGamesPerRun,
                ArenaStopOnTicket = _settings.StopOnTicketShortage,
                ArenaStopOnGold = _settings.StopOnGoldShortage,
                GameProcessId = pid,
                GameStartedAtUtcTicks = started.Ticks,
                IssuedAtUtcTicks = DateTime.UtcNow.Ticks
            };
            _controllerCommand = command;
            Directory.CreateDirectory(_settings.HsModDirectory);
            var pending = ControllerCommandPath + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(pending, JsonSerializer.Serialize(command,
                    new JsonSerializerOptions { IncludeFields = true }), Encoding.UTF8);
                File.Move(pending, ControllerCommandPath, true);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250).ConfigureAwait(true);
                if (!File.Exists(ControllerCommandReceiptPath)) continue;
                ControllerCommand? receipt;
                try
                {
                    receipt = JsonSerializer.Deserialize<ControllerCommand>(
                        File.ReadAllText(ControllerCommandReceiptPath),
                        new JsonSerializerOptions { IncludeFields = true });
                }
                catch { continue; }
                if (receipt == null) continue;
                if (!ControllerCommandContract.Confirms(command, receipt, pid, started.Ticks, DateTime.UtcNow.Ticks)) continue;
                AppendDeveloper("插件运行配置回执：" + (receipt.Ok ? "成功" : "失败：" + receipt.Detail));
                return receipt;
            }
            AppendDeveloper("插件运行配置回执超时");
            return null;
        }
        catch (Exception ex)
        {
            AppendDeveloper("插件运行配置异常：" + ex.GetType().Name);
            return null;
        }
    }
    /// <summary>Arena start refuses to begin while an existing run sits on a
    /// rewards or deck-edit page, because the next ticket would be spent.</summary>
    private void ValidateArenaRuntimeStateBeforeStart()
    {
        var hasRuntimeState = TryReadArenaRuntimeState(out var runtimeState);
        var identityOk = hasRuntimeState &&
            TryReadGameProcessIdentity(out var running, out var pid, out var started) && running &&
            runtimeState.gameProcessId == pid &&
            runtimeState.gameStartedAtUtcTicks == started.Ticks &&
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - runtimeState.updatedAtUnixMs is >= 0 and <= 5000;
        var stateTrusted = identityOk && runtimeState.runtimeEnabled;
        if (!stateTrusted) return;
        if (runtimeState.arenaState is not ("REWARDS" or "IN_REWARDS" or "EDITING_DECK")) return;
        var label = runtimeState.arenaState == "EDITING_DECK" ? "编辑卡组页" : "领奖页";
        throw new InvalidOperationException("当前竞技场已有进行中的轮次，客户端停在" + label +
            "（轮次 " + runtimeState.runKey + "，本局 " + runtimeState.gamesThisRun + " 场）。" +
            "请先在游戏内完成领奖/结束该轮，或在插件里关闭竞技场运行后再开始。");
    }
    private void PublishControllerCommand(string action, string boundary, string note)
    {
        try
        {
            if (!TryReadGameProcessIdentity(out var running, out var pid, out var started) || !running) return;
            if (_controllerCommand == null || _controllerCommand.Action != action ||
                _controllerCommand.Boundary != boundary || _controllerCommand.GameProcessId != pid ||
                _controllerCommand.GameStartedAtUtcTicks != started.Ticks)
            {
                // A new intent (or a replaced client process) replaces the request.
                RemoveControlFile(ControllerCommandReceiptPath);
                _controllerCommand = new ControllerCommand
                {
                    RequestId = Guid.NewGuid().ToString("N"), Action = action, Boundary = boundary,
                    Note = note ?? "", GameProcessId = pid, GameStartedAtUtcTicks = started.Ticks,
                    IssuedAtUtcTicks = DateTime.UtcNow.Ticks
                };
            }
            Directory.CreateDirectory(_settings.HsModDirectory);
            var pending = ControllerCommandPath + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(pending, JsonSerializer.Serialize(_controllerCommand,
                    new JsonSerializerOptions { IncludeFields = true }), Encoding.UTF8);
                File.Move(pending, ControllerCommandPath, true);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            AppendDeveloper("中控命令已发布：" + action + "/" + boundary + "；requestId=" + _controllerCommand.RequestId);
        }
        catch (Exception ex) { AppendDeveloper("中控命令发布失败，将重试：" + ex.GetType().Name); }
    }

    private bool HasControllerCommandReceipt()
    {
        try
        {
            if (_controllerCommand == null) return false;
            if (!TryReadGameProcessIdentity(out var running, out var pid, out var started) || !running) return false;
            if (!File.Exists(ControllerCommandReceiptPath)) return false;
            var receipt = JsonSerializer.Deserialize<ControllerCommand>(
                File.ReadAllText(ControllerCommandReceiptPath),
                new JsonSerializerOptions { IncludeFields = true });
            return ControllerCommandContract.Confirms(_controllerCommand, receipt!, pid, started.Ticks, DateTime.UtcNow.Ticks);
        }
        catch { return false; }
    }

    private void ClearControllerCommand()
    {
        _controllerCommand = null;
        RemoveControlFile(ControllerCommandPath);
        RemoveControlFile(ControllerCommandReceiptPath);
    }
}