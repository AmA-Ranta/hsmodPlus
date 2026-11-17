namespace ArenaController;

/// <summary>What the controller should do when the post-switch login wait fails.</summary>
public enum LaunchRecoveryAction
{
    /// <summary>Nothing proves the client is healthy: fail the task.</summary>
    Fail,
    /// <summary>The client sits on the fatal-error screen: restart it once.</summary>
    RestartClient,
    /// <summary>The client is alive (fresh heartbeat) even though the launch
    /// chain check was slow: continue and verify the session mode.</summary>
    Continue
}

public static class ControllerLaunchRecoveryPolicy
{
    public static LaunchRecoveryAction Decide(bool heartbeatAvailable, bool heartbeatFresh, string? scene)
    {
        if (!heartbeatAvailable || !heartbeatFresh) return LaunchRecoveryAction.Fail;
        if (ControllerLobbyPolicy.IsFatalErrorScene(scene)) return LaunchRecoveryAction.RestartClient;
        return LaunchRecoveryAction.Continue;
    }
}