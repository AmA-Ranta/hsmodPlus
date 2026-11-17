using System;
using System.Globalization;

namespace ArenaBoxBot;

// Refreshing the existing Box page is a request, never an acknowledgement.
// A new publication/request/options ID must not replenish this turn's budget.
internal sealed class BgInvalidOptionRefreshBudget
{
    // Server game numbers are not globally unique, even within one client
    // session. A budget belongs to the actual connection and native turn.
    internal static string TurnIdentity(int handle, string address, uint port, long clientHandle, int turn)
    {
        if (handle == 0 || string.IsNullOrWhiteSpace(address) || port == 0 || turn < 1) return null;
        return string.Join(":", handle.ToString(CultureInfo.InvariantCulture),
            address.Length.ToString(CultureInfo.InvariantCulture), address,
            port.ToString(CultureInfo.InvariantCulture), clientHandle.ToString(CultureInfo.InvariantCulture),
            turn.ToString(CultureInfo.InvariantCulture));
    }

    private string _turn;
    private double _first, _last;
    private int _attempts;

    internal bool ShouldRefresh(string gameTurn, double now, bool ownRecruitPhase)
    {
        if (!ownRecruitPhase || string.IsNullOrEmpty(gameTurn) || double.IsNaN(now) ||
            double.IsInfinity(now) || now < 0) return false;
        if (!string.Equals(_turn, gameTurn, StringComparison.Ordinal))
        {
            _turn = gameTurn;
            _first = now;
            _last = now;
            _attempts = 0;
        }
        if (now < _first || now < _last || now - _first < 2.5 ||
            _attempts >= 2 || (_attempts > 0 && now - _last < 5)) return false;
        _last = now;
        _attempts++;
        return true;
    }
}
