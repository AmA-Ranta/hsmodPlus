using System;
using System.Threading.Tasks;

namespace ArenaBoxBot;

public sealed class ArenaScoringReadinessGate
{
    private string _identity = "";
    private string _pendingIdentity = "";
    private Task<ArenaScoreReply> _pending;
    private DateTime _nextProbe;
    private DateTime _validUntil;
    private DateTime _lastObserved;
    private bool _sessionRejected;
    private string _detail = "等待评分接口验证";

    public bool Check(string identity, DateTime now, Func<Task<ArenaScoreReply>> probe, out string detail)
    {
        if (now < _lastObserved) { _validUntil = DateTime.MinValue; _nextProbe = DateTime.MinValue; }
        _lastObserved = now;
        if (!string.Equals(identity, _identity, StringComparison.Ordinal))
        {
            _identity = identity; _validUntil = DateTime.MinValue; _nextProbe = DateTime.MinValue;
            _sessionRejected = false; _detail = "等待评分接口验证";
        }
        if (_pending != null && _pending.IsCompleted)
        {
            try
            {
                var reply = _pending.GetAwaiter().GetResult();
                if (_pendingIdentity == _identity)
                {
                    _detail = reply.Detail;
                    _validUntil = reply.Success ? now.AddSeconds(30) : DateTime.MinValue;
                    _sessionRejected = reply.Kind == ArenaScoreReplyKind.SessionKeyMissing;
                    _nextProbe = now.AddSeconds(30);
                }
            }
            catch
            {
                if (_pendingIdentity == _identity)
                { _detail = "评分接口验证未成功"; _nextProbe = now.AddSeconds(30); }
            }
            _pending = null;
        }
        if (_validUntil > now) { detail = "ok"; return true; }
        if (!_sessionRejected && _pending == null && now >= _nextProbe)
        {
            _pendingIdentity = _identity;
            _pending = probe();
        }
        detail = _detail;
        return false;
    }
}
