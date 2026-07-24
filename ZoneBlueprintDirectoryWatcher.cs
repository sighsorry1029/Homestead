using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Homestead;

internal static class ZoneBlueprintDirectoryWatcher
{
    private const float MissingDirectoryRetrySeconds = 2f;
    private const float WatcherRetrySeconds = 5f;
    private static readonly object ChangeLock = new();
    private static readonly HashSet<string> PendingBlueprintChanges = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PendingIconInvalidations = new(StringComparer.OrdinalIgnoreCase);
    private static FileSystemWatcher? _watcher;
    private static string _watcherPath = "";
    private static float _nextRetryAt;
    private static volatile bool _changePending;
    private static volatile bool _watcherFaulted;
    private static bool _rescanAfterWatcherReconnect;

    public static void Update(Action<IReadOnlyList<string>, IReadOnlyList<string>> applyChanges)
    {
        Ensure();
        if (TryConsumeChanges(out List<string> blueprintChanges, out List<string> iconInvalidations))
        {
            applyChanges(blueprintChanges, iconInvalidations);
        }
    }

    public static void Reset()
    {
        lock (ChangeLock)
        {
            PendingBlueprintChanges.Clear();
            PendingIconInvalidations.Clear();
            _changePending = false;
        }

        DisposeWatcher();
        _nextRetryAt = 0f;
        _watcherFaulted = false;
        _rescanAfterWatcherReconnect = false;
    }

    private static void Ensure()
    {
        float now = Time.realtimeSinceStartup;
        if (_watcherFaulted)
        {
            _watcherFaulted = false;
            DisposeWatcher();
            _rescanAfterWatcherReconnect = true;
            QueueFullRescan();
            _nextRetryAt = now + WatcherRetrySeconds;
            HomesteadPlugin.HomesteadLogger.LogDebug("Homestead blueprint directory watcher stopped unexpectedly; scheduled a full rescan and watcher restart.");
            return;
        }

        if (now < _nextRetryAt)
        {
            return;
        }

        string directory = HomesteadPlugin.BlueprintStorageFullPath;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            if (_watcher != null)
            {
                DisposeWatcher();
                _rescanAfterWatcherReconnect = true;
                QueueFullRescan();
            }

            _nextRetryAt = now + MissingDirectoryRetrySeconds;
            return;
        }

        if (_watcher != null && string.Equals(_watcherPath, directory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DisposeWatcher();
        try
        {
            FileSystemWatcher watcher = new(directory)
            {
                Filter = "*.*",
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Created += OnDirectoryChanged;
            watcher.Changed += OnDirectoryChanged;
            watcher.Deleted += OnDirectoryChanged;
            watcher.Renamed += OnDirectoryRenamed;
            watcher.Error += OnWatcherError;
            _watcher = watcher;
            _watcherPath = directory;
            watcher.EnableRaisingEvents = true;
            _nextRetryAt = 0f;
            if (_rescanAfterWatcherReconnect)
            {
                _rescanAfterWatcherReconnect = false;
                QueueFullRescan();
            }
        }
        catch (Exception ex)
        {
            DisposeWatcher();
            _rescanAfterWatcherReconnect = true;
            QueueFullRescan();
            _nextRetryAt = now + WatcherRetrySeconds;
            HomesteadPlugin.HomesteadLogger.LogDebug($"Could not watch Homestead blueprint directory yet: {ex.Message}");
        }
    }

    private static bool TryConsumeChanges(out List<string> blueprintChanges, out List<string> iconInvalidations)
    {
        blueprintChanges = [];
        iconInvalidations = [];
        lock (ChangeLock)
        {
            if (!_changePending)
            {
                return false;
            }

            _changePending = false;
            blueprintChanges.AddRange(PendingBlueprintChanges);
            iconInvalidations.AddRange(PendingIconInvalidations);
            PendingBlueprintChanges.Clear();
            PendingIconInvalidations.Clear();
        }

        return true;
    }

    private static void QueueFullRescan()
    {
        lock (ChangeLock)
        {
            _changePending = true;
        }
    }

    private static void OnDirectoryChanged(object sender, FileSystemEventArgs args)
    {
        if (IsBlueprintFile(args.FullPath))
        {
            QueueChange(args.FullPath, null);
        }
    }

    private static void OnDirectoryRenamed(object sender, RenamedEventArgs args)
    {
        if (IsBlueprintFile(args.FullPath) || IsBlueprintFile(args.OldFullPath))
        {
            QueueChange(args.FullPath, args.OldFullPath);
        }
    }

    private static void OnWatcherError(object sender, ErrorEventArgs args)
    {
        _watcherFaulted = true;
    }

    private static void QueueChange(string path, string? oldPath)
    {
        lock (ChangeLock)
        {
            _changePending = true;
            AddPendingChange(path);
            if (!string.IsNullOrWhiteSpace(oldPath))
            {
                AddPendingChange(oldPath!);
            }
        }
    }

    private static void AddPendingChange(string path)
    {
        string file = Path.GetFileName(path);
        if (file.EndsWith(ZoneBlueprintFileFormat.BlueprintExtension, StringComparison.OrdinalIgnoreCase))
        {
            AddNameWithoutSuffix(PendingBlueprintChanges, file, ZoneBlueprintFileFormat.BlueprintExtension);
            return;
        }

        if (file.EndsWith(ZoneBlueprintFileFormat.IconExtension, StringComparison.OrdinalIgnoreCase))
        {
            AddNameWithoutSuffix(PendingIconInvalidations, file, ZoneBlueprintFileFormat.IconExtension);
        }
    }

    private static void AddNameWithoutSuffix(HashSet<string> target, string file, string suffix)
    {
        if (file.Length > suffix.Length)
        {
            target.Add(file.Substring(0, file.Length - suffix.Length));
        }
    }

    private static bool IsBlueprintFile(string path)
    {
        string file = Path.GetFileName(path);
        return file.EndsWith(ZoneBlueprintFileFormat.BlueprintExtension, StringComparison.OrdinalIgnoreCase) ||
               file.EndsWith(ZoneBlueprintFileFormat.IconExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static void DisposeWatcher()
    {
        if (_watcher == null)
        {
            return;
        }

        FileSystemWatcher watcher = _watcher;
        _watcher = null;
        _watcherPath = "";
        watcher.Created -= OnDirectoryChanged;
        watcher.Changed -= OnDirectoryChanged;
        watcher.Deleted -= OnDirectoryChanged;
        watcher.Renamed -= OnDirectoryRenamed;
        watcher.Error -= OnWatcherError;
        try
        {
            watcher.EnableRaisingEvents = false;
        }
        catch
        {
            // A faulted watcher may already have released its native handle.
        }

        try
        {
            watcher.Dispose();
        }
        catch
        {
            // Recovery continues by creating a fresh watcher on the main thread.
        }
    }
}
