using System.Diagnostics;
using System.Text;
using ArenaController;

// Live-operation harness for the maintainer's machine.  It exercises the
// exact production installer/restore code paths that the control center uses,
// so packaging can be verified without clicking through the WinForms UI.
//   LiveOps install --package <dir> --game <dir> --box <HSAng.exe> [--out file]
//   LiveOps restore --package <dir> --game <dir> --box <HSAng.exe> [--out file]
//   LiveOps status  --game <dir> --box <HSAng.exe> [--out file]
//   LiveOps stop    [--game <dir>] [--out file]

var args0 = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (args0.Length == 0)
{
    Console.Error.WriteLine("usage: LiveOps <install|restore|status|stop> [options]");
    return 2;
}
var command = args0[0];
string? Option(string name)
{
    var index = Array.FindIndex(args0, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args0.Length ? args0[index + 1] : null;
}
var output = new StringBuilder();
void Log(string line)
{
    Console.WriteLine(line);
    output.AppendLine(line);
}
var outFile = Option("--out");

var package = Option("--package");
var game = Option("--game") ?? @"D:\Hearthstone";
var box = Option("--box") ?? @"D:\炉石传说盒子\HSAng.exe";

try
{
    if (command is "install" or "restore")
    {
        if (string.IsNullOrWhiteSpace(package) || !Directory.Exists(package))
            throw new DirectoryNotFoundException("安装包目录不存在：" + package);
        var service = new NativeInstallService(package);
        NativeInstallResult result;
        if (command == "install")
        {
            var request = new NativeInstallRequest
            {
                HearthstoneDirectory = game,
                BoxExecutablePath = box,
                CleanupKnownLegacyFiles = true
            };
            result = service.Install(request, new Progress<string>(line => Console.WriteLine(line)));
        }
        else
        {
            result = service.RestoreOriginal(game, box, null);
        }
        Log("operation=" + command);
        Log("success=" + result.Success);
        Log("resolvedGame=" + result.ResolvedHearthstoneDirectory);
        Log("backup=" + result.BackupDirectory);
        if (result.FailureStage != null) Log("failureStage=" + result.FailureStage);
        foreach (var line in result.Lines) Log(line.TrimEnd());
        foreach (var line in result.DeveloperLines.Take(20)) Log("dev: " + line.TrimEnd());
        foreach (var file in new[] { "HsMod.dll", "ArenaBoxBot.dll", "UniverseLib.Mono.dll" })
        {
            var path = Path.Combine(game, "BepInEx", "plugins", file);
            Log($"installed.{file}=" + (File.Exists(path) ? Sha256(path) : "<missing>"));
        }
        var versionDll = Path.Combine(Path.GetDirectoryName(box) ?? "", "version.dll");
        Log("installed.version.dll=" + (File.Exists(versionDll) ? Sha256(versionDll) : "<missing>"));
        return result.Success ? 0 : 1;
    }

    if (command == "status")
    {
        foreach (var file in new[] { "HsMod.dll", "ArenaBoxBot.dll", "UniverseLib.Mono.dll" })
        {
            var path = Path.Combine(game, "BepInEx", "plugins", file);
            Log($"plugin.{file}=" + (File.Exists(path) ? Sha256(path) : "<missing>"));
        }
        var versionDll = Path.Combine(Path.GetDirectoryName(box) ?? "", "version.dll");
        Log("box.version.dll=" + (File.Exists(versionDll) ? Sha256(versionDll) : "<missing>"));
        foreach (var name in new[] { "Hearthstone", "HSAng", "hsmodPlus", "Battle.net" })
            Log("process." + name + "=" + Process.GetProcessesByName(name).Length);
        return 0;
    }

    if (command == "stop")
    {
        foreach (var name in new[] { "Hearthstone", "HSAng", "HSAExternal", "hsmodPlus" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    Log("stopping " + name + " pid=" + process.Id);
                    process.Kill(true);
                    process.WaitForExit(10000);
                }
                catch (Exception ex)
                {
                    Log("stop-failed " + name + " pid=" + process.Id + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }
        Thread.Sleep(1500);
        foreach (var name in new[] { "Hearthstone", "HSAng", "HSAExternal", "hsmodPlus" })
            Log("remaining." + name + "=" + Process.GetProcessesByName(name).Length);
        return 0;
    }

    Console.Error.WriteLine("unknown command: " + command);
    return 2;
}
catch (Exception ex)
{
    Log("ERROR " + ex.GetType().Name + ": " + ex.Message);
    return 1;
}
finally
{
    if (!string.IsNullOrWhiteSpace(outFile))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, output.ToString(), new UTF8Encoding(false));
    }
}

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
}
