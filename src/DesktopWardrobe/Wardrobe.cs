using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;

namespace DesktopWardrobe;

/// <summary>
/// The real storage behind the rack: hanging an item moves it from the desktop into
/// a "Desktop Wardrobe" folder next to the Desktop folder, and taking it off moves it back.
/// </summary>
internal sealed class Wardrobe : IDisposable
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly FileSystemWatcher _watcher;
    private readonly string _orderFile;
    private List<string> _order = new(); // item names, in the order they hang on the rack

    public string DesktopPath { get; }
    public string StoragePath { get; }
    public IReadOnlyList<string> Items { get; private set; } = Array.Empty<string>();

    public event Action? Changed;
    public event Action<string>? Error;

    public Wardrobe()
    {
        DesktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        // Next to the Desktop folder, so moves are instant renames on the same drive (and inside OneDrive if the desktop is).
        string? custom = Environment.GetEnvironmentVariable("DESKTOP_WARDROBE_DIR");
        StoragePath = !string.IsNullOrEmpty(custom)
            ? Path.GetFullPath(custom)
            : Path.Combine(Path.GetDirectoryName(DesktopPath)!, "Desktop Wardrobe");
        Directory.CreateDirectory(StoragePath);

        string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopWardrobe");
        Directory.CreateDirectory(dataDir);
        _orderFile = Path.Combine(dataDir, "order.json");
        try
        {
            if (File.Exists(_orderFile))
                _order = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_orderFile)) ?? new();
        }
        catch (Exception)
        {
            // A broken order file only loses the hanging order, not any items.
        }

        _watcher = new FileSystemWatcher(StoragePath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
        };
        _watcher.Created += OnStorageChanged;
        _watcher.Deleted += OnStorageChanged;
        _watcher.Renamed += OnStorageChanged;
        _watcher.EnableRaisingEvents = true;

        Refresh();
    }

    /// <summary>Only real items on the same drive, and never the wardrobe itself or a folder containing it.</summary>
    public bool CanHang(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!File.Exists(full) && !Directory.Exists(full))
            return false;
        if (IsSameOrInside(full, StoragePath) || IsSameOrInside(StoragePath, full))
            return false;
        return PathComparer.Equals(Path.GetPathRoot(full), Path.GetPathRoot(StoragePath));
    }

    public void Hang(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            try
            {
                string target = UniqueTarget(StoragePath, name);
                Move(path, target);
                _order.Add(Path.GetFileName(target));
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Couldn't hang \"{name}\": {ex.Message}");
            }
        }
        Refresh();
    }

    public void ReturnToDesktop(string path)
    {
        string name = Path.GetFileName(path);
        try
        {
            Move(path, UniqueTarget(DesktopPath, name));
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Couldn't put \"{name}\" back: {ex.Message}");
        }
        Refresh();
    }

    public void ReturnAll()
    {
        foreach (string path in Items.ToList())
            ReturnToDesktop(path);
    }

    /// <summary>Moves an item to a new spot on the line.</summary>
    public void MoveTo(string path, int index)
    {
        string name = Path.GetFileName(path);
        int current = _order.FindIndex(n => PathComparer.Equals(n, name));
        if (current < 0)
            return;

        _order.RemoveAt(current);
        _order.Insert(Math.Clamp(index, 0, _order.Count), name);
        if (current == _order.FindIndex(n => PathComparer.Equals(n, name)))
            return;

        Items = _order.Select(n => Path.Combine(StoragePath, n)).ToList();
        SaveOrder();
        Changed?.Invoke();
    }

    /// <summary>Re-reads the storage folder, keeping the known hanging order and appending anything new.</summary>
    public void Refresh()
    {
        List<string> names;
        try
        {
            names = new DirectoryInfo(StoragePath).EnumerateFileSystemInfos()
                .Where(i => (i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .Select(i => i.Name)
                .ToList();
        }
        catch (IOException)
        {
            return;
        }

        var present = new HashSet<string>(names, PathComparer);
        var ordered = _order.Where(present.Contains).Distinct(PathComparer).ToList();
        var known = new HashSet<string>(ordered, PathComparer);
        ordered.AddRange(names.Where(n => !known.Contains(n)).OrderBy(n => n, PathComparer));

        var items = ordered.Select(n => Path.Combine(StoragePath, n)).ToList();
        bool changed = !items.SequenceEqual(Items, PathComparer);
        _order = ordered;
        Items = items;

        if (changed)
        {
            SaveOrder();
            Changed?.Invoke();
        }
    }

    private void OnStorageChanged(object sender, FileSystemEventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, Refresh);

    private void SaveOrder()
    {
        try
        {
            File.WriteAllText(_orderFile, JsonSerializer.Serialize(_order));
        }
        catch (IOException)
        {
        }
    }

    private static void Move(string source, string target)
    {
        if (Directory.Exists(source))
            Directory.Move(source, target);
        else
            File.Move(source, target);
    }

    private static string UniqueTarget(string folder, string name)
    {
        string candidate = Path.Combine(folder, name);
        string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
        for (int i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
        return candidate;
    }

    private static bool IsSameOrInside(string path, string folder) =>
        PathComparer.Equals(path, folder) ||
        path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _watcher.Dispose();
}
