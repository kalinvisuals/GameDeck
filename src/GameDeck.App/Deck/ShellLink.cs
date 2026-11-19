using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;

namespace GameDeck.App.Deck;

/// <summary>
/// Reads Windows shortcuts (.lnk) through the shell's own IShellLink, so the deck can tell which
/// program a shortcut starts. Nothing is run: the file is only read.
/// </summary>
internal static partial class ShellLink
{
    /// <summary>Program file and arguments of a shortcut; null when it isn't one or can't be read.</summary>
    public static Task<(string Target, string Arguments)?> ReadAsync(string lnk) => OnSta(() => Read(lnk));

    /// <summary>
    /// The executable a launch path ends up running, e.g. "obs64.exe"; null when it can't be known
    /// (a link, a document, a folder). Apps installed with Squirrel (Discord, Slack, GitHub Desktop)
    /// have shortcuts to Update.exe with "--processStart Discord.exe"; the real program is the latter.
    /// </summary>
    public static async Task<string?> ProgramOfAsync(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".exe") return Path.GetFileName(path).ToLowerInvariant();
        if (ext != ".lnk") return null;
        if (await ReadAsync(path) is not { } link) return null;
        var started = ProcessStart().Match(link.Arguments);
        if (started.Success) return Path.GetFileName(started.Groups[1].Value).ToLowerInvariant();
        return link.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(link.Target).ToLowerInvariant() : null;
    }

    [GeneratedRegex("""--processStart\s+"?([^"\s]+\.exe)""", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessStart();

    /// <summary>Writes a shortcut (tests use it to build real .lnk files).</summary>
    internal static Task SaveAsync(string lnk, string target, string arguments) => OnSta(() =>
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(target);
            link.SetArguments(arguments);
            ((IPersistFile)link).Save(lnk, true);
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    });

    private static (string, string)? Read(string lnk)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            ((IPersistFile)link).Load(lnk, 0); // STGM_READ
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0x4); // SLGP_RAWPATH: don't resolve or search
            var arguments = new StringBuilder(2048);
            link.GetArguments(arguments, arguments.Capacity);
            return (Environment.ExpandEnvironmentVariables(target.ToString()), arguments.ToString());
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>The shell's COM objects want a single-threaded apartment; a short-lived thread provides one.</summary>
    private static Task<T> OnSta<T>(Func<T> work)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(work()); }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true, Name = "ShellLink" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
