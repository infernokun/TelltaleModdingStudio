using TelltaleModding.Core.Archives;

namespace TelltaleModding.App.ViewModels;

/// <summary>One row in the archive-contents browser.</summary>
public sealed class ArchiveEntryViewModel : ViewModelBase
{
    public ArchiveEntryViewModel(ArchiveFileEntry entry)
    {
        Entry = entry;
    }

    public ArchiveFileEntry Entry { get; }

    public string Name => Entry.Name;
    public string Category => Entry.Category.ToString();
    public string SizeDisplay => FormatSize(Entry.Size);

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
