namespace TelltaleModding.App.Services;

/// <summary>
/// Abstraction over the platform file/folder pickers so view-models never touch
/// Avalonia storage APIs directly.
/// </summary>
public interface IDialogService
{
    /// <summary>Pick a single existing file. Returns null if cancelled.</summary>
    Task<string?> OpenFileAsync(string title, params (string Name, string[] Patterns)[] filters);

    /// <summary>Pick an existing folder. Returns null if cancelled.</summary>
    Task<string?> OpenFolderAsync(string title);

    /// <summary>Pick a path to save a new file to. Returns null if cancelled.</summary>
    Task<string?> SaveFileAsync(string title, string suggestedName,
        params (string Name, string[] Patterns)[] filters);
}
