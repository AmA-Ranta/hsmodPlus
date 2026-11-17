using System.Text;
using System.Text.Json;
using ArenaControl.Contracts;

namespace ArenaController;

internal sealed partial class MainForm
{
    private readonly Button _stopAfterRun = new();
    private ArenaRunStopRequest? _arenaRunStopRequest;
    private bool _arenaRunStopPending;
    private string ArenaRunStopPath => HsModPath(ArenaRunStopContract.RequestFile);
    private string ArenaRunStopReceiptPath => HsModPath(ArenaRunStopContract.ReceiptFile);

    private void ConfigureArenaRunStopButton(TableLayoutPanel grid)
    {
        _stopAfterRun.Text = "竞技场本轮结束后停止";
        _stopAfterRun.Dock = DockStyle.Fill;
        _stopAfterRun.Margin = new Padding(3);
        _stopAfterRun.FlatStyle = FlatStyle.Flat;
        _stopAfterRun.BackColor = Color.FromArgb(255, 240, 240);
        _stopAfterRun.ForeColor = Color.FromArgb(250, 81, 81);
        _stopAfterRun.FlatAppearance.BorderColor = Color.FromArgb(255, 196, 196);
        _stopAfterRun.Click += (_, _) => RequestArenaRunStop();
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.Controls.Add(_stopAfterRun, 0, 1);
        grid.SetColumnSpan(_stopAfterRun, 2);
        UpdateArenaRunStopUi();
    }

    private void UpdateArenaRunStopUi()
    {
        _stopAfterRun.Visible = IsArenaSelectedInUi;
        _stopAfterRun.Enabled = IsArenaSelectedInUi && _running && !_starting && !_stopRequested && !_arenaRunStopPending;
        _stopAfterRun.Text = _arenaRunStopPending ? "本轮完成并领奖后停止（已请求）" : "竞技场本轮结束后停止";
        if (_stopAfterRun.Parent is TableLayoutPanel grid && grid.RowStyles.Count == 2)
        {
            grid.RowStyles[0].Height = IsArenaSelectedInUi ? 50 : 100;
            grid.RowStyles[1].Height = IsArenaSelectedInUi ? 50 : 0;
            if (grid.Parent?.Parent is TableLayoutPanel monitor && monitor.RowStyles.Count == 3)
                monitor.RowStyles[0].Height = IsArenaMode ? 176 : 112;
        }
    }

    private void RequestArenaRunStop()
    {
        if (!IsArenaSelectedInUi || !_running || _starting || _stopRequested || _arenaRunStopPending) return;
        _arenaRunStopPending = true;
        _arenaRunStopRequest = null;
        RemoveControlFile(ArenaRunStopReceiptPath);
        PublishArenaRunStop();
        UpdateArenaRunStopUi();
        Append("已请求竞技场本轮结束后停止：继续当前轮的对战和重选，完成领奖后停止；不会开启下一轮。本次以完整轮次代替对局数限制，定时及故障保护仍生效。");
    }

    private bool PublishArenaRunStop()
    {
        if (!_arenaRunStopPending || !IsArenaMode) return false;
        try
        {
            if (!TryReadGameProcessIdentity(out var running, out var pid, out var started) || !running) return false;
            var underground = string.Equals(_arenaMode.SelectedItem?.ToString(), "地下竞技场", StringComparison.Ordinal);
            if (_arenaRunStopRequest == null || _arenaRunStopRequest.GameProcessId != pid ||
                _arenaRunStopRequest.GameStartedAtUtcTicks != started.Ticks)
            {
                // An official reconnect keeps the user's intent and exact run
                // identity, while the previous process's request cannot act on it.
                var runKey = _arenaRunStopRequest?.RunKey ?? "";
                if (string.IsNullOrEmpty(runKey) && TryReadArenaRuntimeState(out var snapshot) &&
                    snapshot.gameProcessId == pid && snapshot.gameStartedAtUtcTicks == started.Ticks &&
                    ArenaRunStopBindingPolicy.CanPinRun(snapshot.arenaSelected, snapshot.runActive,
                        snapshot.inGameplay, snapshot.arenaState))
                    runKey = snapshot.runKey;
                _arenaRunStopRequest = new ArenaRunStopRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"), GameProcessId = pid,
                    GameStartedAtUtcTicks = started.Ticks, RequestedAtUtcTicks = DateTime.UtcNow.Ticks,
                    Underground = underground, RunKey = runKey
                };
                RemoveControlFile(ArenaRunStopReceiptPath);
            }
            if (string.IsNullOrEmpty(_arenaRunStopRequest.RunKey))
            {
                // Arm locally until the plugin publishes the identity of the
                // next run.  Publishing an empty run key makes the plugin read
                // the current "no active run" state as an already finished run
                // and stop immediately without ever playing one.
                if (File.Exists(ArenaRunStopPath)) RemoveControlFile(ArenaRunStopPath);
                AppendDeveloper("竞技场轮后停止：等待下一轮开始后再固定轮次标识");
                return false;
            }
            if (File.Exists(ArenaRunStopPath))
            {
                var existing = JsonSerializer.Deserialize<ArenaRunStopRequest>(File.ReadAllText(ArenaRunStopPath),
                    new JsonSerializerOptions { IncludeFields = true });
                if (ArenaRunStopContract.Matches(existing!, pid, started.Ticks, underground, DateTime.UtcNow.Ticks) &&
                    existing!.RequestId == _arenaRunStopRequest.RequestId &&
                    existing.RequestedAtUtcTicks == _arenaRunStopRequest.RequestedAtUtcTicks &&
                    existing.RunKey == _arenaRunStopRequest.RunKey && existing.CompletedAtUtcTicks == 0) return true;
            }
            Directory.CreateDirectory(_settings.HsModDirectory);
            var pending = ArenaRunStopPath + ".pending";
            File.WriteAllText(pending, JsonSerializer.Serialize(_arenaRunStopRequest,
                new JsonSerializerOptions { IncludeFields = true }), Encoding.UTF8);
            File.Move(pending, ArenaRunStopPath, true);
            AppendDeveloper("竞技场轮后停止请求已发布：requestId=" + _arenaRunStopRequest.RequestId +
                ";pid=" + pid + ";runKey=" + _arenaRunStopRequest.RunKey);
            return true;
        }
        catch (Exception ex) { AppendDeveloper("竞技场轮后停止请求未确认，将重试：" + ex.GetType().Name); }
        return false;
    }

    /// <summary>A controller restart must not silently drop an armed run stop:
    /// the plugin keeps honouring the request file, so the controller has to pick
    /// the pending request back up or the run would end without stopping.</summary>
    private void TryRestoreArenaRunStop()
    {
        if (_arenaRunStopPending || !IsArenaMode || !_running || _stopRequested) return;
        try
        {
            if (!File.Exists(ArenaRunStopPath)) return;
            var request = JsonSerializer.Deserialize<ArenaRunStopRequest>(File.ReadAllText(ArenaRunStopPath),
                new JsonSerializerOptions { IncludeFields = true });
            if (request == null || request.CompletedAtUtcTicks != 0) return;
            if (!TryReadGameProcessIdentity(out var running, out var pid, out var started) || !running) return;
            if (request.GameProcessId != pid || request.GameStartedAtUtcTicks != started.Ticks) return;
            _arenaRunStopRequest = request;
            _arenaRunStopPending = true;
            UpdateArenaRunStopUi();
            Append("已恢复竞技场本轮结束后停止请求（中控重启前设置，轮次 " + request.RunKey + "）。");
        }
        catch { }
    }

    private bool PollArenaRunStop()
    {
        TryRestoreArenaRunStop();
        UpdateArenaRunStopUi();
        if (!_arenaRunStopPending || !IsArenaMode || _stopRequested) return false;
        PublishArenaRunStop();
        try
        {
            if (_arenaRunStopRequest == null || !TryReadGameProcessIdentity(out var running, out var pid, out var started) || !running)
                return false;
            var receipt = JsonSerializer.Deserialize<ArenaRunStopRequest>(File.ReadAllText(ArenaRunStopReceiptPath),
                new JsonSerializerOptions { IncludeFields = true });
            if (!ArenaRunStopContract.Confirms(_arenaRunStopRequest, receipt!, pid, started.Ticks,
                _arenaRunStopRequest.Underground, DateTime.UtcNow.Ticks)) return false;
            Append("竞技场本轮及领奖已完成，正在确认脚本关闭与停止策略。");
            RequestStop("竞技场本轮完成并领奖后停止");
            return true;
        }
        catch { return false; }
    }

    private void CancelArenaRunStop()
    {
        _arenaRunStopPending = false;
        _arenaRunStopRequest = null;
        RemoveControlFile(ArenaRunStopPath);
        // Keep the receipt as evidence; a later request/start removes it.
        UpdateArenaRunStopUi();
    }
}
