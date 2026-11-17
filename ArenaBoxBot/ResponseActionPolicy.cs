using System;

namespace ArenaBoxBot;

/// <summary>
/// HsMod's native response gate must settle the protocol acknowledgement for
/// every action that submits a server choice.  Besides the classic choice and
/// mulligan replace, the newer Assemble ("拼装") selection is a GENERAL choice
/// that creates or merges a minion: releasing it early can resubmit the same
/// selection and corrupt the assembled result.  Ordinary play/attack actions
/// stay on the bounded recovery budget instead.
/// </summary>
internal static class ResponseActionPolicy
{
    internal static bool IsChoiceLike(string normalizedAction) =>
        normalizedAction is "choice" or "replace" or "assemble" ||
        normalizedAction.IndexOf("assemble", StringComparison.Ordinal) >= 0;
}
