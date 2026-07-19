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

    /// <summary>Creates a virtual grouping node with preloaded children.</summary>
    public static FileNodeViewModel ForGroup(string label, IEnumerable<FileNodeViewModel> children)
    {
        var group = new FileNodeViewModel(label, "", isDirectory: true);
        group._childrenLoaded = true;
        group.Children.Clear();
        foreach (var child in children)
            group.Children.Add(child);
        return group;
    }

    /// <summary>Creates a flat search-result node showing a path relative to the root.</summary>
    public static FileNodeViewModel ForSearchResult(string rootPath, string filePath)
    {
        var relative = Path.GetRelativePath(rootPath, filePath);
        return new FileNodeViewModel(relative, filePath, isDirectory: false);
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
            foreach (var dir in Directory.EnumerateDirectories(FullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                Children.Add(new FileNodeViewModel(Path.GetFileName(dir), dir, isDirectory: true));

            var files = Directory.EnumerateFiles(FullPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => new FileNodeViewModel(Path.GetFileName(p), p, isDirectory: false))
                .ToList();

            // Big flat folders become unmanageable as one list; group files by kind.
            if (files.Count > 64)
            {
                foreach (var group in files
                    .GroupBy(f => CategoryLabel(Path.GetExtension(f.Name)))
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    Children.Add(ForGroup($"{group.Key} ({group.Count():N0})", group));
                }
            }
            else
            {
                foreach (var file in files)
                    Children.Add(file);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Children.Add(new FileNodeViewModel($"⚠ {ex.Message}", "", isDirectory: false));
        }
    }

    private static string CategoryLabel(string extension) => extension.ToLowerInvariant() switch
    {
        ".d3dmesh" => "Meshes",
        ".d3dtx" => "Textures",
        ".dds" => "DDS images",
        ".skl" => "Skeletons",
        ".anm" => "Animations",
        ".chore" => "Chores",
        ".prop" => "Props",
        ".scene" => "Scenes",
        ".lua" or ".lenc" => "Scripts",
        ".aud" or ".ogg" or ".wav" => "Audio",
        "" => "Other",
        var ext => ext.TrimStart('.'),
    };
}
