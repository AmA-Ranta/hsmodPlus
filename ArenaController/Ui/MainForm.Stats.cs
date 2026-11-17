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
    private string StatsPath => HsModPath(StatsFileName);


    private LadderSummary ReadLadderSummary(string mode)
    {
        var summary = new LadderSummary();
        try
        {
            if (string.Equals(mode, "battlegrounds", StringComparison.OrdinalIgnoreCase))
                ReadBattlegroundSessionSummary(summary);
            else ReadLadderSessionSummary(mode, summary);
            // Standard/Wild use ladder_session.csv; Battlegrounds uses its
            // placement ledger bg_session.csv. Older builds and Casual may
            // expose only match.log, which remains a compatibility fallback.
            if (summary.Games == 0)
                ReadMatchLogSummary(mode, summary);
            if (string.Equals(mode, "battlegrounds", StringComparison.OrdinalIgnoreCase))
                summary.Mmr = ReadBattlegroundMmr();
            var key = _settings.HsModDirectory + "|" + mode;
            if (_lastReliableLadderGames.TryGetValue(key, out var previous) && summary.Games < previous)
                throw new InvalidDataException("战绩记录数量暂时回退，可能正在重写或切换数据文件。");
            _lastReliableLadderGames[key] = summary.Games;
            summary.Reliable = true;
            return summary;
        }
        catch { return new LadderSummary { Reliable = false, LastResult = "战绩暂不可读" }; }
    }


    private void ReadLadderSessionSummary(string mode, LadderSummary summary)
    {
        var path = HsModPath("ladder_session.csv");
        if (!File.Exists(path)) return;
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0) throw new InvalidDataException("天梯战绩文件正在更新，尚未读到表头。");
        var header = lines[0].Split(';');
        var modeIndex = Array.FindIndex(header, x => x.Trim().Equals("mode", StringComparison.OrdinalIgnoreCase));
        var resultIndex = Array.FindIndex(header, x => x.Trim().Equals("result", StringComparison.OrdinalIgnoreCase));
        var rankIndex = Array.FindIndex(header, x => x.Trim().Equals("rank_after", StringComparison.OrdinalIgnoreCase));
        var timeIndex = Array.FindIndex(header, x => x.Trim().Equals("time", StringComparison.OrdinalIgnoreCase));
        if (modeIndex < 0 || resultIndex < 0) throw new InvalidDataException("天梯战绩表头尚未完整写入。");
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = line.Split(';');
            if (fields.Length < header.Length) throw new InvalidDataException("天梯战绩记录尚未完整写入。");
            if (modeIndex < 0 || resultIndex < 0 || fields.Length <= Math.Max(modeIndex, resultIndex) || !fields[modeIndex].Trim().Equals(mode, StringComparison.OrdinalIgnoreCase)) continue;
            summary.Games++;
            ApplyResult(summary, fields[resultIndex].Trim());
            if (rankIndex >= 0 && rankIndex < fields.Length) summary.LastRank = fields[rankIndex].Trim();
            if (timeIndex >= 0 && timeIndex < fields.Length) summary.LastTime = fields[timeIndex].Trim();
        }
    }


    private void RefreshStats()
    {
        if (IsArenaMode)
        {
            var s = ReadStats();
            _stats.Text = $"竞技场累计（普通 + 地下）：{s.TotalWins} 胜 / {s.TotalLosses} 负\r\n当前轮次：{s.CurrentRunWins} 胜 / {s.CurrentRunLosses} 负（累计 {s.RunCount} 轮，最高 {s.HighestWins} 胜）\r\n累计奖励：{s.RewardsClaimed} 次　购票：{s.TicketsPurchased} 次　花费：{s.GoldSpent} 金币\r\n状态：{ArenaStatusText.DescribeRunState(s.RunActive, IsHearthstoneRunning())}";
        }
        else
        {
            var s = ReadLadderSummary(CurrentLadderMode);
            if (!s.Reliable)
                _stats.Text = $"{_settings.Mode}：战绩暂不可读，等待文件更新完成。\r\n未将临时空内容或读取失败计作新的对局结果。";
            else if (string.Equals(CurrentLadderMode, "battlegrounds", StringComparison.OrdinalIgnoreCase))
                _stats.Text = $"酒馆战棋：{s.Games} 局\r\n最近结果：{s.LastResult}\r\n战棋分数：{s.Mmr}\r\n最近时间：{s.LastTime}\r\n卡组：不适用（酒馆战棋由 HsMod 原生选择）";
            else
                _stats.Text = $"{_settings.Mode}：{s.Wins} 胜 / {s.Losses} 负 / {s.Draws} 平　共 {s.Games} 局\r\n最近结果：{s.LastResult}\r\n最近段位：{s.LastRank}\r\n最近时间：{s.LastTime}\r\n卡组：{(_deckName.Text.Length == 0 ? "未指定（使用 HsMod 当前设置）" : _deckName.Text)}";
        }
        RefreshRuntimeStatus();
    }

}
