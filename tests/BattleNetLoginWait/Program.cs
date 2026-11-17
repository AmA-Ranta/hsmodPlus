using ArenaController;
using System.Text.Json;

var checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    checks++;
}
using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "observed-login-error.json")));
var snapshot = fixture.RootElement;
var explicitTitle = BattleNetWindowPolicy.HasExplicitLoginWindow(new[] { snapshot.GetProperty("mainTitle").GetString() });
Check(!explicitTitle, "Captured generic main title cannot reveal its internal login modal");
Check(snapshot.GetProperty("childAutomationId").GetString() == "LoginWindow", "Keep the observed child identity");
var observed = BattleNetLoginWait.Classify(explicitTitle, true, snapshot.GetProperty("gamePlayButtonEnabled").GetBoolean());
Check(observed == BattleNetLoginObservation.Required, "Visible internal login error must block official retry batches");
Check(BattleNetLoginWait.Classify(false, true, true) == BattleNetLoginObservation.Required, "An enabled background button cannot bypass a visible login modal");
Check(BattleNetLoginWait.Classify(false, false, false) == BattleNetLoginObservation.Unknown, "A missing modal and disabled Play are not authentication proof");
Check(BattleNetLoginWait.IsHearthstonePlayButton(snapshot.GetProperty("gamePlayButtonName").GetString()), "Recognize the actual localized game button");
Check(BattleNetLoginWait.IsHearthstonePlayButton("Play: Hearthstone"), "Recognize the English game button");
Check(!BattleNetLoginWait.IsHearthstonePlayButton("进入游戏"), "A news button without the game identity must not resume a task");
Check(!BattleNetLoginWait.IsHearthstonePlayButton("Play: World of Warcraft"), "Another game is not Hearthstone readiness");

var wait = new BattleNetLoginWait();
wait.Pause(observed);
var launches = 0;
for (var tick = 0; tick < 12; tick++) if (wait.TryResume(observed, false)) launches++;
Check(wait.IsWaiting && launches == 0, "Twelve periodic checks against the same login error must not create launch batches");
Check(!wait.TryResume(BattleNetLoginObservation.Unknown, false) && wait.IsWaiting, "Transient accessibility failure must keep login wait");
if (wait.TryResume(BattleNetLoginObservation.Ready, false)) launches++;
Check(launches == 1 && !wait.IsWaiting, "One real prerequisite change permits exactly one original resume path");
Check(!wait.TryResume(BattleNetLoginObservation.Ready, false), "Subsequent ready observations cannot launch duplicates");

wait.Pause(BattleNetLoginObservation.Ready);
Check(!wait.TryResume(BattleNetLoginObservation.Ready, false), "A failed batch against unchanged Ready must not loop");
Check(!wait.TryResume(BattleNetLoginObservation.Unknown, false), "Losing observability does not replenish failed retries");
Check(!wait.TryResume(BattleNetLoginObservation.Ready, false), "Unknown then the same Ready must still wait for manual retry or a real change");
Check(!wait.TryResume(BattleNetLoginObservation.Required, false), "A newly observed authentication prerequisite keeps the task paused");
Check(wait.TryResume(BattleNetLoginObservation.Ready, false), "Required-to-ready change after the failed batch permits recovery");

wait.Pause(BattleNetLoginObservation.Unknown, BattleNetLoginObservation.Ready);
Check(!wait.TryResume(BattleNetLoginObservation.Ready, false), "Expiring Ready while the next probe is pending must not replenish a failed launch batch");
Check(!wait.TryResume(BattleNetLoginObservation.Required, false), "A real subsequent login modal is still required before recovering an unchanged-ready failure");
Check(wait.TryResume(BattleNetLoginObservation.Ready, false), "The later real login change permits one recovery after probe expiration");

wait.Pause(BattleNetLoginObservation.Unknown);
Check(!wait.TryResume(BattleNetLoginObservation.Unknown, false), "No title or readable UI is not successful login");
Check(wait.TryResume(BattleNetLoginObservation.Ready, false), "First positive game readiness may resolve an unknown prerequisite");
wait.Pause(BattleNetLoginObservation.Required);
Check(wait.TryResume(BattleNetLoginObservation.Unknown, true), "A user-created game may enter the original process-owner/login validation path");
wait.Pause(BattleNetLoginObservation.Required);
wait.Reset();
Check(!wait.IsWaiting && !wait.TryResume(BattleNetLoginObservation.Ready, false), "Manual stop/reset must not be resurrected by later login readiness");
Console.WriteLine(JsonSerializer.Serialize(new { passed = checks, liveUiExecuted = false, liveGameExecuted = false, fixture = "observed-login-error.json" }));
