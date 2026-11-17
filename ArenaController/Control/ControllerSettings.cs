using System.Text.Json.Serialization;

namespace ArenaController;

/// <summary>Persisted controller settings.  Only the applied snapshot is stored;
/// staged UI edits live in memory until 应用设置.</summary>
internal sealed class ControllerSettings
{
    public string HearthstoneDirectory { get; set; } = @"D:\Hearthstone";
    public string HsModDirectory { get; set; } = @"D:\Hearthstone\BepInEx\HsMod";
    public string ArenaConfigPath { get; set; } = @"D:\Hearthstone\BepInEx\config\local.hsmod.arenaboxbot.cfg";
    public string HsModConfigPath { get; set; } = @"D:\Hearthstone\BepInEx\config\HsMod.cfg";
    // The source is always the DLL beside the running controller.  Persisting
    // this value made a temporary test extraction look like a real install
    // source in the next session, even though the installer correctly ignored
    // it.  Keep the property for migration compatibility but never serialize
    // it; the UI derives the read-only path from AppContext.BaseDirectory.
    [JsonIgnore]
    public string PluginSourcePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "ArenaBoxBot.dll");
    public string BoxExecutablePath { get; set; } = @"D:\炉石传说盒子\HSAng.exe";
    public string Mode { get; set; } = "标准";
    public string ArenaMode { get; set; } = "普通竞技场";
    /// <summary>HsMod 自动脚本使用的收藏卡组名称。竞技场模式忽略该字段。</summary>
    public string DeckName { get; set; } = "";
    public int MaxGamesPerRun { get; set; }
    public int MaxTotalGames { get; set; }
    public int MaxRuns { get; set; }
    public bool RequireBoxHeartbeat { get; set; } = true;
    public bool AutoSchedule { get; set; }
    public string StartTime { get; set; } = "08:00";
    public string StopTime { get; set; } = "23:00";
    public bool CloseGameAfterStop { get; set; } = true;
    public bool StopOnTicketShortage { get; set; } = true;
    public bool StopOnGoldShortage { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
}
