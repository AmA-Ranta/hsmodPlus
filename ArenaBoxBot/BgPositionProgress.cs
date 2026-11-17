using System;

namespace ArenaBoxBot;

/// <summary>Options/publication changes do not acknowledge a board move.</summary>
internal sealed class BgPositionProgress
{
    internal enum Decision { Allow, AwaitBoard, RetryLimit }
    private string _game, _board;
    private int _turn = -1;
    private double _sentAt;
    private bool _waitNoted, _limitNoted;
    internal int Attempts { get; private set; }
    internal int Source { get; private set; }
    internal int From { get; private set; }
    internal int To { get; private set; }

    internal Decision Observe(string game, int turn, string board, double now, out bool firstNotice)
    {
        firstNotice = false;
        if (!Select(game, turn, board) || Attempts == 0) return Decision.Allow;
        if (Attempts >= 2)
        {
            firstNotice = !_limitNoted;
            _limitNoted = true;
            return Decision.RetryLimit;
        }
        if (now - _sentAt < 2d)
        {
            firstNotice = !_waitNoted;
            _waitNoted = true;
            return Decision.AwaitBoard;
        }
        return Decision.Allow;
    }

    internal void Sent(string game, int turn, string board, int source, int from, int to, double now)
    {
        if (source <= 0 || from < 1 || from > 7 || to < 1 || to > 7 || from == to ||
            !Select(game, turn, board)) return;
        ++Attempts;
        Source = source; From = from; To = to; _sentAt = now;
    }

    private bool Select(string game, int turn, string board)
    {
        if (string.IsNullOrEmpty(game) || turn < 1 || string.IsNullOrEmpty(board)) return false;
        if (_game != game || _turn != turn || _board != board)
        {
            _game = game; _turn = turn; _board = board;
            Attempts = Source = From = To = 0;
            _waitNoted = _limitNoted = false;
        }
        return true;
    }
}
