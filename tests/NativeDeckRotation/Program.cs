using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Reflection.Emit;
using ArenaBoxBot;
using HarmonyLib;

internal static class Program
{
    private static int s_checks;
    private static void Check(bool value, string reason)
    {
        s_checks++;
        if (!value) throw new Exception(reason);
    }

    private static NativeDeckRotationContext Context(int mode = 0) => new NativeDeckRotationContext
    {
        Constructed = true, RuntimeEnabled = true, RotationEnabled = true, Mode = mode,
        DeckId = 9530174145, DeckName = "当前卡组", MenuReady = true
    };

    private static void Main(string[] args)
    {
        foreach (var mode in new[] { 0, 1, 2 })
        {
            var context = Context(mode);
            var policy = new HsModNativeDeckRotationPolicy();
            policy.Begin(context, context.DeckId, "对局数达到阈值", 0);
            context.Gameplay = true;
            context.MenuReady = false;
            Check(policy.Observe(context, true, 3, 180) == NativeDeckRotationAction.Wait, "Long gameplay must finish normally");
            Check(policy.MenuWaitSeconds == 0, "Gameplay cannot consume the menu loading budget");
            context.Gameplay = false;
            context.MenuReady = true;
            Check(policy.Observe(context, false, 0, 200) == NativeDeckRotationAction.Wait, "First HUB/Tournament observation starts loading wait");
            Check(policy.MenuWaitSeconds == 0, "Exit animation time cannot consume loading budget");
            Check(policy.Observe(context, true, 0, 210) == NativeDeckRotationAction.Wait && policy.Pending, "A list containing only the current deck must retain intent and block queue");
            Check(policy.Observe(context, false, 0, 220) == NativeDeckRotationAction.Wait && policy.Pending, "Temporary null collection cannot drop intent");
            Check(policy.Observe(context, true, 1, 221) == NativeDeckRotationAction.Retry, "Fresh native list with an alternative must retry before next match");
            Check(!policy.Complete(context.DeckId) && policy.Pending, "Unchanged native config cannot release queue guard");
            Check(!policy.Complete(0) && policy.Pending, "Empty native config cannot claim switch success");
            Check(policy.Complete(context.DeckId + 1) && !policy.Pending, "Successful native switch releases intent and leaves original UI stabilization intact");
        }

        foreach (var change in new Action<NativeDeckRotationContext>[]
        {
            c => c.Mode = 1, c => c.DeckId++, c => c.DeckName = "手动改牌",
            c => c.RuntimeEnabled = false, c => c.RotationEnabled = false,
            c => c.Constructed = false, c => c.StopAfterGame = true
        })
        {
            var context = Context();
            var policy = new HsModNativeDeckRotationPolicy();
            policy.Begin(context, context.DeckId, "连败达到阈值", 0);
            change(context);
            Check(policy.Observe(context, true, 1, 1) == NativeDeckRotationAction.Cancel, "Manual/mode/off/native stop takes precedence over pending rotation");
        }

        var stalled = Context();
        var timed = new HsModNativeDeckRotationPolicy();
        timed.Begin(stalled, stalled.DeckId, "threshold", 0);
        Check(timed.Observe(stalled, false, 0, 5) == NativeDeckRotationAction.Wait, "Start finite wait at first serviceable menu");
        Check(timed.Observe(stalled, true, 0, 64.9) == NativeDeckRotationAction.Wait, "No-alternative list must not fail early");
        Check(timed.Observe(stalled, false, 0, 65) == NativeDeckRotationAction.Stop && timed.Pending, "Timeout stops script without releasing the old-deck queue guard");
        timed.Restart(100);
        Check(timed.Observe(stalled, false, 0, 100) == NativeDeckRotationAction.Wait, "Explicit script restart grants a fresh finite loading wait");
        Check(timed.Observe(stalled, true, 1, 101) == NativeDeckRotationAction.Retry, "Recovered collection can complete the retained request after explicit restart");
        stalled.Gameplay = true;
        stalled.MenuReady = false;
        Check(timed.Observe(stalled, true, 1, 300) == NativeDeckRotationAction.Wait, "A resumed gameplay interval cannot be interrupted by timeout");
        stalled.Gameplay = false;
        stalled.MenuReady = true;
        Check(timed.Observe(stalled, false, 0, 500) == NativeDeckRotationAction.Wait && timed.MenuWaitSeconds == 1,
            "Returning from long manual gameplay cannot charge the exit interval to menu loading");

        // Reproduce the loading race: Tick resolved the old deck, then a fresh catalog becomes
        // ready at the queue gate and original Switch successfully changes configuration.
        var raced = new HsModNativeDeckRotationPolicy();
        var racedContext = Context();
        raced.Begin(racedContext, racedContext.DeckId, "threshold", 0);
        Check(raced.Observe(racedContext, false, 0, 0) == NativeDeckRotationAction.Wait, "Tick prefix sees temporary collection loading");
        var tickLocalDeckId = racedContext.DeckId;
        var pendingOnQueueEntry = raced.Pending;
        Check(raced.Observe(racedContext, true, 1, 1) == NativeDeckRotationAction.Retry, "Catalog becomes ready after Tick resolved old local deck");
        Check(raced.Complete(tickLocalDeckId + 1), "Original Switch updates configured ID at queue gate");
        Check(!HsModNativeDeckRotationPolicy.CanSubmitAfterService(pendingOnQueueEntry, true, raced.Pending, false),
            "Queue gate must not submit the old Tick local deck after a successful late switch");
        Check(HsModNativeDeckRotationPolicy.CanSubmitAfterService(false, true, false, false),
            "Next fresh Tick may use new config, subject to original UI and selected-ID gates");
        Check(!HsModNativeDeckRotationPolicy.CanSubmitAfterService(false, false, false, false), "Native off must not queue");
        Check(!HsModNativeDeckRotationPolicy.CanSubmitAfterService(false, true, false, true), "Failure must not queue");

        // Native SetScriptDeck writes ID/name before ConfigFile.Save. Reproduce Save throwing
        // after that program write, then an explicit off -> on and a later real manual edit.
        var failedWrite = new HsModNativeDeckRotationPolicy();
        var failedContext = Context();
        var originalPlayedId = failedContext.DeckId;
        failedWrite.Begin(failedContext, originalPlayedId, "连败达到阈值", 0);
        failedContext.DeckId++;
        failedContext.DeckName = "原生程序部分写入的新牌";
        Check(failedWrite.RetainFailedReplayIdentity(failedContext), "Native Save failure retains actual memory identity");
        Check(failedWrite.Pending && failedWrite.Matches(failedContext), "Program partial write cannot be misclassified as manual cancellation");
        Check(failedWrite.ExcludeDeckId == originalPlayedId && failedWrite.Reason == "连败达到阈值", "Partial write must preserve the played-deck exclusion and original threshold reason");
        Check(!HsModNativeDeckRotationPolicy.CanSubmitAfterService(false, true, failedWrite.Pending, true), "Save failure keeps queue closed even if in-memory config changed");
        failedWrite.Restart(100);
        Check(failedWrite.Observe(failedContext, true, 1, 100) == NativeDeckRotationAction.Retry, "Explicit restart retries native selection and persistence against retained program identity");
        failedContext.DeckId++;
        Check(failedWrite.Observe(failedContext, true, 1, 101) == NativeDeckRotationAction.Cancel, "A subsequent real manual ID change cancels the request");
        foreach (var protectedScope in new Action<NativeDeckRotationContext>[]
        {
            c => c.Constructed = false, c => c.Gameplay = true, c => c.MenuReady = false, c => c.RotationEnabled = false
        })
        {
            var scope = Context();
            protectedScope(scope);
            Check(!HsModNativeDeckRotationPolicy.CanSafelyStop(scope), "Unknown/Arena/BG/gameplay/off scope cannot change native runtime");
        }
        Check(HsModNativeDeckRotationPolicy.CanSafelyStop(Context()), "Verified constructed menu may stop script after a failed deck getter");

        Check(!HsModNativeDeckRotationPolicy.CanConfirmNativeSet(202, 101, 0, true), "Retained partial memory is not a native Set receipt");
        Check(!HsModNativeDeckRotationPolicy.CanConfirmNativeSet(202, 101, 202, false), "Unbound native entries cannot confirm selection");
        Check(!HsModNativeDeckRotationPolicy.CanConfirmNativeSet(101, 101, 101, true), "A replay selecting the played deck stays blocked");
        Check(!HsModNativeDeckRotationPolicy.CanConfirmNativeSet(303, 101, 202, true), "Readback must match this replay's native Set receipt");
        Check(HsModNativeDeckRotationPolicy.CanConfirmNativeSet(202, 101, 202, true), "A normally completed native Set and different readback confirms selection");
        Console.WriteLine("Native deck rotation policy PASS: " + s_checks + " assertions");
        if (args.Length != 3)
            throw new ArgumentException("Pass built ArenaBoxBot.dll, original HsMod.dll and game Managed directory for the real Harmony generation gate");
        try { GenerateNativeReplacement(args[0], args[1], args[2]); }
        catch (Exception error)
        {
            for (var cursor = error; cursor != null; cursor = cursor.InnerException)
            {
                Console.Error.WriteLine("GENERATION ERROR TYPE: " + cursor.GetType().FullName);
                try { Console.Error.WriteLine("MESSAGE: " + cursor.Message); } catch { }
                try { Console.Error.WriteLine("SOURCE: " + cursor.Source); } catch { }
                try
                {
                    foreach (var frame in new System.Diagnostics.StackTrace(cursor, false).GetFrames() ?? new System.Diagnostics.StackFrame[0])
                    {
                        var method = frame.GetMethod();
                        Console.Error.WriteLine("FRAME: " + method?.DeclaringType?.FullName + "::" + method?.Name);
                    }
                }
                catch { }
            }
            Environment.ExitCode = 1;
        }
    }

    private static void GenerateNativeReplacement(string pluginPath, string nativePath, string managedDirectory)
    {
        var roots = new[] { Path.GetDirectoryName(Path.GetFullPath(pluginPath)), Path.GetDirectoryName(Path.GetFullPath(nativePath)),
            Path.GetFullPath(managedDirectory), @"D:\Hearthstone\BepInEx\core" };
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            var name = new AssemblyName(request.Name).Name + ".dll";
            var file = roots.Select(root => Path.Combine(root, name)).FirstOrDefault(File.Exists);
            return file == null ? null : Assembly.LoadFrom(file);
        };
        Console.WriteLine("Loading original native assembly");
        var native = Assembly.LoadFrom(Path.GetFullPath(nativePath));
        Console.WriteLine("Loading built plugin assembly");
        var plugin = Assembly.LoadFrom(Path.GetFullPath(pluginPath));
        Console.WriteLine("Resolving actual Tick metadata");
        var bot = native.GetType("HsMod.WildLadderBot", true);
        var tick = bot.GetMethod("Tick", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Console.WriteLine("Resolving production transpiler metadata");
        var helper = plugin.GetType("ArenaBoxBot.HsModNativeDecks", true);
        var transpiler = helper.GetMethod("QueueTranspiler", BindingFlags.Static | BindingFlags.NonPublic);
        Check(tick != null && transpiler != null, "Production queue transpiler wiring must exist");
        // The console CLR cannot detour Unity ECall methods. Inspect the full
        // native body with Harmony's reader and apply the real production
        // transpiler; live Unity installation is a separate required check.
        var original = PatchProcessor.GetOriginalInstructions(tick, out ILGenerator generator);
        var originalInstructions = original.ToArray();
        var originalLabels = original.SelectMany(x => x.labels).ToArray();
        var originalBlocks = original.SelectMany(x => x.blocks).ToArray();
        var replacement = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { original, generator, tick })).ToList();
        Check(replacement.Count == originalInstructions.Length + 3, "Queue protection must add exactly three instructions");
        Check(originalInstructions.All(x => replacement.Contains(x)), "Every native instruction remains unchanged");
        Check(replacement.SelectMany(x => x.blocks).SequenceEqual(originalBlocks), "All native EH boundary markers remain in order");
        Check(originalLabels.All(label => replacement.SelectMany(x => x.labels).Contains(label)), "All original branch labels remain present");
        var guard = replacement.FindIndex(x => x.opcode == OpCodes.Call && x.operand is MethodInfo m && m.Name == "CanSubmitConstructedMatch");
        Check(guard > 0 && replacement[guard - 1].opcode == OpCodes.Stfld, "Gate runs at reviewed empty stack boundary");
        Check(replacement[guard + 1].opcode == OpCodes.Brtrue && replacement[guard + 2].opcode == OpCodes.Ret,
            "Failed gate exits before native queue");
        Check(replacement[guard + 3].labels.Contains((Label)replacement[guard + 1].operand), "Allowed gate returns to original Time getter");
        Check(replacement[guard + 3].operand is MethodInfo getter && getter.Name == "get_realtimeSinceStartup" &&
            replacement[guard + 4].operand is MethodInfo begin && begin.Name == "BeginMatchSubmission", "Original native submission follows allowed gate");
        var depth = 0;
        foreach (var instruction in replacement.Take(guard))
            foreach (var block in instruction.blocks)
            {
                if (block.blockType == ExceptionBlockType.BeginExceptionBlock) depth++;
                if (block.blockType == ExceptionBlockType.EndExceptionBlock) depth--;
            }
        Check(depth == 0, "Queue gate ret must be outside all native EH regions");
        Console.WriteLine("PASS: supplied original full IL processed by production Harmony transpiler; native instructions, branches and EH preserved; live Unity detour validation still required");
    }
}
