using Diffract.Git.Snapshot;

namespace Diffract.Git.Watching;

/// <summary>
/// Watches a repo's common git directory and worktrees recursively, and its worktree containers without subfolders,
/// and reports each change as a <see cref="ChangeSet"/>. Never starts git and never relies on fsmonitor.
/// Call <see cref="Update"/> after a refresh, so added or removed worktrees are watched or dropped.
/// </summary>
public sealed class RepoWatcher : IDisposable
{
    // The most Windows allows for a network share, and plenty for a burst of git writes.
    private const int BufferSize = 64 * 1024;

    private readonly Action<ChangeSet> _changed;
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Path, bool Recursive), FileSystemWatcher> _watchers = new();
    private WatchLayout _layout;
    private bool _disposed;

    /// <param name="changed">Called on a watcher thread for every change that matters. Must return quickly.</param>
    public RepoWatcher(WatchLayout layout, Action<ChangeSet> changed)
    {
        _layout = layout;
        _changed = changed;
        Sync();
    }

    /// <summary>The folders being watched, for tests and logs.</summary>
    public IReadOnlyList<(string Path, bool Recursive)> Watched
    {
        get
        {
            lock (_lock)
                return [.. _watchers.Keys];
        }
    }

    public void Update(WatchLayout layout)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _layout = layout;
            Sync();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var watcher in _watchers.Values)
                watcher.Dispose();
            _watchers.Clear();
        }
    }

    private void Sync()
    {
        var wanted = _layout.Roots().Where(r => Directory.Exists(r.Path)).ToHashSet();
        foreach (var gone in _watchers.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _watchers[gone].Dispose();
            _watchers.Remove(gone);
        }
        foreach (var root in wanted.Where(r => !_watchers.ContainsKey(r)))
        {
            try
            {
                _watchers[root] = Start(root.Path, root.Recursive);
            }
            catch (Exception e) when (e is ArgumentException or FileNotFoundException or IOException)
            {
                // The folder went away between the check and the start. The change that removed it triggers a refresh.
            }
        }
    }

    private FileSystemWatcher Start(string path, bool recursive)
    {
        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = recursive,
            InternalBufferSize = BufferSize,
            // A container only matters for folders that appear or disappear.
            NotifyFilter = recursive
                ? NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                : NotifyFilters.DirectoryName,
        };
        watcher.Created += (_, e) => Report(e.FullPath);
        // A folder "changes" whenever something in it does. That something has its own event, and a worktree
        // record folder that changes for every HEAD write would look like a new worktree.
        watcher.Changed += (_, e) =>
        {
            if (!Directory.Exists(e.FullPath))
                Report(e.FullPath);
        };
        watcher.Deleted += (_, e) => Report(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            Report(e.OldFullPath);
            Report(e.FullPath);
        };
        // An overflow loses events, so only a full refresh can be trusted. Any other error usually means the
        // watched folder itself went away, which the worktree list will show.
        watcher.Error += (_, e) => _changed(e.GetException() is InternalBufferOverflowException ? ChangeSet.All : ChangeSet.WorktreeListChanged);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void Report(string path)
    {
        WatchLayout layout;
        lock (_lock)
        {
            if (_disposed)
                return;
            layout = _layout;
        }

        var change = ChangeClassifier.Classify(path, layout);
        if (!change.IsEmpty)
            _changed(change);
    }
}
