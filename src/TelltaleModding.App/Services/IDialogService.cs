namespace TelltaleModding.App.Services;

/// <summary>File and folder pickers for view models.</summary>
public interface IDialogService
{
    /// <summary>Pick a single existing file. Returns null if canceled.</summary>
    Task<string?> OpenFileAsync(string title, params (string Name, string[] Patterns)[] filters);

    /// <summary>Pick an existing folder. Returns null if canceled.</summary>
    Task<string?> OpenFolderAsync(string title);

    /// <summary>Pick a path to save a new file to. Returns null if canceled.</summary>
    Task<string?> SaveFileAsync(string title, string suggestedName,
        params (string Name, string[] Patterns)[] filters);
}
