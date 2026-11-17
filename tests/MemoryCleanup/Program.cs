using ArenaBoxBot;

void Require(bool value, string name) { if (!value) throw new Exception(name); }
var count = 0;
foreach (var scene in new[] { "GAMEPLAY", "DRAFT", "LOGIN", "LOADING", "UNKNOWN", "", null })
{
    var policy = new MemoryCleanupPolicy();
    for (var i = 0; i < 100; i++) Require(!policy.Observe(scene, false, false, i * 0.25), "Never evict resources in " + scene);
    count++;
}
foreach (var scene in new[] { "HUB", "TOURNAMENT" })
{
    var policy = new MemoryCleanupPolicy();
    for (var i = 0; i < 8; i++) Require(!policy.Observe(scene, false, false, i * 0.25), "Wait for stable menu");
    Require(policy.Observe(scene, false, false, 2), "Run native cleanup after quiet menu");
    Require(!policy.Observe(scene, false, true, 2.1), "Queueing resets quiet window");
    Require(!policy.Observe(scene, false, false, 2.2), "Do not reuse pre-queue window");
    Require(!policy.Observe(scene, true, false, 2.3), "Transition resets quiet window");
    Require(!policy.Observe(scene, false, false, 2.4), "Do not reuse pre-transition window");
    Require(!policy.Observe(scene, false, false, 100), "Sampling gap resets quiet window");
    Require(!policy.Observe(scene, false, false, 99), "Backwards clock resets quiet window");
    Require(!policy.Observe(scene, false, false, double.NaN), "Unknown clock denied");
    count++;
}
Console.WriteLine("Memory cleanup boundary tests passed: " + count + " scene cases, quiet window, queue, transition, lost samples, clock reset.");
var window = new FrameTimingWindow();
Require(!window.Observe(0), "First frame is a baseline");
for (var i = 1; i <= 100; i++) Require(!window.Observe(i * .01), "No per-frame report");
Require(window.Observe(30), "Aggregate after 30 seconds");
Require(window.Frames == 101 && window.SlowFrames == 1 && window.SevereFrames == 1 && window.MaximumMs == 29000, "Stall included in bounded aggregate");
Require(!window.Observe(31), "New window continues");
Require(!window.Observe(0), "Backwards clock resets timing");
Require(!window.Observe(double.NaN) && !window.Observe(1), "Invalid timing cannot contaminate next window");
Console.WriteLine("Passive frame timing aggregation checks passed; no rendering or native action calls.");
var cacheClock = 0d;
var loadCount = 0;
var configured = true;
var cache = new TimeCachedBoolean(() => { ++loadCount; return configured; }, () => cacheClock);
for (var i = 0; i < 10000; i++) Require(cache.Read(), "Cached mode must retain exact read result");
Require(loadCount == 1, "High-frequency getters must not reread configuration per call");
configured = false;
cacheClock = .25;
Require(!cache.Read() && loadCount == 2, "External mode changes must be seen within the bounded lifetime");
configured = true;
cache.Invalidate();
Require(cache.Read() && loadCount == 3, "Manual mode setter must invalidate immediately");
cacheClock = .1;
Require(cache.Read() && loadCount == 4, "Backwards clock must not retain stale mode");
var parallelLoads = 0;
var parallel = new TimeCachedBoolean(() => { Interlocked.Increment(ref parallelLoads); Thread.Sleep(1); return true; }, () => 0);
Parallel.For(0, 1000, _ => Require(parallel.Read(), "Concurrent main-thread/API getters agree"));
Require(parallelLoads == 1, "Concurrent cache refresh must be single-flight");
var failClock = 0d;
var failReads = 0;
var failing = new TimeCachedBoolean(() => ++failReads == 1 ? true : throw new IOException("unavailable"), () => failClock);
Require(failing.Read(), "Initial mode read");
failClock = 1;
for (var i = 0; i < 2; i++)
{
    try { failing.Read(); throw new Exception("Read failure must not return stale mode"); }
    catch (IOException) { }
}
Require(failReads == 3, "A failed refresh cannot become cached authority");
Console.WriteLine("Mode configuration cache: 10000 getters/one read, bounded external refresh, immediate invalidation, clock reset, concurrent single-flight, stale-read failure checks passed.");
