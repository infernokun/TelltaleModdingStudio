using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TelltaleModding.App.ViewModels;

/// <summary>
/// One node of the extracted-folder explorer tree. Directory children are loaded
/// lazily on first expansion so opening a folder with thousands of files stays instant.
/// </summary>
public sealed partial class FileNodeViewModel : ViewModelBase
{
    /// <summary>Dummy child that makes unexpanded directories show an expander arrow.</summary>
    private static FileNodeViewModel PlaceholderNode() => new("…", "", isDirectory: false);

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }

    public ObservableCollection<FileNodeViewModel> Children { get; } = new();

    private bool _childrenLoaded;

    [ObservableProperty]
    private bool _isExpanded;

    public FileNodeViewModel(string name, string fullPath, bool isDirectory)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        if (isDirectory)
            Children.Add(PlaceholderNode());
    }

    /// <summary>Creates the root node for a folder and loads its first level.</summary>
    public static FileNodeViewModel ForRoot(string folderPath)
    {
        var root = new FileNodeViewModel(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(folderPath)),
            folderPath, isDirectory: true);
        root.LoadChildren();
        root.IsExpanded = true;
        return root;
    }

    public string Icon => IsDirectory ? "📁" : Path.GetExtension(Name).ToLowerInvariant() switch
    {
        ".d3dmesh" => "🧊",
        ".d3dtx" or ".dds" => "🖼",
        ".skl" => "🦴",
        ".anm" or ".chore" => "🏃",
        ".aud" or ".ogg" or ".wav" => "🔊",
        ".lua" or ".lenc" => "📜",
        ".ttarch" or ".ttarch2" => "📦",
        _ => "📄",
    };

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            LoadChildren();
    }

    /// <summary>Populates real children on first use (no-op afterwards).</summary>
    public void LoadChildren()
    {
        if (!IsDirectory || _childrenLoaded)
            return;
        _childrenLoaded = true;
        Children.Clear();

        try
        {
            var dirs = Directory.EnumerateDirectories(FullPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => new FileNodeViewModel(Path.GetFileName(p), p, isDirectory: true));
            var files = Directory.EnumerateFiles(FullPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => new FileNodeViewModel(Path.GetFileName(p), p, isDirectory: false));
            foreach (var node in dirs.Concat(files))
                Children.Add(node);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Children.Add(new FileNodeViewModel($"⚠ {ex.Message}", "", isDirectory: false));
        }
    }
}
