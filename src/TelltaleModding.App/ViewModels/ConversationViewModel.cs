using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Dialogue;

namespace TelltaleModding.App.ViewModels;

/// <summary>
/// A scene's conversations from its <c>.dlog</c>: browse one as a tree of lines, choice
/// menus and branches, or click through it: lines play in order and choices wait for
/// the player. Lines are matched to <see cref="DialogueIndex"/> by language line id.
/// </summary>
public sealed partial class ConversationViewModel : ViewModelBase
{
    private static readonly ConditionalWeakTable<DialogueIndex, Dictionary<uint, DialogueEntry>> LineTables = new();

    private GameCatalog? _catalog;
    private Dictionary<uint, DialogueEntry> _lines = new();
    private DlogFile? _dialog;
    private int _loadGeneration;
    private bool _suppressScene;
    private readonly Dictionary<ulong, ConversationNodeViewModel> _itemsByNode = new();
    private DlogWalker? _walker;

    /// <summary>Raised when a line should be heard; hook it to the audio player.</summary>
    public event Action<DialogueEntry>? PlayLineRequested;

    /// <summary>Every <c>.dlog</c> in the game, by name.</summary>
    public ObservableCollection<string> Scenes { get; } = new();

    public ObservableCollection<ConversationItemViewModel> Conversations { get; } = new();

    /// <summary>Top-level items of the selected conversation's tree.</summary>
    public ObservableCollection<ConversationNodeViewModel> Tree { get; } = new();

    /// <summary>What has been said so far in click-through mode.</summary>
    public ObservableCollection<PlayStepViewModel> Transcript { get; } = new();

    /// <summary>The options waiting for a pick in click-through mode.</summary>
    public ObservableCollection<PlayOptionViewModel> PendingOptions { get; } = new();

    [ObservableProperty]
    private bool _hasSource;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string? _selectedScene;

    [ObservableProperty]
    private ConversationItemViewModel? _selectedConversation;

    [ObservableProperty]
    private ConversationNodeViewModel? _selectedNode;

    /// <summary>Hide conversations that never say anything (logic-only entry points).</summary>
    [ObservableProperty]
    private bool _onlyWithLines = true;

    /// <summary>Show bookkeeping nodes (start, logic, scripts, waits, idles) in the tree.</summary>
    [ObservableProperty]
    private bool _showLogic;

    /// <summary>Raise <see cref="PlayLineRequested"/> as click-through reaches each line.</summary>
    [ObservableProperty]
    private bool _speakLines = true;

    /// <summary>Move to the next line by itself when the host reports the clip ended.</summary>
    [ObservableProperty]
    private bool _autoAdvance = true;

    /// <summary>True while click-through waits for "Next" (a line was just shown).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AdvanceCommand))]
    private bool _canAdvance;

    [ObservableProperty]
    private string _playState = "";

    /// <summary>The tab shown: 0 = tree, 1 = click-through.</summary>
    [ObservableProperty]
    private int _modeIndex;

    public string Title => SelectedConversation?.Title ?? "";

    public bool HasConversation => SelectedConversation is not null;

    public bool HasPendingOptions => PendingOptions.Count > 0;

    public bool HasTranscript => Transcript.Count > 0;

    public ConversationViewModel()
    {
        Transcript.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTranscript));
    }

    // ---- Loading ----------------------------------------------------------

    /// <summary>
    /// Points the view at a game: lists its dialogs and opens <paramref name="sceneName"/>
    /// (a <c>.dlog</c> name or a <see cref="DialogueEntry.Scene"/>), when given.
    /// </summary>
    public void Load(GameCatalog catalog, DialogueIndex index, string? sceneName = null)
    {
        _catalog = catalog;
        _lines = LineTables.GetValue(index, i =>
        {
            var table = new Dictionary<uint, DialogueEntry>(i.Lines.Count);
            foreach (var line in i.Lines)
                table.TryAdd(line.Line.Id, line);
            return table;
        });
        HasSource = true;
        _suppressScene = true;
        SelectedScene = null;
        ClearScene();
        _sceneEpisodes = catalog.Entries
            .Where(e => e.Name.EndsWith(".dlog", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => Path.GetFileNameWithoutExtension(e.Name), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, (IReadOnlySet<string>)g.Select(e => DialogueIndex.EpisodeOf(e.ArchivePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .ToList();
        FillScenes();
        _suppressScene = false;
        if (!string.IsNullOrEmpty(sceneName))
            SelectScene(sceneName);
    }

    public void Clear()
    {
        _loadGeneration++;
        _catalog = null;
        _dialog = null;
        _lines = new();
        HasSource = false;
        _sceneEpisodes = new();
        Scenes.Clear();
        ClearScene();
        Status = "";
    }

    private List<(string Name, IReadOnlySet<string> Episodes)> _sceneEpisodes = new();
    private string? _episode;

    /// <summary>The scenes' episodes (archive labels, as <see cref="DialogueIndex.EpisodeOf"/> gives them).</summary>
    public IEnumerable<string> SceneEpisodes => _sceneEpisodes.SelectMany(s => s.Episodes);

    /// <summary>Raised when opening a scene outside the episode filter cleared it.</summary>
    public event Action? EpisodeFilterCleared;

    /// <summary>Lists only the scenes of <paramref name="episode"/> (all when null).</summary>
    public void SetEpisode(string? episode)
    {
        if (string.Equals(_episode, episode, StringComparison.OrdinalIgnoreCase))
            return;
        _episode = episode;
        FillScenes();
    }

    // Refills the scene list for the episode, keeping the open scene when it still fits.
    private void FillScenes()
    {
        var keep = SelectedScene;
        bool suppress = _suppressScene;
        _suppressScene = true;
        Scenes.Clear();
        foreach (var (name, episodes) in _sceneEpisodes)
            if (_episode is null || episodes.Contains(_episode))
                Scenes.Add(name);
        if (keep is not null && Scenes.Contains(keep))
            SelectedScene = keep;
        _suppressScene = suppress;
        Status = _episode is null ? $"{Scenes.Count:N0} dialogs" : $"{Scenes.Count:N0} dialogs in {_episode}";
    }

    /// <summary>Opens a scene's dialog by <c>.dlog</c> name or <see cref="DialogueEntry.Scene"/>; false if it has none.</summary>
    public bool SelectScene(string sceneName)
    {
        var stem = Path.GetFileNameWithoutExtension(sceneName);
        var match = Scenes.FirstOrDefault(s => s.Equals(stem, StringComparison.OrdinalIgnoreCase));
        // A jump or a line from another episode: show every scene again.
        if (match is null && _episode is not null &&
            _sceneEpisodes.Any(s => s.Name.Equals(stem, StringComparison.OrdinalIgnoreCase)))
        {
            _episode = null;
            FillScenes();
            EpisodeFilterCleared?.Invoke();
            match = Scenes.FirstOrDefault(s => s.Equals(stem, StringComparison.OrdinalIgnoreCase));
        }
        if (match is null)
            return false;
        if (match == SelectedScene)
            return true;
        SelectedScene = match;
        return true;
    }

    /// <summary>Opens the scene and conversation that say <paramref name="entry"/> and selects that line.</summary>
    public async Task<bool> ShowLineAsync(DialogueEntry entry)
    {
        if (!SelectScene(entry.Scene))
            return false;
        await _pendingLoad;
        if (_dialog?.Nodes.FirstOrDefault(n => n.LineIds.Contains(entry.Line.Id)) is not { } node)
            return false;
        GoToNode(node, entry.Line.Id);
        ModeIndex = 0;
        return true;
    }

    private Task _pendingLoad = Task.CompletedTask;

    partial void OnSelectedSceneChanged(string? value)
    {
        if (_suppressScene || value is null)
            return;
        _pendingLoad = LoadSceneAsync(value);
    }

    private async Task LoadSceneAsync(string scene)
    {
        if (_catalog is not { } catalog)
            return;
        int generation = ++_loadGeneration;
        IsLoading = true;
        ClearScene();
        Status = $"Reading {scene}.dlog…";
        try
        {
            var (dialog, error) = await Task.Run(() =>
            {
                var entry = catalog.Find(scene + ".dlog");
                if (entry is null)
                    return ((DlogFile?)null, "The dialog is not in the game's archives.");
                var data = catalog.Read(entry);
                if (!DlogFile.TryRead(data, out var parsed, out var reason))
                    return (null, reason);
                parsed!.ResolveNames(DlogFile.NameCandidates(data));
                return (parsed, (string?)null);
            });
            if (generation != _loadGeneration)
                return;
            if (dialog is null)
            {
                Status = error ?? "Could not read the dialog.";
                return;
            }
            _dialog = dialog;
            RefreshConversations();
            int choices = dialog.Nodes.Count(n => n.Kind == DlogNodeKind.Choices);
            Status = $"{Conversations.Count:N0} conversations · {choices:N0} choice menus · {dialog.Nodes.Count:N0} nodes";
            SelectedConversation = Conversations.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (generation == _loadGeneration)
                Status = $"Could not read the dialog: {ex.Message}";
        }
        finally
        {
            if (generation == _loadGeneration)
                IsLoading = false;
        }
    }

    private void ClearScene()
    {
        _dialog = null;
        StopPlayThrough();
        SelectedConversation = null;
        Conversations.Clear();
        Tree.Clear();
        _itemsByNode.Clear();
    }

    partial void OnOnlyWithLinesChanged(bool value)
    {
        var keep = SelectedConversation?.Branch;
        RefreshConversations();
        SelectedConversation = Conversations.FirstOrDefault(c => c.Branch == keep) ?? Conversations.FirstOrDefault();
    }

    partial void OnShowLogicChanged(bool value)
    {
        var node = SelectedNode?.Node;
        RebuildTree();
        if (node is not null && _itemsByNode.TryGetValue(node.Id, out var item))
            Reveal(item);
    }

    private void RefreshConversations()
    {
        Conversations.Clear();
        if (_dialog is not { } dialog)
            return;
        int index = 0;
        foreach (var folder in dialog.Folders)
        {
            foreach (var conversation in folder.Conversations)
            {
                index++;
                var item = new ConversationItemViewModel(dialog, conversation, folder, index, Describe);
                if (OnlyWithLines && item.LineCount == 0)
                    continue;
                Conversations.Add(item);
            }
        }
    }

    partial void OnSelectedConversationChanged(ConversationItemViewModel? value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasConversation));
        StopPlayThrough();
        RebuildTree();
    }

    // ---- Tree -------------------------------------------------------------

    private void RebuildTree()
    {
        Tree.Clear();
        _itemsByNode.Clear();
        SelectedNode = null;
        if (_dialog is null || SelectedConversation is null)
            return;
        var builder = new TreeBuilder(this, _dialog);
        foreach (var item in builder.Branch(SelectedConversation.Branch, null, 0))
            Tree.Add(item);
    }

    /// <summary>Selects the tree item of <paramref name="node"/>, switching conversation if needed.</summary>
    private void GoToNode(DlogNode node, uint? lineId = null)
    {
        if (_dialog is null)
            return;
        if (!_itemsByNode.ContainsKey(node.Id))
        {
            var conversation = _dialog.ConversationOf(node);
            var target = Conversations.FirstOrDefault(c => c.Branch == conversation);
            if (target is null && conversation is not null && OnlyWithLines)
            {
                OnlyWithLines = false;
                target = Conversations.FirstOrDefault(c => c.Branch == conversation);
            }
            if (target is not null)
                SelectedConversation = target;
        }
        if (!_itemsByNode.TryGetValue(node.Id, out var item))
            return;
        if (lineId is { } id)
        {
            var siblings = item.Parent?.Children ?? (IEnumerable<ConversationNodeViewModel>)Tree;
            item = siblings.FirstOrDefault(c => c.Node == node && c.Entry?.Line.Id == id) ?? item;
        }
        Reveal(item);
    }

    private void Reveal(ConversationNodeViewModel item)
    {
        for (var parent = item.Parent; parent is not null; parent = parent.Parent)
            parent.IsExpanded = true;
        SelectedNode = item;
    }

    internal void FollowJump(ConversationNodeViewModel item)
    {
        if (_dialog is null || item.Node is not { } jump)
            return;
        if (jump.Jump is { OtherDialog: not 0 } info)
        {
            var other = _catalog?.FindBySymbol(info.OtherDialog);
            if (other is not null)
                SelectScene(other.Name);
            return;
        }
        if (_dialog.JumpTarget(jump) is { } target)
            GoToNode(target);
    }

    internal void RequestPlay(DialogueEntry entry) => PlayLineRequested?.Invoke(entry);

    [RelayCommand]
    private void ExpandAll() => SetExpanded(Tree, true);

    [RelayCommand]
    private void CollapseAll() => SetExpanded(Tree, false);

    private static void SetExpanded(IEnumerable<ConversationNodeViewModel> items, bool expanded)
    {
        foreach (var item in items)
        {
            item.IsExpanded = expanded;
            SetExpanded(item.Children, expanded);
        }
    }

    // ---- Text helpers ------------------------------------------------------

    internal DialogueEntry? Line(uint id) => _lines.GetValueOrDefault(id);

    /// <summary>"Speaker: text" for a line id, or a placeholder when the language database lacks it.</summary>
    internal string Describe(uint id) =>
        Line(id) is { } entry
            ? (entry.Line.Speaker.Length > 0 ? $"{TitleCase(entry.Line.Speaker)}: {Text(entry)}" : Text(entry))
            : $"(no text · line {id})";

    internal static string Text(DialogueEntry entry) => entry.Line.Text.Length > 0 ? entry.Line.Text : "(silence)";

    internal string OptionText(uint id) => Line(id) is { } entry ? Text(entry) : $"line {id}";

    internal static string TitleCase(string name) =>
        string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));

    /// <summary>
    /// The label of a choice option: its text, its conditional's case texts, or a
    /// placeholder with the first thing said in its branch as the detail.
    /// </summary>
    internal (string Label, string Detail, bool Silent) OptionLabel(DlogChild option)
    {
        if (option.TextIds.Count > 0)
            return (OptionText(option.TextIds[0]), "", false);
        // A textless option that opens with a conditional shows its cases' texts.
        if (_dialog?.Head(option) is { Kind: DlogNodeKind.Conditional } conditional
            && conditional.Children.Where(c => c.TextIds.Count > 0).Select(c => OptionText(c.TextIds[0])).ToList() is { Count: > 0 } cases)
            return (string.Join("  /  ", cases), "Depends on earlier choices", false);
        var first = FirstLine(option) is { } id ? Describe(id) : "";
        return (option.IsTimeout ? "(timer runs out)" : "(no option text)", first, true);
    }

    /// <summary>The first line said anywhere in a branch, depth first.</summary>
    internal uint? FirstLine(DlogChild branch)
    {
        if (_dialog is null)
            return null;
        var seen = new HashSet<ulong>();
        var stack = new Stack<DlogChild>();
        stack.Push(branch);
        while (stack.Count > 0)
        {
            foreach (var node in _dialog.Chain(stack.Pop()))
            {
                if (!seen.Add(node.Id))
                    break;
                if (node.LineIds.Count > 0)
                    return node.LineIds[0];
                foreach (var child in node.Children.Reverse())
                    stack.Push(child);
            }
        }
        return null;
    }

    internal string JumpLabel(DlogNode jump)
    {
        if (jump.Jump is not { } info || _dialog is null)
            return "Jump";
        if (info.OtherDialog != 0)
        {
            var other = _catalog?.FindBySymbol(info.OtherDialog);
            return other is not null ? $"Continues in {Path.GetFileNameWithoutExtension(other.Name)}" : "Continues in another dialog";
        }
        var target = _dialog.JumpTarget(jump);
        if (target is null)
            return info.Target == DlogJumpTarget.ToName ? "Jump to a node not in this dialog" : "Jump back";
        string where = target.Name ?? target.Kind switch
        {
            DlogNodeKind.Choices => "the choice",
            DlogNodeKind.Start when _dialog.ConversationOf(target) is { } c => $"conversation {Conversations.FirstOrDefault(x => x.Branch == c)?.Title ?? "start"}",
            _ => target.Kind.ToString().ToLowerInvariant(),
        };
        string verb = info.Target == DlogJumpTarget.ToParent ? "Back to" : "Go to";
        string ret = info.Behaviour == DlogJumpBehaviour.JumpExecuteAndReturn ? " and return" : "";
        return $"{verb} {where}{ret}";
    }

    // ---- Click-through -----------------------------------------------------

    [RelayCommand]
    private void StartPlayThrough()
    {
        if (_dialog is null || SelectedConversation is null)
            return;
        StopPlayThrough();
        ModeIndex = 1;
        _walker = new DlogWalker(_dialog);
        _walker.Start(SelectedConversation.Branch);
        Pump();
    }

    /// <summary>Starts click-through at the selected tree item.</summary>
    [RelayCommand]
    private void PlayFromHere(ConversationNodeViewModel? item)
    {
        item ??= SelectedNode;
        if (_dialog is null || item?.Node is not { } node)
            return;
        StopPlayThrough();
        ModeIndex = 1;
        _walker = new DlogWalker(_dialog);
        _walker.StartAt(node);
        Pump();
    }

    private void StopPlayThrough()
    {
        _walker = null;
        Transcript.Clear();
        PendingOptions.Clear();
        OnPropertyChanged(nameof(HasPendingOptions));
        CanAdvance = false;
        PlayState = "";
    }

    [RelayCommand(CanExecute = nameof(CanAdvance))]
    private void Advance()
    {
        CanAdvance = false;
        Pump();
    }

    /// <summary>Call when the line requested last finished playing, to move on in click-through.</summary>
    public void OnLineFinished()
    {
        if (AutoAdvance && CanAdvance)
            Advance();
    }

    [RelayCommand]
    private void Choose(PlayOptionViewModel? option)
    {
        if (option is null || _walker is null)
            return;
        PendingOptions.Clear();
        OnPropertyChanged(nameof(HasPendingOptions));
        if (option.ShowInTranscript)
            Transcript.Add(PlayStepViewModel.Choice(option.Label));
        _walker.Choose(option.Index);
        Pump();
    }

    [RelayCommand]
    private void ReplayStep(PlayStepViewModel? step)
    {
        if (step?.Entry is { } entry)
            RequestPlay(entry);
    }

    private int _pumpGeneration;

    // Runs the walker to its next line, decision or end.
    private async void Pump()
    {
        if (_walker is not { } walker)
            return;
        int generation = ++_pumpGeneration;
        var step = walker.Next();
        switch (step.Kind)
        {
            case DlogStepKind.Line:
            {
                var entry = Line(step.LineId);
                Transcript.Add(PlayStepViewModel.Spoken(entry, Describe(step.LineId)));
                PlayState = "";
                CanAdvance = true;
                if (entry is not null && SpeakLines && entry.HasAudio)
                {
                    RequestPlay(entry);
                }
                else if (AutoAdvance)
                {
                    // Nothing to hear: give the reader a moment, then carry on.
                    await Task.Delay(1500);
                    if (generation == _pumpGeneration && CanAdvance && _walker == walker)
                        Advance();
                }
                break;
            }
            case DlogStepKind.Decision:
                PendingOptions.Clear();
                for (int i = 0; i < step.Options.Count; i++)
                    PendingOptions.Add(DescribeOption(step, i));
                OnPropertyChanged(nameof(HasPendingOptions));
                PlayState = step.Decision switch
                {
                    DlogDecisionKind.Choice => "Choose a reply",
                    DlogDecisionKind.Condition => "The game decides this from earlier choices. Pick a branch.",
                    _ => "The game varies this between visits. Pick one.",
                };
                break;
            case DlogStepKind.End:
            {
                string message = step.End switch
                {
                    DlogEndReason.Exit or DlogEndReason.Finished => "End of conversation.",
                    DlogEndReason.OtherDialog => _catalog?.FindBySymbol(step.OtherDialog) is { } other
                        ? $"Continues in {Path.GetFileNameWithoutExtension(other.Name)}."
                        : "Continues in another dialog.",
                    DlogEndReason.UnresolvedJump => "Jumps to a node that is not in this dialog.",
                    _ => "Stopped: the conversation loops on game state here.",
                };
                Transcript.Add(PlayStepViewModel.Info(message));
                PlayState = message;
                break;
            }
        }
    }

    private PlayOptionViewModel DescribeOption(DlogStep step, int index)
    {
        var option = step.Options[index];
        if (step.Decision == DlogDecisionKind.Choice)
        {
            if (option.TextId is { } id)
                return new PlayOptionViewModel(index, OptionText(id), option.Case is not null ? "Only offered in some playthroughs" : "", false, true);
            var (label, why, silent) = OptionLabel(option.Branch);
            return new PlayOptionViewModel(index, label, why, silent, true);
        }
        string text = option.TextId is { } caseText ? OptionText(caseText) : $"{(step.Decision == DlogDecisionKind.Condition ? "Case" : "Variant")} {index + 1}";
        string detail = FirstLine(option.Branch) is { } first ? Describe(first) : "(says nothing)";
        return new PlayOptionViewModel(index, text, detail, option.TextId is null, false);
    }

    // ---- Tree construction -------------------------------------------------

    private sealed class TreeBuilder
    {
        private readonly ConversationViewModel _owner;
        private readonly DlogFile _dialog;
        private readonly HashSet<ulong> _placed = new();

        public TreeBuilder(ConversationViewModel owner, DlogFile dialog)
        {
            _owner = owner;
            _dialog = dialog;
        }

        private static bool IsBookkeeping(DlogNode node) => node.Kind is DlogNodeKind.Start or DlogNodeKind.Logic
            or DlogNodeKind.Script or DlogNodeKind.Wait or DlogNodeKind.Idle or DlogNodeKind.Marker
            or DlogNodeKind.Notes or DlogNodeKind.Chore or DlogNodeKind.CancelChoices
            || (node.Kind is DlogNodeKind.Exchange or DlogNodeKind.Text && node.LineIds.Count == 0);

        /// <summary>The items for one branch's chain, depth first.</summary>
        public List<ConversationNodeViewModel> Branch(DlogChild branch, ConversationNodeViewModel? parent, int depth)
        {
            var items = new List<ConversationNodeViewModel>();
            var hidden = new List<DlogNode>();
            foreach (var node in _dialog.Chain(branch))
            {
                if (!_placed.Add(node.Id))
                {
                    items.Add(Item(node, parent, "SEE", "Continues above", Badge.Link, isLink: true));
                    break;
                }
                if (depth > 40)
                    break;
                if (IsBookkeeping(node) && !_owner.ShowLogic)
                {
                    hidden.Add(node);
                    continue;
                }
                int before = items.Count;
                AddNode(node, parent, depth, items);
                if (items.Count > before)
                {
                    foreach (var h in hidden)
                        _owner._itemsByNode.TryAdd(h.Id, items[before]);
                    hidden.Clear();
                }
            }
            // Hidden nodes at the end of a branch point at the branch's own item.
            if (parent is not null)
                foreach (var h in hidden)
                    _owner._itemsByNode.TryAdd(h.Id, parent);
            else if (items.Count > 0)
                foreach (var h in hidden)
                    _owner._itemsByNode.TryAdd(h.Id, items[^1]);
            return items;
        }

        private void AddNode(DlogNode node, ConversationNodeViewModel? parent, int depth, List<ConversationNodeViewModel> items)
        {
            switch (node.Kind)
            {
                case DlogNodeKind.Exchange:
                case DlogNodeKind.Text:
                    foreach (var id in node.LineIds)
                    {
                        var entry = _owner.Line(id);
                        var item = new ConversationNodeViewModel(_owner, node, parent)
                        {
                            Badge = node.Kind == DlogNodeKind.Text ? "TEXT" : "LINE",
                            BadgeBrush = Badge.Line,
                            Speaker = entry is { Line.Speaker.Length: > 0 } ? TitleCase(entry.Line.Speaker) : "",
                            Text = entry is not null ? ConversationViewModel.Text(entry) : $"(no text · line {id})",
                            IsMuted = entry is null,
                            Detail = entry is { Line.Directions.Count: > 0 } ? string.Join(" · ", entry.Line.Directions) : "",
                            Entry = entry,
                        };
                        Register(node, item);
                        items.Add(item);
                    }
                    if (node.LineIds.Count == 0)
                        items.Add(Register(node, Item(node, parent, "LINES", "(no lines)", Badge.Logic)));
                    break;
                case DlogNodeKind.Choices:
                {
                    var menu = Register(node, Item(node, parent, "CHOICE", node.Name ?? "Choice", Badge.Choice));
                    menu.Detail = $"{node.Options.Count()} options";
                    foreach (var child in node.Children)
                    {
                        string label;
                        string detail = "";
                        string badge = "OPTION";
                        bool silent = false;
                        switch (child.Kind)
                        {
                            case DlogChildKind.PreChoice:
                                label = "Before the options";
                                badge = "PRE";
                                break;
                            case DlogChildKind.PostChoice:
                                label = "After any option";
                                badge = "POST";
                                break;
                            default:
                                (label, detail, silent) = _owner.OptionLabel(child);
                                break;
                        }
                        var option = new ConversationNodeViewModel(_owner, null, menu)
                        {
                            Badge = badge,
                            BadgeBrush = child.Kind == DlogChildKind.Choice ? Badge.Option : Badge.Logic,
                            Text = label,
                            Detail = detail,
                            IsMuted = silent || child.Kind != DlogChildKind.Choice,
                            Child = child,
                        };
                        foreach (var sub in Branch(child, option, depth + 1))
                            option.Children.Add(sub);
                        if (child.Kind != DlogChildKind.Choice && option.Children.Count == 0)
                            continue;
                        option.IsExpanded = child.Kind == DlogChildKind.Choice;
                        menu.Children.Add(option);
                    }
                    menu.IsExpanded = true;
                    items.Add(menu);
                    break;
                }
                case DlogNodeKind.Conditional:
                case DlogNodeKind.Sequence:
                case DlogNodeKind.Parallel:
                {
                    var (badge, title) = node.Kind switch
                    {
                        DlogNodeKind.Conditional => ("IF", node.Name ?? "Depends on earlier choices"),
                        DlogNodeKind.Sequence => ("VARIES", node.Name ?? "One of these per visit"),
                        _ => ("TOGETHER", node.Name ?? "All at once"),
                    };
                    var group = Register(node, Item(node, parent, badge, title, Badge.Branch));
                    int n = 0;
                    foreach (var child in node.Children)
                    {
                        n++;
                        var caseText = child.TextIds.Count > 0 ? _owner.OptionText(child.TextIds[0]) : null;
                        var branch = new ConversationNodeViewModel(_owner, null, group)
                        {
                            Badge = node.Kind == DlogNodeKind.Conditional ? "CASE" : "PART",
                            BadgeBrush = caseText is not null ? Badge.Option : Badge.Branch,
                            Text = caseText ?? (node.Kind == DlogNodeKind.Conditional ? $"Case {n}" : $"Part {n}"),
                            IsMuted = caseText is null,
                            Child = child,
                            IsExpanded = true,
                        };
                        foreach (var sub in Branch(child, branch, depth + 1))
                            branch.Children.Add(sub);
                        group.Children.Add(branch);
                    }
                    group.IsExpanded = true;
                    items.Add(group);
                    break;
                }
                case DlogNodeKind.Jump:
                {
                    var item = Register(node, Item(node, parent, "JUMP", _owner.JumpLabel(node), Badge.Link, isLink: true));
                    item.IsLink = node.Jump is { OtherDialog: not 0 } || _dialog.JumpTarget(node) is not null;
                    items.Add(item);
                    break;
                }
                case DlogNodeKind.Exit:
                    items.Add(Register(node, Item(node, parent, "END", "End of conversation", Badge.End)));
                    break;
                default:
                {
                    string text = node.Script is { Length: > 0 } script
                        ? script.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? ""
                        : node.Name ?? "";
                    var badge = node.Kind == DlogNodeKind.Other ? node.ClassName : node.Kind.ToString().ToUpperInvariant();
                    var item = Register(node, Item(node, parent, badge, text, Badge.Logic));
                    item.IsMuted = true;
                    items.Add(item);
                    break;
                }
            }
        }

        private ConversationNodeViewModel Register(DlogNode node, ConversationNodeViewModel item)
        {
            _owner._itemsByNode.TryAdd(node.Id, item);
            return item;
        }

        private ConversationNodeViewModel Item(DlogNode node, ConversationNodeViewModel? parent, string badge, string text, IBrush brush, bool isLink = false) =>
            new(_owner, node, parent) { Badge = badge, Text = text, BadgeBrush = brush, IsLink = isLink };
    }

    private static class Badge
    {
        public static readonly IBrush Line = new SolidColorBrush(Color.Parse("#2B3242"));
        public static readonly IBrush Choice = new SolidColorBrush(Color.Parse("#F0545C"));
        public static readonly IBrush Option = new SolidColorBrush(Color.Parse("#7A3A40"));
        public static readonly IBrush Branch = new SolidColorBrush(Color.Parse("#6B4E1F"));
        public static readonly IBrush Link = new SolidColorBrush(Color.Parse("#245A49"));
        public static readonly IBrush End = new SolidColorBrush(Color.Parse("#3A3F4B"));
        public static readonly IBrush Logic = new SolidColorBrush(Color.Parse("#22262F"));
    }
}

/// <summary>Converters for the conversation view.</summary>
public static class ConversationConverters
{
    public static readonly IValueConverter InfoStyle =
        new FuncValueConverter<bool, FontStyle>(isInfo => isInfo ? FontStyle.Italic : FontStyle.Normal);
}

/// <summary>One conversation of a dialog, as listed.</summary>
public sealed class ConversationItemViewModel
{
    public ConversationItemViewModel(DlogFile dialog, DlogChild branch, DlogFolder folder, int number, Func<uint, string> describe)
    {
        Branch = branch;
        var start = dialog.Head(branch);
        var lines = new List<uint>();
        int choices = 0;
        var seen = new HashSet<ulong>();
        var pending = new Stack<DlogChild>();
        pending.Push(branch);
        while (pending.Count > 0)
        {
            foreach (var node in dialog.Chain(pending.Pop()))
            {
                if (!seen.Add(node.Id))
                    break;
                lines.AddRange(node.LineIds);
                if (node.Kind == DlogNodeKind.Choices)
                    choices++;
                foreach (var child in node.Children.Reverse())
                    pending.Push(child);
            }
        }
        LineCount = lines.Count;
        ChoiceCount = choices;
        Name = start?.Name;
        // The first line with words in it (some lines are silent beats).
        Preview = lines.Select(describe).FirstOrDefault(d => !d.EndsWith("(silence)", StringComparison.Ordinal))
                  ?? (lines.Count > 0 ? describe(lines[0]) : "");
        Title = Name ?? (Preview.Length > 0 ? Preview : $"Conversation {number}");
        var parts = new List<string>();
        if (folder.Name is { } f && f != "Default")
            parts.Add(f);
        parts.Add(LineCount == 1 ? "1 line" : $"{LineCount:N0} lines");
        if (choices > 0)
            parts.Add(choices == 1 ? "1 choice" : $"{choices} choices");
        Subtitle = string.Join(" · ", parts);
    }

    public DlogChild Branch { get; }

    /// <summary>The start node's name (<c>cs_enter</c>…) when known.</summary>
    public string? Name { get; }

    public string Title { get; }

    /// <summary>The first line, shown under a named conversation.</summary>
    public string Preview { get; }

    public bool HasPreview => Name is not null && Preview.Length > 0;

    public string Subtitle { get; }
    public int LineCount { get; }
    public int ChoiceCount { get; }
}

/// <summary>An item of the conversation tree: a line, a choice menu or option, a branch, a jump…</summary>
public sealed partial class ConversationNodeViewModel : ViewModelBase
{
    private readonly ConversationViewModel _owner;

    public ConversationNodeViewModel(ConversationViewModel owner, DlogNode? node, ConversationNodeViewModel? parent)
    {
        _owner = owner;
        Node = node;
        Parent = parent;
    }

    /// <summary>The node shown (null for an option or case, which are branches).</summary>
    public DlogNode? Node { get; }

    /// <summary>The branch shown, for options and cases.</summary>
    public DlogChild? Child { get; init; }

    public ConversationNodeViewModel? Parent { get; }

    public ObservableCollection<ConversationNodeViewModel> Children { get; } = new();

    public string Badge { get; init; } = "";
    public IBrush? BadgeBrush { get; init; }
    public string Speaker { get; init; } = "";
    public bool HasSpeaker => Speaker.Length > 0;
    public string Text { get; init; } = "";
    public string Detail { get; set; } = "";
    public bool HasDetail => Detail.Length > 0;
    public bool IsMuted { get; set; }

    /// <summary>The line this item says, when it is a line.</summary>
    public DialogueEntry? Entry { get; init; }

    public bool CanPlay => Entry?.HasAudio == true;

    /// <summary>A jump whose target can be shown.</summary>
    public bool IsLink { get; set; }

    [ObservableProperty]
    private bool _isExpanded;

    [RelayCommand]
    private void Play()
    {
        if (Entry is not null)
            _owner.RequestPlay(Entry);
    }

    [RelayCommand]
    private void GoTo() => _owner.FollowJump(this);
}

/// <summary>A line said, a choice made, or a note, in the click-through transcript.</summary>
public sealed class PlayStepViewModel
{
    private PlayStepViewModel(string speaker, string text, DialogueEntry? entry, bool isChoice, bool isInfo)
    {
        Speaker = speaker;
        Text = text;
        Entry = entry;
        IsChoice = isChoice;
        IsInfo = isInfo;
    }

    public string Speaker { get; }
    public string Text { get; }
    public bool HasSpeaker => Speaker.Length > 0;
    public DialogueEntry? Entry { get; }
    public bool CanReplay => Entry?.HasAudio == true;
    public bool IsChoice { get; }
    public bool IsInfo { get; }
    public bool IsLine => !IsChoice && !IsInfo;

    /// <summary>Accent for the player's picks, muted for notes.</summary>
    public IBrush Foreground => IsChoice ? ChoiceBrush : IsInfo ? MutedBrush : TextBrush;

    private static readonly IBrush ChoiceBrush = new SolidColorBrush(Color.Parse("#F0545C"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#9099A8"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#E7E9EE"));

    internal static PlayStepViewModel Spoken(DialogueEntry? entry, string fallback) =>
        entry is null
            ? new PlayStepViewModel("", fallback, null, false, false)
            : new PlayStepViewModel(entry.Line.Speaker.Length > 0 ? ConversationViewModel.TitleCase(entry.Line.Speaker) : "",
                ConversationViewModel.Text(entry), entry, false, false);

    internal static PlayStepViewModel Choice(string text) => new("", "▸ " + text, null, true, false);

    internal static PlayStepViewModel Info(string text) => new("", text, null, false, true);
}

/// <summary>An option offered in click-through: a choice, a conditional case, a sequence part.</summary>
public sealed class PlayOptionViewModel
{
    public PlayOptionViewModel(int index, string label, string detail, bool isMuted, bool showInTranscript)
    {
        Index = index;
        Label = label;
        Detail = detail;
        IsMuted = isMuted;
        ShowInTranscript = showInTranscript;
    }

    public int Index { get; }
    public string Label { get; }
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;
    public bool IsMuted { get; }
    internal bool ShowInTranscript { get; }
}
