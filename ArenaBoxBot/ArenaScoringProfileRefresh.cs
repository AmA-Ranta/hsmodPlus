using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ArenaBoxBot;

/// <summary>Signals the original Box capture protocol only after a real key-null rejection of the same protected profile.</summary>
public sealed class ArenaScoringProfileRefresh
{
    private readonly object _sync = new object();
    private bool _signalled;
    public static string Fingerprint(byte[] cipher)
    {
        using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(cipher));
    }

    public bool Signal(string profilePath, string rejectedFingerprint)
    {
        lock (_sync)
        {
            if (_signalled || string.IsNullOrEmpty(rejectedFingerprint) ||
                !string.Equals(Path.GetFileName(profilePath), "boxai-direct-profile.dpapi", StringComparison.Ordinal)) return false;
            var marker = Path.Combine(Path.GetDirectoryName(profilePath), "boxai-direct-profile.invalid");
            if (File.Exists(marker)) { _signalled = true; return false; }
            var pending = marker + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                if (Fingerprint(File.ReadAllBytes(profilePath)) != rejectedFingerprint) return false;
                // Exact existing HsMod/Box protocol, not new credentials or a
                // stop/restart command. Box owns capturing its current login.
                File.WriteAllText(pending, "probe_rejected\n", new UTF8Encoding(false));
                if (Fingerprint(File.ReadAllBytes(profilePath)) != rejectedFingerprint) return false;
                File.Move(pending, marker);
                _signalled = true;
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            finally { try { if (File.Exists(pending)) File.Delete(pending); } catch { } }
        }
    }

    public void AfterVerifiedScore() { lock (_sync) _signalled = false; }
}
