namespace ArenaController;

public static class ScriptActivationGate
{
    /// <summary>Startup and recovery share the same prerequisite and cancellation boundary.</summary>
    public static async Task<bool> EnableAsync(Func<Task> validateSelection, Action confirmTaskStillWanted, Func<bool> enable)
    {
        confirmTaskStillWanted();
        await validateSelection().ConfigureAwait(true);
        confirmTaskStillWanted();
        return enable();
    }
}
