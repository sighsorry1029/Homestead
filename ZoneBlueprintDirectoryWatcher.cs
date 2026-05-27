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
    private static readonly HashSet<string> PendingIconInvalidations = new(StringComparer.OrdinalIgnoreCase);
    private static FileSystemWatcher? _watcher;
    private static string _watcherPath = "";
    private static float _nextRetryAt;
    private static volatile bool _changePending;

    public static void Update(Action<IReadOnlyList<string>> applyChanges)
    {
        Ensure();
        if (TryConsumeChanges(out List<string> iconInvalidations))
        {
            applyChanges(iconInvalidations);
        }
    }

    public static void Reset()
    {
        lock (ChangeLock)
        {
            PendingIconInvalidations.Clear();
            _changePending = false;
        }

        DisposeWatcher();
        _nextRetryAt = 0f;
    }

    private static void Ensure()
    {
        if (Time.realtimeSinceStartup < _nextRetryAt)
        {
            return;
        }

        string directory = HomesteadPlugin.BlueprintStorageFullPath;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            _nextRetryAt = Time.realtimeSinceStartup + MissingDirectoryRetrySeconds;
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
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            _watcherPath = directory;
        }
        catch (Exception ex)
        {
            _nextRetryAt = Time.realtimeSinceStartup + WatcherRetrySeconds;
            HomesteadPlugin.HomesteadLogger.LogDebug($"Could not watch Homestead blueprint directory yet: {ex.Message}");
        }
    }

    private static bool TryConsumeChanges(out List<string> iconInvalidations)
    {
        iconInvalidations = [];
        lock (ChangeLock)
        {
            if (!_changePending)
            {
                return false;
            }

            _changePending = false;
            iconInvalidations.AddRange(PendingIconInvalidations);
            PendingIconInvalidations.Clear();
        }

        return true;
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

    private static void QueueChange(string path, string? oldPath)
    {
        lock (ChangeLock)
        {
            _changePending = true;
            AddPendingIconInvalidation(path);
            if (!string.IsNullOrWhiteSpace(oldPath))
            {
                AddPendingIconInvalidation(oldPath!);
            }
        }
    }

    private static void AddPendingIconInvalidation(string path)
    {
        if (!IsBlueprintPng(path))
        {
            return;
        }

        string file = Path.GetFileName(path);
        const string suffix = ZoneBlueprintFileFormat.IconExtension;
        if (file.Length > suffix.Length)
        {
            PendingIconInvalidations.Add(file.Substring(0, file.Length - suffix.Length));
        }
    }

    private static bool IsBlueprintFile(string path)
    {
        string file = Path.GetFileName(path);
        return file.EndsWith(ZoneBlueprintFileFormat.BlueprintExtension, StringComparison.OrdinalIgnoreCase) ||
               file.EndsWith(ZoneBlueprintFileFormat.IconExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlueprintPng(string path)
    {
        return Path.GetFileName(path).EndsWith(ZoneBlueprintFileFormat.IconExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static void DisposeWatcher()
    {
        if (_watcher == null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnDirectoryChanged;
        _watcher.Changed -= OnDirectoryChanged;
        _watcher.Deleted -= OnDirectoryChanged;
        _watcher.Renamed -= OnDirectoryRenamed;
        _watcher.Dispose();
        _watcher = null;
        _watcherPath = "";
    }
}
