using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleModding.App.Services;
using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Scripts;

namespace TelltaleModding.App.ViewModels;

/// <summary>How a script is shown.</summary>
public enum ScriptViewMode { Decompiled, Disassembly }

/// <summary>
/// The open game's Lua scripts: find a script by name or by what it contains, read
/// it as decompiled Lua or as an annotated listing, and save it.
/// </summary>
public sealed partial class ScriptsViewModel : ViewModelBase
{
    private GameCatalog? _catalog;
    private List<ScriptRowViewModel> _all = new();
    private ConcurrentDictionary<ArchiveEntryRef, string>? _contentIndex;
    private Task? _contentIndexTask;
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _filterCts;
    private int _loadGeneration;
    private LuaScript? _script;

    /// <summary>Set by the shell so pickers can be shown.</summary>
    public IDialogService? Dialogs { get; set; }

    /// <summary>Raised with a short status message for the status bar.</summary>
    public event Action<string>? StatusMessage;

    /// <summary>Raised to scroll the code view to the first occurrence of a text.</summary>
    public event Action<string>? RevealRequested;

    [ObservableProperty]
    private bool _hasSource;

    public void Load(GameCatalog catalog)
    {
        _catalog = catalog;
        _contentIndex = null;
        _contentIndexTask = null;
        _all = catalog.Entries.Where(e => LuaScriptReader.IsScriptFile(e.Name))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new ScriptRowViewModel(e))
            .ToList();
        HasSource = true;
        // Cleared through the field so the search doesn't run against the old list.
#pragma warning disable MVVMTK0034
        _query = "";
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(Query));
        ClearScript();
        _ = FilterAsync();
    }

    public void Clear()
    {
        _catalog = null;
        _all.Clear();
        _contentIndex = null;
        HasSource = false;
        Scripts.Clear();
        ClearScript();
    }

    /// <summary>Selects the script called <paramref name="name"/>.</summary>
    public async Task OpenAsync(string name)
    {
        var row = _all.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return;
        if (!Scripts.Contains(row))
        {
            Query = "";
            await FilterAsync();
        }
        Selected = row;
    }

    // ---- List & search ----------------------------------------------------

    public ObservableCollection<ScriptRowViewModel> Scripts { get; } = new();

    [ObservableProperty]
    private string _query = "";

    /// <summary>Also match the strings and function names inside each script.</summary>
    [ObservableProperty]
    private bool _searchContents;

    [ObservableProperty]
    private string _summary = "";

    partial void OnQueryChanged(string value) => QueueFilter(debounce: true);

    partial void OnSearchContentsChanged(bool value) => QueueFilter();

    private async void QueueFilter(bool debounce = false)
    {
        _debounceCts?.Cancel();
        if (debounce)
        {
            var cts = _debounceCts = new CancellationTokenSource();
            try
            {
                await Task.Delay(200, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        await FilterAsync();
    }

    private async Task FilterAsync()
    {
        if (_catalog is null)
            return;
        _filterCts?.Cancel();
        var cts = _filterCts = new CancellationTokenSource();
        var terms = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool contents = SearchContents && terms.Length > 0;
        if (contents)
        {
            Summary = "Searching inside scripts…";
            await EnsureContentIndexAsync();
            if (cts.IsCancellationRequested)
                return;
        }
        var index = _contentIndex;
        var all = _all;
        List<ScriptRowViewModel> hits;
        try
        {
            hits = await Task.Run(() => all.Where(row =>
            {
                cts.Token.ThrowIfCancellationRequested();
                return terms.All(t => row.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                                      (contents && index is not null && index.TryGetValue(row.Entry, out var text) &&
                                       text.Contains(t, StringComparison.OrdinalIgnoreCase)));
            }).ToList(), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts != _filterCts)
            return;
        Scripts.Clear();
        foreach (var hit in hits)
            Scripts.Add(hit);
        Summary = hits.Count == all.Count ? $"{all.Count:N0} scripts" : $"{hits.Count:N0} of {all.Count:N0} scripts";
    }

    // Function names and string constants of every script, read once.
    private Task EnsureContentIndexAsync()
    {
        if (_contentIndexTask is not null || _catalog is null)
            return _contentIndexTask ?? Task.CompletedTask;
        var catalog = _catalog;
        var rows = _all.ToList();
        var index = new ConcurrentDictionary<ArchiveEntryRef, string>();
        _contentIndexTask = Task.Run(() =>
        {
            Parallel.ForEach(rows, new ParallelOptions { MaxDegreeOfParallelism = 4 }, row =>
            {
                try
                {
                    var script = LuaScriptReader.Read(catalog.Read(row.Entry), row.Name);
                    index[row.Entry] = script.Kind == LuaScriptKind.Source
                        ? script.Decompiled
                        : string.Join('\n', script.FunctionNames.Concat(script.StringConstants));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                }
            });
            if (catalog == _catalog)
                _contentIndex = index;
        });
        return _contentIndexTask;
    }

    // ---- Viewer -----------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDecompiledCommand), nameof(SaveOriginalCommand))]
    private ScriptRowViewModel? _selected;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _info = "";

    [ObservableProperty]
    private string _code = "";

    [ObservableProperty]
    private bool _hasScript;

    [ObservableProperty]
    private bool _hasDisassembly;

    [ObservableProperty]
    private ScriptViewMode _viewMode = ScriptViewMode.Decompiled;

    public bool IsDecompiledView { get => ViewMode == ScriptViewMode.Decompiled; set { if (value) ViewMode = ScriptViewMode.Decompiled; } }
    public bool IsDisassemblyView { get => ViewMode == ScriptViewMode.Disassembly; set { if (value) ViewMode = ScriptViewMode.Disassembly; } }

    public ObservableCollection<string> Outline { get; } = new();

    partial void OnSelectedChanged(ScriptRowViewModel? value)
    {
        if (value is not null)
            _ = LoadScriptAsync(value);
    }

    partial void OnViewModeChanged(ScriptViewMode value)
    {
        OnPropertyChanged(nameof(IsDecompiledView));
        OnPropertyChanged(nameof(IsDisassemblyView));
        _ = ShowCodeAsync();
    }

    private void ClearScript()
    {
        _loadGeneration++;
        _script = null;
        Selected = null;
        HasScript = HasDisassembly = false;
        Title = Info = Code = "";
        Outline.Clear();
    }

    private async Task LoadScriptAsync(ScriptRowViewModel row)
    {
        if (_catalog is null)
            return;
        int generation = ++_loadGeneration;
        var catalog = _catalog;
        Title = row.Name;
        Info = "Reading…";
        IsLoading = true;
        try
        {
            var script = await Task.Run(() => LuaScriptReader.Read(catalog.Read(row.Entry), row.Name));
            if (generation != _loadGeneration)
                return;
            _script = script;
            HasScript = true;
            HasDisassembly = script.Kind is LuaScriptKind.Bytecode51 or LuaScriptKind.Bytecode52;
            if (!HasDisassembly)
                ViewMode = ScriptViewMode.Decompiled;
            Outline.Clear();
            foreach (var name in script.FunctionNames)
                Outline.Add(name);
            await ShowCodeAsync();
            if (generation != _loadGeneration)
                return;
            var parts = new List<string> { script.VersionLabel, AssetsViewModel.FormatSize(row.Entry.Size) };
            if (HasDisassembly)
                parts.Add($"{script.FunctionCount:N0} functions");
            if (script.DecompileFallbacks > 0)
                parts.Add($"{script.DecompileFallbacks} region{(script.DecompileFallbacks == 1 ? "" : "s")} shown as listing");
            Info = string.Join(" · ", parts);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            if (generation == _loadGeneration)
                Info = $"Could not read: {ex.Message}";
        }
        finally
        {
            if (generation == _loadGeneration)
                IsLoading = false;
        }
    }

    private async Task ShowCodeAsync()
    {
        if (_script is not { } script)
            return;
        var mode = ViewMode;
        int generation = _loadGeneration;
        var code = await Task.Run(() => mode == ScriptViewMode.Disassembly ? script.Disassembly : script.Decompiled);
        if (generation == _loadGeneration && mode == ViewMode)
            Code = code;
    }

    /// <summary>Scrolls the code view to a function from the outline.</summary>
    [RelayCommand]
    private void Reveal(string? functionName)
    {
        if (string.IsNullOrEmpty(functionName))
            return;
        RevealRequested?.Invoke(functionName);
    }

    // ---- Save -------------------------------------------------------------

    private bool HasSelection() => Selected is not null && _catalog is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SaveDecompiledAsync()
    {
        if (Dialogs is null || _script is null || Selected is null)
            return;
        var name = Path.GetFileNameWithoutExtension(Selected.Name) + ".lua";
        var path = await Dialogs.SaveFileAsync("Save decompiled script", name, ("Lua script", new[] { "*.lua" }));
        if (path is null)
            return;
        var script = _script;
        await Task.Run(() => File.WriteAllText(path, script.Decompiled));
        StatusMessage?.Invoke($"Saved {Path.GetFileName(path)}");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SaveOriginalAsync()
    {
        if (Dialogs is null || _catalog is null || Selected is null)
            return;
        var entry = Selected.Entry;
        var path = await Dialogs.SaveFileAsync("Save script as stored in the game", Selected.Name, ("All files", new[] { "*" }));
        if (path is null)
            return;
        var catalog = _catalog;
        await Task.Run(() => File.WriteAllBytes(path, catalog.Read(entry)));
        StatusMessage?.Invoke($"Saved {Path.GetFileName(path)}");
    }
}

/// <summary>One script in the list.</summary>
public sealed class ScriptRowViewModel
{
    public ScriptRowViewModel(ArchiveEntryRef entry) => Entry = entry;

    public ArchiveEntryRef Entry { get; }
    public string Name => Entry.Name;
    public string Detail => $"{Path.GetFileName(Entry.ArchivePath)} · {AssetsViewModel.FormatSize(Entry.Size)}";
}
