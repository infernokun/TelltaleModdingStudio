using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace TelltaleModding.App.Services;

/// <summary>Avalonia StorageProvider-backed implementation of <see cref="IDialogService"/>.</summary>
public sealed class DialogService : IDialogService
{
    private readonly Window _owner;

    public DialogService(Window owner) => _owner = owner;

    public async Task<string?> OpenFileAsync(string title, params (string Name, string[] Patterns)[] filters)
    {
        var results = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ToFileTypes(filters),
        });
        return results.Count > 0 ? results[0].TryGetLocalPath() : null;
    }

    public async Task<string?> OpenFolderAsync(string title)
    {
        var results = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return results.Count > 0 ? results[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName,
        params (string Name, string[] Patterns)[] filters)
    {
        var result = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = ToFileTypes(filters),
        });
        return result?.TryGetLocalPath();
    }

    private static List<FilePickerFileType>? ToFileTypes((string Name, string[] Patterns)[] filters) =>
        filters.Length == 0
            ? null
            : filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns }).ToList();
}
