using GameDeck.Core.Localization;
using System.Collections.Concurrent;

namespace GameDeck.Core.Audio;

/// <summary>Decodes each file once (keyed by path + modification time) so triggers need no disk I/O.</summary>
internal sealed class SoundCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task<CachedSound>>> _items = new(StringComparer.OrdinalIgnoreCase);

    public Task<CachedSound> GetAsync(string path)
    {
        if (!File.Exists(path)) return Task.FromException<CachedSound>(new FileNotFoundException(L.T("sound.missing"), path));

        var key = Key(path);
        var lazy = _items.GetOrAdd(key, _ => new Lazy<Task<CachedSound>>(() => Task.Run(() => CachedSound.Decode(path))));
        var task = lazy.Value;
        if (task.IsFaulted) _items.TryRemove(key, out _); // let the next press retry
        return task;
    }

    public void Evict(string path)
    {
        var prefix = path + "|";
        foreach (var key in _items.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            _items.TryRemove(key, out _);
    }

    private static string Key(string path) => $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}";
}
