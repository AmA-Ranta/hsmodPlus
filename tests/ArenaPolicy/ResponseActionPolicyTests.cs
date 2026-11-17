using ArenaBoxBot;

internal static class ResponseActionPolicyTests
{
    internal static void Run()
    {
        Check(ResponseActionPolicy.IsChoiceLike("choice"), "choice must keep the native acknowledgement path");
        Check(ResponseActionPolicy.IsChoiceLike("replace"), "mulligan replace must keep the native acknowledgement path");
        Check(ResponseActionPolicy.IsChoiceLike("assemble"), "拼装 assemble selection must keep the native acknowledgement path");
        Check(ResponseActionPolicy.IsChoiceLike("assemble_choice"), "拼装变体 assemble_choice must stay on the native acknowledgement path");
        Check(ResponseActionPolicy.IsChoiceLike("play_assemble"), "拼装变体 play_assemble must stay on the native acknowledgement path");
        Check(!ResponseActionPolicy.IsChoiceLike("play_minion"), "ordinary play stays on the bounded recovery budget");
        Check(!ResponseActionPolicy.IsChoiceLike("attack"), "ordinary attack stays on the bounded recovery budget");
        Check(!ResponseActionPolicy.IsChoiceLike("end_turn"), "end turn keeps its dedicated unsubmitted-end-turn guard");
        Check(!ResponseActionPolicy.IsChoiceLike(""), "empty action is handled by the missing-recommendation gate");
        Check(!ResponseActionPolicy.IsChoiceLike("play_spell"), "specials are ordinary actions");
        Console.WriteLine("PASS: 拼装/choice/replace stay on native acknowledgement; ordinary actions stay bounded");
    }

    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException(reason);
    }
}
