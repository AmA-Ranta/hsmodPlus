using System;
using ArenaController;

internal static class HsModApiParserTests
{
    internal static void Run()
    {
        Check(HsModApiParser.ExtractToken("<meta name=\"hsmod-token\" content=\"abc123\">") == "abc123",
            "token is read when the name attribute comes first");
        Check(HsModApiParser.ExtractToken("<meta content='xyz789' name='hsmod-token'>") == "xyz789",
            "token is read when the content attribute comes first");
        Check(HsModApiParser.ExtractToken("<html></html>") == null, "missing token yields null");
        Check(HsModApiParser.ExtractToken(null) == null, "null html yields null");

        Check(HsModApiParser.ParseStatus("{\"ready\":true}", true, 10, DateTime.MinValue) == null,
            "status without scriptEnabled is unknown, never a confirmed stop");
        var status = HsModApiParser.ParseStatus(
            "{\"ready\":true,\"loggedIn\":true,\"pluginEnabled\":true,\"scriptEnabled\":true," +
            "\"mode\":\"Wild\",\"activity\":\"第 3 回合\",\"updatedAt\":\"2026-11-04T02:00:00\"}",
            true, 4242, new DateTime(2026, 11, 4, 1, 0, 0, DateTimeKind.Utc));
        Check(status != null && status.Ready && status.LoggedIn && status.ScriptEnabled, "status flags parse");
        Check(status!.Mode == "Wild" && status.Activity == "第 3 回合", "status mode and activity parse");
        Check(status.GameProcessId == 4242, "same-game status keeps the sampled pid");
        var otherGame = HsModApiParser.ParseStatus(
            "{\"scriptEnabled\":false}", false, 4242, DateTime.MinValue);
        Check(otherGame != null && otherGame.GameProcessId == 0 && otherGame.ScriptEnabled == false,
            "different-game status drops the pid and keeps script state");

        var deck = HsModApiParser.ParseDeckSnapshot(
            "{\"groups\":{\"mode\":[{\"key\":\"wildLadderPlayMode\",\"value\":\"wild\"}]," +
            "\"deck\":[{\"key\":\"wildLadderDeckName\",\"value\":\"快攻\",\"deckPending\":true," +
            "\"deckOptions\":[{\"value\":\"快攻\",\"label\":\"快攻 (Wild)\"}]}]}}");
        Check(deck != null && deck.NativeMode == "wild", "deck snapshot reads the native mode");
        Check(deck!.CurrentValue == "快攻" && deck.Pending, "deck snapshot reads value and pending flag");
        Check(deck.Options.Count == 1 && deck.Options[0].Value == "快攻", "deck options are parsed");
        Check(HsModApiParser.ParseDeckSnapshot("{\"groups\":{}}") == null, "deck snapshot without the key is null");
        Console.WriteLine("PASS: HsMod api parser (12 assertions)");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
