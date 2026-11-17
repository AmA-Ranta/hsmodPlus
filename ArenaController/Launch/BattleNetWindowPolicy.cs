namespace ArenaController;

public static class BattleNetWindowPolicy
{
    // A background/tray launcher exposes no main window. Absence of a title
    // does not establish a login page or an authenticated session. Let the
    // official launch request and the game's real login gate settle that.
    public static bool HasExplicitLoginWindow(IEnumerable<string?> titles)
        => titles.Any(title => !string.IsNullOrWhiteSpace(title) &&
            (title.Contains("登录", StringComparison.OrdinalIgnoreCase) ||
             title.Contains("Log in", StringComparison.OrdinalIgnoreCase) ||
             title.Contains("Sign in", StringComparison.OrdinalIgnoreCase)));
}
