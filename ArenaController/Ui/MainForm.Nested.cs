using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ArenaControl.Contracts;

using ArenaController.Ui.Design;

namespace ArenaController;

internal sealed partial class MainForm : Form
{


    private sealed class ArenaRuntimeSnapshot
    {
        public long updatedAtUnixMs { get; set; }
        public int gameProcessId { get; set; }
        public long gameStartedAtUtcTicks { get; set; }
        public bool arenaSelected { get; set; }
        public bool runtimeEnabled { get; set; }
        public bool inGameplay { get; set; }
        public string scene { get; set; } = "";
        public string arenaState { get; set; } = "";
        public bool runActive { get; set; }
        public int slot { get; set; }
        public int maxSlot { get; set; }
        public int gamesThisRun { get; set; }
        public string runKey { get; set; } = "";
        public string progressKey { get; set; } = "";
        public string stopReason { get; set; } = "";
        // Plugin self-description.  The controller never derives the mode from
        // its own settings: it reports what the independent plugin publishes.
        public string pluginVersion { get; set; } = "";
        public string mode { get; set; } = "";
        public string supportedModes { get; set; } = "";
        public bool controllerRequired { get; set; }
    }

}
