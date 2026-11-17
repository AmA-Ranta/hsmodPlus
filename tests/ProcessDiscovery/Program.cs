using System.Diagnostics;
using System.Reflection;
using ArenaController;

var directory = @"D:\Hearthstone";
var residual = new GameProcessEvidence(7644, null, false, false);
var real = new GameProcessEvidence(23704, @"D:\Hearthstone\Hearthstone.exe", true, false);
void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
Require(!HearthstoneProcessDiscovery.BlocksLaunch(residual, directory), "Inaccessible windowless child cannot block official launch");
Require(HearthstoneProcessDiscovery.SelectConfigured(new[] { residual, real }, directory) == real.Id, "Residual cannot hide the real client or break API session binding");
Require(HearthstoneProcessDiscovery.SelectConfigured(new[] { real, residual }, directory) == real.Id, "Enumeration order must not decide current client");
Require(HearthstoneProcessDiscovery.BlocksLaunch(real with { HasWindow = false }, directory), "Readable configured client starting without a window still blocks duplicate launch");
foreach (var window in new bool?[] { true, null })
{
    var unknown = residual with { HasWindow = window };
    Require(HearthstoneProcessDiscovery.BlocksLaunch(unknown, directory), "Visible or unqueryable unknown client is retained");
    Require(HearthstoneProcessDiscovery.SelectConfigured(new[] { unknown, real }, directory) == 0, "Ambiguous client cannot be controlled");
}
Require(HearthstoneProcessDiscovery.SelectConfigured(new[] { real, real with { Id = 10 } }, directory) == 0, "Multiple real clients do not grant destructive identity");
Require(!HearthstoneProcessDiscovery.IsConfigured(real with { Executable = @"D:\Other\Hearthstone.exe" }, directory), "Foreign installation cannot be controlled");
Require(HearthstoneProcessDiscovery.BlocksLaunch(real with { Executable = @"D:\Other\Hearthstone.exe", HasWindow = false }, directory), "Known foreign client still prevents duplicate launch");
Require(!HearthstoneProcessDiscovery.IsConfigured(real with { Exited = true }, directory), "Exited client is excluded");
var read = typeof(HearthstoneProcessDiscovery).GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic)!;
var raw = Process.GetProcessesByName("Hearthstone");
var unsafeMetadata = typeof(HearthstoneProcessDiscovery).GetMethod("HasUnverifiedProcess", BindingFlags.Static | BindingFlags.NonPublic)!;
if (raw.Any(p => p.Id == 7644))
    Require((bool)unsafeMetadata.Invoke(null, new object[] { directory })!, "Live residual must continue to block disruptive Box configuration recovery");
try
{
    foreach (var process in raw)
    {
        var evidence = (GameProcessEvidence)read.Invoke(null, new object[] { process })!;
        Console.WriteLine($"live pid={evidence.Id};path={evidence.Executable ?? "unreadable"};window={evidence.HasWindow};blocksLaunch={HearthstoneProcessDiscovery.BlocksLaunch(evidence, directory)}");
    }
}
finally { foreach (var process in raw) process.Dispose(); }
Console.WriteLine("Process discovery regression checks passed; no process was modified.");

async Task SimulateExit(double exitAt, double timeout, double cancelAt = double.PositiveInfinity)
{
    var seconds = 0d;
    await NormalProcessExit.WaitAsync(() => seconds >= exitAt, () => seconds >= cancelAt,
        TimeSpan.FromSeconds(timeout), () => TimeSpan.FromSeconds(seconds),
        () => { seconds += 5; return Task.CompletedTask; });
}
await SimulateExit(75, 120);
try { await SimulateExit(75, 15); throw new Exception("Legacy 15-second wait should fail delayed exit"); }
catch (TimeoutException) { }
try { await SimulateExit(double.PositiveInfinity, 120); throw new Exception("A live process cannot be treated as exited at timeout"); }
catch (TimeoutException) { }
try { await SimulateExit(75, 120, 30); throw new Exception("Cancel must prevent subsequent official launch"); }
catch (OperationCanceledException) { }
try { await SimulateExit(0, 120, 0); throw new Exception("Cancellation must still win when process has just exited"); }
catch (OperationCanceledException) { }
try
{
    await NormalProcessExit.WaitAsync(() => throw new System.ComponentModel.Win32Exception("unreadable"), () => false, TimeSpan.FromSeconds(120));
    throw new Exception("Unreadable exit state cannot authorize launch");
}
catch (System.ComponentModel.Win32Exception) { }
var pendingPoll = new TaskCompletionSource();
var exitedFlag = false;
var asynchronousWait = NormalProcessExit.WaitAsync(() => exitedFlag, () => false, TimeSpan.FromSeconds(120), pause: () => pendingPoll.Task);
Require(!asynchronousWait.IsCompleted, "Normal exit wait must yield, keeping controller UI responsive");
exitedFlag = true;
pendingPoll.SetResult();
await asynchronousWait;
Console.WriteLine("Normal exit: delayed completion, legacy failure negative control, cancellation, unknown exit state, non-terminal timeout, and asynchronous yield checks passed.");
