using GameDeck.Core.Localization;

namespace GameDeck.App.Tray;

/// <summary>
/// The Windows "Open" and folder dialogs, shown on the PC's own screen for the Smart Deck editor: a
/// browser can't reveal the full path of a file it picks, so GameDeck asks Windows directly.
/// </summary>
internal static class PathPicker
{
    /// <summary>Shows the dialog on the UI thread; completes with the chosen path, or null when cancelled.</summary>
    public static Task<string?> ShowAsync(SynchronizationContext ui, bool folder, string? current)
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(_ =>
        {
            try { result.SetResult(Show(folder, current)); }
            catch (Exception ex) { result.SetException(ex); }
        }, null);
        return result.Task;
    }

    private static string? Show(bool folder, string? current)
    {
        // A tray app has no window of its own. An invisible topmost owner keeps the dialog in front of
        // the browser that asked for it, instead of opening behind it.
        using var owner = new Form
        {
            ShowInTaskbar = false,
            TopMost = true,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.CenterScreen,
            Size = new Size(1, 1),
            Opacity = 0,
        };
        owner.Show();
        owner.Activate();

        var start = StartDirectory(current);
        if (folder)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = L.T("deck.browseFolderTitle"),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
            };
            if (start is not null) dialog.InitialDirectory = start;
            return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
        }

        using var file = new OpenFileDialog
        {
            Title = L.T("deck.browseFileTitle"),
            Filter = L.T("deck.browseFilter"),
            // Keep a chosen shortcut as the shortcut: game launchers often need the arguments inside it.
            DereferenceLinks = false,
            CheckFileExists = true,
            RestoreDirectory = true,
        };
        if (start is not null) file.InitialDirectory = start;
        return file.ShowDialog(owner) == DialogResult.OK ? file.FileName : null;
    }

    /// <summary>Where the current path points, or Program Files for a new button.</summary>
    private static string? StartDirectory(string? current)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(current))
            {
                var p = current.Trim().Trim('"');
                if (Directory.Exists(p)) return p;
                var dir = Path.GetDirectoryName(p);
                if (dir is not null && Directory.Exists(dir)) return dir;
            }
        }
        catch (ArgumentException) { /* not a path */ }
        return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    }
}
