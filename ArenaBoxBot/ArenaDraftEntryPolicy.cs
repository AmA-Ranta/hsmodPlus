using System;

namespace ArenaBoxBot;

internal enum ArenaDraftEntryAction
{
    Wait,
    OpenDraftPage,
    WaitForNativeRedraft,
    Ready
}

/// <summary>
/// The server's DRAFTING state is independent of the client's visible page.
/// Cached choices on a landing page cannot be clicked or considered loaded.
/// REDRAFTING has its own TriggerRedraft entry and must never use the ordinary
/// landing continuation, which would set the client back to ordinary Draft.
/// </summary>
internal static class ArenaDraftEntryPolicy
{
    internal static bool IsSelectionState(string serverState) =>
        serverState == "DRAFTING" || serverState == "REDRAFTING";

    internal static string SelectionClient(bool underground, bool redrafting) =>
        (underground ? "Underground_" : "Normal_") + (redrafting ? "Redraft" : "Draft");

    internal static ArenaDraftEntryAction Decide(string serverState, string clientState,
        bool underground, bool modeMatches, bool landingReady, bool selectionShown, bool selectionStable)
    {
        if (!IsSelectionState(serverState) || !modeMatches) return ArenaDraftEntryAction.Wait;
        var prefix = underground ? "Underground_" : "Normal_";
        if (clientState == prefix + "Landing")
            return serverState == "REDRAFTING" ? ArenaDraftEntryAction.WaitForNativeRedraft :
                (landingReady ? ArenaDraftEntryAction.OpenDraftPage : ArenaDraftEntryAction.Wait);
        return clientState == SelectionClient(underground, serverState == "REDRAFTING") &&
            selectionShown && selectionStable ? ArenaDraftEntryAction.Ready : ArenaDraftEntryAction.Wait;
    }

    internal static bool CanUseTicketEntry(string serverState, bool nativeRunActive, bool trackedRunActive,
        int tickets, int ticketCost, bool landingReady, bool modeMatches, bool verifiedEmptySession)
    {
        return serverState == "NO_RUN" && !nativeRunActive && !trackedRunActive && ticketCost > 0 &&
            tickets >= ticketCost && landingReady && modeMatches && verifiedEmptySession;
    }

    internal static bool IsEntryPersisted(bool expectedUnderground, long expectedAtUnixMs,
        bool savedUnderground, long savedAtUnixMs, long savedModeAtUnixMs, bool contextsInitialized)
    {
        return expectedAtUnixMs > 0 && contextsInitialized && savedUnderground == expectedUnderground &&
            savedAtUnixMs == expectedAtUnixMs && savedModeAtUnixMs == expectedAtUnixMs;
    }
}

/// <summary>Retry only a continuation event, never a financial entry request.</summary>
internal sealed class ArenaDraftEntryRetry
{
    internal const float RetrySeconds = 8f;
    internal const int MaximumAttempts = 3;
    private string _context;
    private float _requestedAt = -1f;
    internal int Attempts { get; private set; }

    internal void Observe(string context)
    {
        if (_context == context) return;
        Reset();
        _context = context;
    }

    internal bool CanRequest(float now) => _context != null && Attempts < MaximumAttempts &&
        (_requestedAt < 0f || now - _requestedAt >= RetrySeconds);

    internal bool IsExhausted(float now) => _context != null && Attempts >= MaximumAttempts &&
        now - _requestedAt >= RetrySeconds;

    internal void Requested(float now)
    {
        Attempts++;
        _requestedAt = now;
    }

    internal void Reset()
    {
        _context = null;
        _requestedAt = -1f;
        Attempts = 0;
    }
}
