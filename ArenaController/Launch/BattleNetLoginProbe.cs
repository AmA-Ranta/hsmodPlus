using System.Diagnostics;
using System.Reflection;

namespace ArenaController;

// Read-only launcher readiness, never credentials, dialog buttons or inputs.
// UIA is already shipped with the self-contained Windows Desktop runtime.
// Keep a single background read; an unresponsive provider must not block the
// controller's UI or produce unbounded threads. Probe only at startup/wait.
internal static class BattleNetLoginProbe
{
    private static readonly object Sync = new();
    private static Task<BattleNetLoginObservation>? _pending;
    private static BattleNetLoginObservation _last;
    private static BattleNetLoginObservation _lastKnown;
    private static DateTime _next;

    // For the failed-batch baseline only, never a readiness authorization.
    internal static BattleNetLoginObservation LastKnown
    {
        get { lock (Sync) return _lastKnown; }
    }

    internal static BattleNetLoginObservation Read()
    {
        lock (Sync)
        {
            if (_pending?.IsCompleted == true)
            {
                try { _last = _pending.GetAwaiter().GetResult(); }
                catch { _last = BattleNetLoginObservation.Unknown; }
                if (_last != BattleNetLoginObservation.Unknown) _lastKnown = _last;
                _pending = null;
                _next = DateTime.UtcNow.AddSeconds(5);
            }
            if (_pending == null && DateTime.UtcNow >= _next) _pending = Task.Run(Collect);
            // Never resume on an expired Ready snapshot while a new provider
            // read is pending (or hung). A prior login requirement can remain
            // conservative until the next completed observation replaces it.
            if (_pending != null && _last == BattleNetLoginObservation.Ready)
                return BattleNetLoginObservation.Unknown;
            return _last;
        }
    }

    private static BattleNetLoginObservation Collect()
    {
        var ready = false;
        foreach (var process in Process.GetProcessesByName("Battle.net"))
        {
            using (process)
            {
                try
                {
                    var title = process.MainWindowTitle;
                    if (BattleNetWindowPolicy.HasExplicitLoginWindow(new[] { title }))
                        return BattleNetLoginWait.Classify(true, false, false);
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;
                    var state = ReadWindow(handle);
                    if (state == BattleNetLoginObservation.Required) return state;
                    ready |= state == BattleNetLoginObservation.Ready;
                }
                catch { /* Unknown provider/process state is not readiness. */ }
            }
        }
        return ready ? BattleNetLoginObservation.Ready : BattleNetLoginObservation.Unknown;
    }

    private static BattleNetLoginObservation ReadWindow(IntPtr handle)
    {
        var client = Assembly.Load("UIAutomationClient");
        var types = Assembly.Load("UIAutomationTypes");
        var element = client.GetType("System.Windows.Automation.AutomationElement", true)!;
        var property = types.GetType("System.Windows.Automation.AutomationProperty", true)!;
        var condition = client.GetType("System.Windows.Automation.PropertyCondition", true)!;
        var scope = types.GetType("System.Windows.Automation.TreeScope", true)!;
        var root = element.GetMethod("FromHandle", new[] { typeof(IntPtr) })!.Invoke(null, new object[] { handle });
        if (root == null) return BattleNetLoginObservation.Unknown;
        const BindingFlags fields = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        object Property(string name) => element.GetField(name, fields)!.GetValue(null)!;
        var read = element.GetMethod("GetCurrentPropertyValue", new[] { property })!;
        object? Value(object target, string name) => read.Invoke(target, new[] { Property(name) });
        var descendants = Enum.Parse(scope, "Descendants");
        var loginCondition = Activator.CreateInstance(condition, Property("AutomationIdProperty"), "LoginWindow");
        var login = element.GetMethod("FindFirst")!.Invoke(root, new[] { descendants, loginCondition });
        if (login != null && Value(login, "IsOffscreenProperty") is false)
            return BattleNetLoginWait.Classify(false, true, false);

        var controlType = types.GetType("System.Windows.Automation.ControlType", true)!;
        var button = controlType.GetField("Button", fields)!.GetValue(null);
        var buttonCondition = Activator.CreateInstance(condition, Property("ControlTypeProperty"), button);
        var buttons = element.GetMethod("FindAll")!.Invoke(root, new[] { descendants, buttonCondition });
        if (buttons == null) return BattleNetLoginObservation.Unknown;
        var collection = buttons.GetType();
        var count = Math.Min(128, (int)collection.GetProperty("Count")!.GetValue(buttons)!);
        for (var index = 0; index < count; index++)
        {
            var item = collection.GetProperty("Item")!.GetValue(buttons, new object[] { index });
            if (item != null && Value(item, "IsOffscreenProperty") is false &&
                Value(item, "IsEnabledProperty") is true &&
                BattleNetLoginWait.IsHearthstonePlayButton(Value(item, "NameProperty") as string))
                return BattleNetLoginWait.Classify(false, false, true);
        }
        return BattleNetLoginObservation.Unknown;
    }
}
