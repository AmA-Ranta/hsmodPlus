using ArenaBoxBot;
using Newtonsoft.Json.Linq;

var assertions = 0;
void Equal<T>(T expected, T actual, string description)
{
    assertions++;
    if (!Equals(expected, actual)) throw new Exception(description + ": expected " + expected + ", actual " + actual);
}
void Same(string expected, string actual, string description)
{
    assertions++;
    if (!ReferenceEquals(expected, actual)) throw new Exception(description + ": original reference was changed");
}

var projection = new ArenaStatusProjection();
var status = "{\"ready\":true,\"scriptEnabled\":true,\"mode\":\"Wild\",\"deck\":\"复制机械骑\",\"updatedAt\":\"2026-10-07T07:59:00.1234567+08:00\",\"large\":123456789012345678901234567890.1234567890,\"nested\":{\"mode\":\"Wild\"},\"activity\":null}";
var expected = status.Replace("\"mode\":\"Wild\",\"deck\"", "\"mode\":\"Arena\",\"deck\"");
Equal(expected, projection.Project(status, true), "only top-level mode JSON bytes change");
Same(status, projection.Project(status, false), "leaving Arena returns untouched native snapshot");
Equal(expected, projection.Project(status, true), "same source mode transition invalidates previous cache");
var cached = projection.Project(status, true);
Same(cached, projection.Project(status, true), "unchanged source and selection reuse projection");
Same(status, projection.Project(status, false), "second exit restores original snapshot immediately");

foreach (var newline in new[] { "\n", "\r\n", "\r" })
{
    var pretty = "{" + newline + "  \"mode\" : " + newline + "    \"Wild\"," + newline + "  \"deck\":\"quote \\\" and slash \\\\ 与汉字\", \"x\": [1, 2]" + newline + "}  ";
    Equal(pretty.Replace("\"Wild\"", "\"Arena\""), projection.Project(pretty, true), "multiline offsets " + newline.Length);
}
var escaped = "{\"m\\u006fde\":\"W\\u0069ld\",\"nested\":{\"mode\":\"Standard\"}}";
Equal("{\"m\\u006fde\":\"Arena\",\"nested\":{\"mode\":\"Standard\"}}", projection.Project(escaped, true), "escaped property/value leaves unrelated bytes intact");
var changedSource = "{\"mode\":\"Wild\",\"updatedAt\":2}";
Equal("{\"mode\":\"Arena\",\"updatedAt\":2}", projection.Project(changedSource, true), "new native snapshot invalidates cache");

foreach (var bad in new[] { null, "", "not json", "{\"mode\":\"Wild\"", "{\"mode\":\"Wild\",\"nested\":[", "{\"mode\":\"Wild\"} trailing", "{\"mode\":\"Wild\",\"mode\":\"Standard\"}", "{\"mode\":null}", "{\"mode\":3}", "{\"mode\":{}}", "[]", "{\"ready\":false}", "{\"nested\":{\"mode\":\"Wild\"}}" })
{
    Same(bad, projection.Project(bad, true), "bad, ambiguous or incomplete snapshots pass through");
    Same(bad, projection.Project(bad, false), "non-Arena never alters snapshots");
}

var groups = JObject.Parse("{\"快捷键\":[{\"key\":\"keyWildLadderModeToggle\",\"description\":\"old four modes\",\"value\":\"F11\",\"other\":7},{\"key\":\"unrelated\",\"description\":\"keep\"}],\"自动脚本\":[{\"key\":\"keyWildLadderModeToggle\",\"description\":\"old\"}],\"extra\":{\"value\":1}}");
ArenaModeMetadata.UpdateShortcutDescription(groups);
var shortcut = (JObject)((JArray)groups["快捷键"])[0];
var otherShortcut = (JObject)((JArray)groups["快捷键"])[1];
var scriptShortcut = (JObject)((JArray)groups["自动脚本"])[0];
Equal(ArenaModeMetadata.ShortcutDescription, (string)shortcut["description"], "API shortcut tooltip includes fifth mode");
Equal(ArenaModeMetadata.ShortcutDescription, (string)scriptShortcut["description"], "duplicated shortcut metadata is consistent");
Equal("F11", (string)shortcut["value"], "shortcut binding preserved");
Equal(7, (int)shortcut["other"], "unrelated shortcut properties preserved");
Equal("keep", (string)otherShortcut["description"], "unrelated description preserved");
Equal(1, (int)((JObject)groups["extra"])["value"], "non-array groups preserved");
ArenaModeMetadata.UpdateShortcutDescription(null);

var arenaSnapshot = new ArenaActivitySnapshot(101, "第 4 回合 · 我方回合", "服务器已确认攻击：A#7 → B#8", 12, 9);
var native = "{\r\n  \"mode\" : \"Wild\", \"scriptEnabled\":true, \"updatedAt\":\"2026-10-08T01:02:03.0000000Z\",\r\n" +
    "  \"deck\": {\"id\":\"990\", \"name\":\"构造卡组\", \"custom\":123456789012345678901234567890.1234567890},\r\n" +
    "  \"activity\": { \"status\":\"old\", \"action\":\"起手旧动作\", \"statusKey\":\"ladder.old\", \"statusArgs\":[1, {\"nested\":2}]," +
    " \"actionKey\":\"ladder.mulligan\", \"actionArgs\":[\"keep\"], \"games\":0, \"wins\":0, \"losses\":0," +
    " \"unknown\": {\"action\":\"leave untouched\", \"status\":\"stay\", \"number\":123456789012345678901234567890.000000001}, \"raw\":\"\\u0061\" } }  ";
var projected = projection.Project(native, true, arenaSnapshot);
var parsed = JObject.Parse(projected);
Equal("Arena", (string)parsed["mode"], "activity projection retains virtual mode");
Equal(arenaSnapshot.Status, (string)parsed["activity"]["status"], "fresh Arena phase replaces ladder phase");
Equal(arenaSnapshot.Action, (string)parsed["activity"]["action"], "confirmed action replaces mulligan residue");
Equal(21, (int)parsed["activity"]["games"], "Arena lifetime games use Arena ledger values");
Equal(12, (int)parsed["activity"]["wins"], "Arena wins projected");
Equal(9, (int)parsed["activity"]["losses"], "Arena losses projected");
Equal(JTokenType.Null, parsed["activity"]["statusKey"].Type, "old status localization key cleared");
Equal(JTokenType.Null, parsed["activity"]["actionKey"].Type, "old action localization key cleared");
Equal(0, ((JArray)parsed["activity"]["actionArgs"]).Count, "old action translation arguments cleared");
Equal(true, projected.Contains("\"scriptEnabled\":true, \"updatedAt\":\"2026-10-08T01:02:03.0000000Z\""), "native flags and timestamp bytes preserved");
Equal(true, projected.Contains("\"deck\": {\"id\":\"990\", \"name\":\"构造卡组\", \"custom\":123456789012345678901234567890.1234567890}"), "deck object and high-precision numeric bytes preserved");
Equal(true, projected.Contains("\"unknown\": {\"action\":\"leave untouched\", \"status\":\"stay\", \"number\":123456789012345678901234567890.000000001}, \"raw\":\"\\u0061\""), "unknown activity fields and escapes preserved byte for byte");
Same(projected, projection.Project(native, true, arenaSnapshot), "unchanged activity version reuses cache");
var nextSnapshot = new ArenaActivitySnapshot(102, "对手回合", "回合已结束", 13, 9);
var newProjected = projection.Project(native, true, nextSnapshot);
Equal("回合已结束", (string)JObject.Parse(newProjected)["activity"]["action"], "changed activity version invalidates same-source cache");
Same(native, projection.Project(native, false, nextSnapshot), "other modes preserve complete native source reference");
Equal("起手旧动作", (string)JObject.Parse(projection.Project(native, true))["activity"]["action"], "absent Arena snapshot never invents observations");
foreach (var newline in new[] { "\n", "\r\n", "\r" })
{
    var text = "{" + newline + "\"mode\": \"Wild\", \"activity\": {" + newline +
        " \"status\" : \"old\", \"actionArgs\" : [" + newline + " 1, 2 ], \"untouched\": \"\\u0062\" } }";
    var result = JObject.Parse(projection.Project(text, true, arenaSnapshot));
    Equal(arenaSnapshot.Status, (string)result["activity"]["status"], "activity offsets support newline style " + newline.Length);
    Equal(true, projection.Project(text, true, arenaSnapshot).Contains("\"untouched\": \"\\u0062\""), "escaped unknown bytes survive newline projection");
}
foreach (var bad in new[] {
    "{\"mode\":\"Wild\",\"activity\":{\"status\":\"a\",\"status\":\"b\"}}",
    "{\"mode\":\"Wild\",\"activity\":{},\"activity\":{}}",
    "{\"mode\":\"Wild\",\"activity\":{\"action\":{\"unknown\":\"keep\"}}}",
    "{\"mode\":\"Wild\",\"activity\":{\"games\":\"unknown schema\"}}",
    "{\"mode\":\"Wild\",\"activity\":{\"status\":\"old\"}} trailing"
}) Same(bad, projection.Project(bad, true, arenaSnapshot), "ambiguous, incompatible or partial activity schemas fail closed");
var overflow = new ArenaActivitySnapshot(103, "", "", int.MaxValue, 1);
Equal<int?>(null, overflow.Games, "counter overflow cannot invent a negative game total");
var evidence = new ArenaActivityEvidence();
foreach (var processorAvailable in new[] { false, true })
foreach (var pendingHistory in new[] { false, true })
foreach (var identityChanges in new[] { false, true })
    Equal(processorAvailable && !pendingHistory && !identityChanges,
        ArenaActivityEvidence.CanReadPowerOwners(processorAvailable, pendingHistory, identityChanges),
        "ownership cannot be inferred from absent, queued or identity-changing history");
foreach (var tag in new[] { 50, 2032, 202 })
    Equal(true, ArenaActivityEvidence.IsOwnershipOrIdentityTag(tag), "native ownership and card identity tags invalidate map evidence");
foreach (var tag in new[] { 0, 49, 51, 201, 203, 2031, 2033 })
    Equal(false, ArenaActivityEvidence.IsOwnershipOrIdentityTag(tag), "unrelated tags do not invalidate stable ownership");
evidence.SetContext("game-a", true);
evidence.ObservePower("game-a", "TRIGGER", true, false, "auto", "enemy");
Equal("尚无已确认动作", evidence.Action, "automatic trigger is not a chosen action");
evidence.ObservePower("game-a", "POWER", true, false, "minion", "enemy");
Equal("尚无已确认动作", evidence.Action, "automatic minion POWER is not a hero skill");
evidence.ObservePower("game-a", "PLAY", false, false, "opponent", "friendly");
Equal("尚无已确认动作", evidence.Action, "opponent plays cannot become script activity");
evidence.ObservePower("stale-game", "ATTACK", true, false, "A", "B");
Equal("尚无已确认动作", evidence.Action, "different-game packet cannot update activity");
evidence.ObservePower("game-a", "PLAY", true, false, "A#1", "B#2");
Equal("服务器已确认出牌：A#1 → B#2", evidence.Action, "native friendly PLAY is accepted evidence");
evidence.ObservePower("game-a", "ATTACK", true, false, "A#1", "B#2");
Equal("服务器已确认攻击：A#1 → B#2", evidence.Action, "native friendly ATTACK is accepted evidence");
evidence.ObservePower("game-a", "POWER", true, true, "技能#3", "");
Equal("服务器已确认英雄技能：技能#3", evidence.Action, "hero POWER requires explicit hero-skill identity");
var confirmed = evidence.Action;
foreach (var invalid in new (int expected, int received, bool friendly, bool entities)[] { (8, 9, true, true),
    (8, 8, false, true), (8, 8, true, false), (-1, -1, true, true) })
{
    evidence.ObserveChoice("game-a", invalid.expected, invalid.received, invalid.friendly, invalid.entities, false, "wrong");
    Equal(confirmed, evidence.Action, "unrelated or unproven choice packet leaves confirmed activity intact");
}
evidence.ObserveChoice("game-a", 0, 0, true, true, true, "");
Equal("服务器已确认起手留牌", evidence.Action, "exact native zero-ID mulligan can confirm");
evidence.ObserveChoice("game-a", 8, 8, true, true, false, "卡牌#7");
Equal("服务器已确认选择：卡牌#7", evidence.Action, "exact choice identity confirms selected entity");
confirmed = evidence.Action;
evidence.ObserveCurrentPlayer("game-a", 0);
Equal(confirmed, evidence.Action, "first opponent observation cannot invent an end-turn action");
evidence.ObserveCurrentPlayer("game-a", 1);
evidence.ObserveCurrentPlayer("stale-game", 0);
Equal(confirmed, evidence.Action, "stale turn transition ignored");
evidence.ObserveCurrentPlayer("game-a", 0);
Equal("回合已结束", evidence.Action, "real friendly CURRENT_PLAYER transition proves turn ended");
evidence.SetContext("game-b", true);
Equal("尚无已确认动作", evidence.Action, "new game clears previous action");
evidence.ObservePower("game-a", "ATTACK", true, false, "A", "B");
Equal("尚无已确认动作", evidence.Action, "old game cannot revive cleared action");
evidence.SetContext("game-b", false);
evidence.ObservePower("game-b", "PLAY", true, false, "A", "");
Equal("尚无已确认动作", evidence.Action, "disabled script clears and rejects subsequent observations");
evidence.SetContext("", false);
evidence.ObserveCurrentPlayer("game-b", 1);
evidence.ObserveCurrentPlayer("game-b", 0);
Equal("尚无已确认动作", evidence.Action, "mode exit cannot retain turn state");
if (args.Length > 0)
{
    // Replay an actual saved API response without writing to game state.
    var capture = File.ReadAllText(args[0]);
    var before = JObject.Parse(capture);
    var replaySnapshot = new ArenaActivitySnapshot(201, "竞技场对战中 · 本轮 2 胜 / 0 负", "服务器已确认友方动作（测试值）", 3, 1);
    var after = JObject.Parse(projection.Project(capture, true, replaySnapshot));
    Equal(replaySnapshot.Action, (string)after["activity"]["action"], "actual API capture replaces stale mulligan action");
    Equal(4, (int)after["activity"]["games"], "actual API capture replaces constructed session count with Arena lifetime count");
    Equal(JTokenType.Null, after["activity"]["actionKey"].Type, "actual API capture clears old ladder action key");
    foreach (var property in before.Properties().Where(p => p.Name != "activity" && p.Name != "mode"))
        Equal(true, JToken.DeepEquals(property.Value, after[property.Name]), "actual API capture preserves root field " + property.Name);
    Same(capture, projection.Project(capture, false, replaySnapshot), "actual API capture remains original in other modes");
    Console.WriteLine("PASS actual saved API response replay (pure test data; no game writes)");
}
Console.WriteLine($"PASS Arena status projection ({assertions} assertions)");
