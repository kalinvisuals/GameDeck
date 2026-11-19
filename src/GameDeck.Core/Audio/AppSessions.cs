using System.Diagnostics;
using GameDeck.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace GameDeck.Core.Audio;

/// <summary>One application as the Windows volume mixer shows it: all its sessions on every active output.</summary>
/// <param name="Key">Stable identity: the executable name in lower case ("discord.exe"), or <see cref="AppSessions.SystemSoundsKey"/>.</param>
/// <param name="Volume">0..1. With several sessions, the loudest one.</param>
/// <param name="Muted">True only when every session is muted.</param>
public sealed record AppAudioInfo(string Key, string Name, float Volume, bool Muted, int Sessions);

/// <summary>Volume and/or mute to apply to one application; null leaves that part unchanged.</summary>
public sealed record AppAudioTarget(string Key, float? Volume, bool? Muted);

/// <summary>
/// Per-application volume and mute, the same controls as the Windows volume mixer. This is not an
/// audio path: nothing is captured, mixed or processed, and devices and routing are never changed.
///
/// Applications are matched by executable name, which survives restarts (process ids do not). All
/// sessions of one application move together. GameDeck's own sessions are never listed or touched,
/// so the soundboard and the mic path are unaffected. Nothing runs in the background: each call
/// enumerates afresh and releases the COM objects. Call from a thread-pool (MTA) thread.
/// </summary>
public static class AppSessions
{
    public const string SystemSoundsKey = "#system";

    public static IReadOnlyList<AppAudioInfo> List()
    {
        using var e = new MMDeviceEnumerator();
        var sessions = Enumerate(e);
        try
        {
            return sessions
                .GroupBy(s => s.Key)
                .Select(g =>
                {
                    float volume = 0;
                    var muted = true;
                    foreach (var s in g)
                    {
                        try
                        {
                            volume = Math.Max(volume, s.Session.SimpleAudioVolume.Volume);
                            muted &= s.Session.SimpleAudioVolume.Mute;
                        }
                        catch { /* session went away mid-read */ }
                    }
                    return new AppAudioInfo(g.Key, g.First().Name, volume, muted, g.Count());
                })
                .OrderBy(a => a.Key == SystemSoundsKey ? 1 : 0)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            Release(sessions);
        }
    }

    /// <summary>Applies each target to every session of that application. Returns the keys that were found.</summary>
    public static IReadOnlySet<string> Apply(IEnumerable<AppAudioTarget> targets)
    {
        var byKey = targets.GroupBy(t => t.Key.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Last());
        var found = new HashSet<string>();
        if (byKey.Count == 0) return found;

        using var e = new MMDeviceEnumerator();
        // Setting a volume only needs the key: skipping file descriptions keeps a slider drag fast.
        var sessions = Enumerate(e, describe: false);
        try
        {
            foreach (var s in sessions)
            {
                if (!byKey.TryGetValue(s.Key, out var t)) continue;
                try
                {
                    if (t.Volume is { } v) s.Session.SimpleAudioVolume.Volume = Math.Clamp(v, 0f, 1f);
                    if (t.Muted is { } m) s.Session.SimpleAudioVolume.Mute = m;
                    found.Add(s.Key);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Apps: could not set '{s.Key}'", ex);
                }
            }
        }
        finally
        {
            Release(sessions);
        }
        return found;
    }

    /// <summary>Stable key for a running process: "discord.exe". Null if the process is gone.</summary>
    public static string? KeyForProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return KeyFromProcessName(p.ProcessName);
        }
        catch
        {
            return null;
        }
    }

    public static string KeyFromProcessName(string processName) => processName.ToLowerInvariant() + ".exe";

    private const string OwnKey = "gamedeck.exe";

    private sealed record Entry(string Key, string Name, AudioSessionControl Session, MMDevice Device);

    /// <summary>Sessions stay valid until <see cref="Release"/>, which also releases their devices.</summary>
    private static List<Entry> Enumerate(MMDeviceEnumerator e, bool describe = true)
    {
        var own = Environment.ProcessId;
        var names = new Dictionary<uint, (string Key, string Name)?>();
        var list = new List<Entry>();
        foreach (var device in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var used = false;
            try
            {
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    var keep = false;
                    try
                    {
                        if (s.State == AudioSessionState.AudioSessionStateExpired) continue;
                        if (s.IsSystemSoundsSession)
                        {
                            list.Add(new Entry(SystemSoundsKey, Localization.L.T("apps.systemSounds"), s, device));
                            keep = true;
                            continue;
                        }
                        var pid = s.GetProcessID;
                        if (pid == 0 || pid == own) continue;
                        if (!names.TryGetValue(pid, out var id)) names[pid] = id = Identify((int)pid, describe);
                        // Any GameDeck copy (e.g. a dev build next to the installed one): its sessions carry the soundboard.
                        if (id is null || id.Value.Key == OwnKey) continue;
                        list.Add(new Entry(id.Value.Key, id.Value.Name, s, device));
                        keep = true;
                    }
                    finally
                    {
                        if (keep) used = true;
                        else s.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Apps: could not read sessions on '{SafeName(device)}'", ex);
            }
            finally
            {
                if (!used) device.Dispose();
            }
        }
        return list;
    }

    private static (string Key, string Name)? Identify(int pid, bool describe)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var key = KeyFromProcessName(p.ProcessName);
            string? description = null;
            try { if (describe) description = p.MainModule?.FileVersionInfo.FileDescription; }
            catch { /* elevated or protected process: the name is enough */ }
            return (key, string.IsNullOrWhiteSpace(description) ? p.ProcessName : description.Trim());
        }
        catch
        {
            return null; // exited between enumeration and lookup
        }
    }

    private static void Release(List<Entry> sessions)
    {
        foreach (var s in sessions)
        {
            try { s.Session.Dispose(); } catch { }
        }
        foreach (var d in sessions.Select(s => s.Device).Distinct())
        {
            try { d.Dispose(); } catch { }
        }
    }

    private static string SafeName(MMDevice d)
    {
        try { return d.FriendlyName; } catch { return "?"; }
    }
}
