using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Numerics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleModding.App.Services;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Dialogue;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.App.ViewModels;

/// <summary>
/// The Face tab: poses the shown character's face over the body clip (a mood's expression,
/// mouth shapes, a head/face gesture, per-bone tweaks) and lip-syncs their voice lines.
/// </summary>
public sealed partial class FaceViewModel : ViewModelBase
{
    private static readonly IReadOnlyDictionary<string, FaceClipSet> NoClips = new Dictionary<string, FaceClipSet>();

    private GameCatalog? _catalog;
    private GameAudio? _audio;
    private Func<string, byte[]?>? _read;
    private Task<IReadOnlyDictionary<string, FaceClipSet>> _index = Task.FromResult(NoClips);
    private readonly Dictionary<string, AnimationFile?> _clips = new(StringComparer.OrdinalIgnoreCase);

    private string _characterId = "", _characterName = "";
    private FaceClipSet? _set;
    private FaceRig? _rig;
    private SkeletonFile? _listsSkeleton;
    private FaceClipSet? _listsSet;
    private FaceMood? _lineMood;
    private int _modelGeneration, _moodGeneration, _lineGeneration;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _gestureClock = new(), _lineClock = new();

    public FaceViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
        Player.Ended += () => StopLine();
    }

    /// <summary>Raised when the face changed and the frame should be drawn again.</summary>
    public event Action? PoseChanged;

    /// <summary>Raised with a camera distance (see <c>MeshViewport.FocusFace</c>) to frame the face.</summary>
    public event Action<float>? FrameFaceRequested;

    /// <summary>The game's voice lines; set by the shell.</summary>
    public Func<DialogueIndex?>? DialogueSource { get; set; }

    /// <summary>Plays the voice line being lip-synced.</summary>
    public AudioPlayerViewModel Player { get; } = new();

    /// <summary>The rig, while the shown model can be posed.</summary>
    public FaceRig? Rig => _rig;

    /// <summary>True when the face differs from the body clip (so frames go through the rig).</summary>
    public bool IsActive => _rig is { IsNeutral: false };

    // ---- Sources -----------------------------------------------------------

    /// <summary>Where clips come from: the open game, or loose files (<paramref name="catalog"/> null).</summary>
    public void UseSource(GameCatalog? catalog, Func<string, byte[]?> read, IEnumerable<string> fileNames)
    {
        Clear();
        _catalog = catalog;
        _audio = catalog is null ? null : new GameAudio(catalog);
        _read = read;
        var names = fileNames.ToList();
        _index = Task.Run(() => FaceClipSet.Index(names));
    }

    /// <summary>Forgets the source, the character and the rig.</summary>
    public void Clear()
    {
        StopLine();
        StopGesture();
        _catalog = null;
        _audio = null;
        _read = null;
        _index = Task.FromResult(NoClips);
        lock (_clips)
            _clips.Clear();
        _characterId = _characterName = "";
        _modelGeneration++;
        _rig = null;
        _set = null;
        ResetLists();
        HasRig = false;
        Summary = "";
    }

    /// <summary>The character the next model belongs to.</summary>
    public void SetCharacter(string id, string displayName)
    {
        if (id == _characterId)
            return;
        StopLine();
        StopGesture();
        _characterId = id;
        _characterName = displayName;
        LineFilter = "";
        ResetLists();
    }

    private void ResetLists()
    {
        _listsSet = null;
        _listsSkeleton = null;
        _lineMood = null;
        Moods.Clear();
        Shapes.Clear();
        Gestures.Clear();
        BoneGroups.Clear();
        Lines.Clear();
        _selectedMood = null;
        OnPropertyChanged(nameof(SelectedMood));
        Strength = 1;
        HasFaceClips = HasFaceBones = HasGestures = false;
        MouthNote = LineStatus = "";
    }

    /// <summary>Builds the rig for a (re)built model; face settings carry over outfit changes.</summary>
    public async Task SetModelAsync(AssembledModel? model)
    {
        int generation = ++_modelGeneration;
        _rig = null;
        if (model is null || !model.CanAnimate)
        {
            HasRig = false;
            EmptyText = model is null ? "" : "This model has no skeleton, so its face can't be posed.";
            return;
        }

        var index = await _index;
        if (generation != _modelGeneration)
            return;
        var prefix = FaceClipSet.MatchPrefix(index, _characterId, _characterName);
        var set = prefix is null ? null : index[prefix];
        var skeleton = model.Skeleton!;

        // The rig, the neutral mouth and one face gesture (to find every face bone) load off the UI thread.
        var (rig, bones) = await Task.Run(() =>
        {
            var r = new FaceRig(model.Geometry, skeleton);
            var driven = new HashSet<ulong>();
            if (set is not null)
            {
                foreach (var clip in new[] { set.MouthClip("AA", null), set.MouthClip(FaceClipSet.ClosedShape, null),
                             set.Moods.FirstOrDefault(m => m.ExpressionClip is not null)?.ExpressionClip,
                             set.Gestures.FirstOrDefault(g => g.Kind == FaceGestureKind.Face)?.FileName })
                {
                    if (clip is not null && LoadClip(clip) is { } anm)
                        driven.UnionWith(r.SamplePose(anm, clip).Bones.Keys);
                }
            }
            return (r, FaceBones.Find(skeleton, driven));
        });
        if (generation != _modelGeneration)
            return;

        _set = set;
        if (set != _listsSet || skeleton != _listsSkeleton)
        {
            BuildLists(set, bones, keepSettings: _listsSkeleton is not null);
            _listsSet = set;
            _listsSkeleton = skeleton;
        }

        _rig = rig;
        HasRig = true;
        foreach (var shape in Shapes)
            rig.SetMouthWeight(shape.Name, (float)shape.Value);
        foreach (var bone in BoneGroups.SelectMany(g => g.Bones).Where(b => b.IsModified))
            rig.SetBoneOffset(bone.Bone.Joint, new Vector3((float)bone.Pitch, (float)bone.Yaw, (float)bone.Roll));
        rig.ExpressionStrength = (float)Strength;
        await ApplyMoodAsync();
    }

    private void BuildLists(FaceClipSet? set, IReadOnlyList<FaceBone> bones, bool keepSettings)
    {
        var oldMood = keepSettings ? SelectedMood?.Mood?.Key : null;
        var oldWeights = keepSettings ? Shapes.ToDictionary(s => s.Name, s => s.Value) : new Dictionary<string, double>();
        var oldOffsets = keepSettings
            ? BoneGroups.SelectMany(g => g.Bones).ToDictionary(b => b.Bone.Joint, b => (b.Pitch, b.Yaw, b.Roll))
            : new Dictionary<ulong, (double, double, double)>();

        Moods.Clear();
        Shapes.Clear();
        Gestures.Clear();
        BoneGroups.Clear();
        HasFaceClips = set is not null && (set.Moods.Count > 0 || set.MouthShapes.Count > 0);
        if (set is not null)
        {
            Moods.Add(new FaceMoodViewModel(null));
            foreach (var mood in set.Moods)
                Moods.Add(new FaceMoodViewModel(mood));
            foreach (var shape in set.MouthShapes)
                Shapes.Add(new FaceShapeViewModel(shape, oldWeights.GetValueOrDefault(shape), OnShapeChanged));
            Gestures.Add(new FaceGestureViewModel(null));
            foreach (var gesture in set.Gestures)
                Gestures.Add(new FaceGestureViewModel(gesture));
        }
        HasGestures = Gestures.Count > 1;
        _selectedMood = Moods.FirstOrDefault(m => m.Mood?.Key == oldMood) ?? Moods.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedMood));
        _selectedGesture = Gestures.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedGesture));

        foreach (var group in bones.GroupBy(b => b.Group))
        {
            var groupVm = new FaceBoneGroupViewModel(FaceBones.GroupLabel(group.Key));
            foreach (var bone in group)
            {
                var vm = new FaceBoneViewModel(bone, OnBoneChanged);
                if (oldOffsets.TryGetValue(bone.Joint, out var o))
                    vm.Set(o.Item1, o.Item2, o.Item3);
                groupVm.Bones.Add(vm);
            }
            BoneGroups.Add(groupVm);
        }
        HasFaceBones = BoneGroups.Count > 0;

        Summary = set is null
            ? $"{bones.Count} face bone{(bones.Count == 1 ? "" : "s")}"
            : $"{set.Moods.Count} moods · {set.MouthShapes.Count} mouth shapes · {set.Gestures.Count} gestures · {bones.Count} face bones";
        EmptyText = HasFaceClips ? ""
            : HasFaceBones ? "This character has no facial poses in the game files. You can still pose the face bones below."
            : "This character has no facial poses in the game files, and no face bones could be identified.";
        UpdateMouthNote();
        RefreshLines();
    }

    private AnimationFile? LoadClip(string name)
    {
        lock (_clips)
            if (_clips.TryGetValue(name, out var cached))
                return cached;
        AnimationFile? clip = null;
        try
        {
            if (_read?.Invoke(name) is { } bytes && AnimationFile.TryLoad(bytes, out var loaded, out _))
                clip = loaded;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
        }
        lock (_clips)
            _clips[name] = clip;
        return clip;
    }

    // ---- State -------------------------------------------------------------

    [ObservableProperty]
    private bool _hasRig;

    [ObservableProperty]
    private bool _hasFaceClips;

    [ObservableProperty]
    private bool _hasFaceBones;

    [ObservableProperty]
    private bool _hasGestures;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _emptyText = "";

    private bool _changePending;

    // Many changes in one pass (a reset, a slider drag) draw one frame.
    private void Changed()
    {
        if (_changePending)
            return;
        _changePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _changePending = false;
            PoseChanged?.Invoke();
        });
    }

    [RelayCommand]
    private void FrameFace() => FrameFaceRequested?.Invoke(1.8f);

    /// <summary>Everything back to the body clip's own face.</summary>
    [RelayCommand]
    private void ResetAll()
    {
        StopLine();
        StopGesture();
        foreach (var shape in Shapes)
            shape.Value = 0;
        foreach (var bone in BoneGroups.SelectMany(g => g.Bones))
            bone.Reset();
        Strength = 1;
        SelectedMood = Moods.FirstOrDefault();
        _rig?.ResetToNeutral();
        Changed();
    }

    // ---- Expression --------------------------------------------------------

    public ObservableCollection<FaceMoodViewModel> Moods { get; } = new();

    private FaceMoodViewModel? _selectedMood;

    public FaceMoodViewModel? SelectedMood
    {
        get => _selectedMood;
        set
        {
            if (value is null || value == _selectedMood)
                return;
            _selectedMood = value;
            OnPropertyChanged();
            _ = ApplyMoodAsync();
        }
    }

    /// <summary>How strongly the expression shows, 0-1.</summary>
    [ObservableProperty]
    private double _strength = 1;

    partial void OnStrengthChanged(double value)
    {
        if (_rig is null)
            return;
        _rig.ExpressionStrength = (float)value;
        Changed();
    }

    [ObservableProperty]
    private string _mouthNote = "";

    [RelayCommand]
    private void Neutral() => SelectedMood = Moods.FirstOrDefault();

    private FaceMood? EffectiveMood => _lineMood ?? SelectedMood?.Mood;

    /// <summary>Loads the mood's expression and mouth shapes into the rig.</summary>
    private async Task ApplyMoodAsync()
    {
        int generation = ++_moodGeneration;
        UpdateMouthNote();
        if (_rig is not { } rig || _set is not { } set)
        {
            Changed();
            return;
        }
        var mood = EffectiveMood;
        var (expression, shapes) = await Task.Run(() =>
        {
            FacePose? face = null;
            if (mood?.ExpressionClip is { } exprName && LoadClip(exprName) is { } expr)
                face = rig.SamplePose(expr, exprName);
            // The mood's own closed mouth is part of its expression.
            if (mood?.PhonemeSuffix is { } suffix && set.MouthClip(FaceClipSet.ClosedShape, mood) is { } closedName &&
                closedName.EndsWith($"_{suffix}.anm", StringComparison.OrdinalIgnoreCase) && LoadClip(closedName) is { } closedClip)
            {
                var closed = rig.SamplePose(closedClip, closedName);
                var merged = new Dictionary<ulong, (Vector3, Quaternion)>(face?.Bones ?? new Dictionary<ulong, (Vector3, Quaternion)>());
                foreach (var (bone, local) in closed.Bones)
                    merged[bone] = local;
                face = new FacePose(merged);
            }
            var poses = new Dictionary<string, FacePose>(StringComparer.OrdinalIgnoreCase);
            foreach (var shape in set.MouthShapes.Append(FaceClipSet.ClosedShape))
                if (set.MouthClip(shape, mood) is { } name && LoadClip(name) is { } clip)
                    poses[shape] = rig.SamplePose(clip, name);
            return (face, poses);
        });
        if (generation != _moodGeneration || rig != _rig)
            return;
        rig.Expression = expression;
        rig.SetMouthShapes(shapes);
        Changed();
    }

    private void UpdateMouthNote()
    {
        var mood = EffectiveMood;
        string? fallback = _set is { HasNeutralMouth: false } s
            ? s.Moods.FirstOrDefault(m => m.PhonemeSuffix is not null && s.MouthClip("AA", null)?.EndsWith($"_{m.PhonemeSuffix}.anm", StringComparison.OrdinalIgnoreCase) == true)?.Label
            : null;
        MouthNote = _set is null || Shapes.Count == 0 ? ""
            : mood?.PhonemeSuffix is not null ? $"Mood variant: {mood.Label} mouth shapes"
            : mood is not null ? $"{mood.Label} has no mouth shapes of its own · neutral ones"
            : fallback is not null ? $"No neutral mouth shapes · using {fallback}"
            : "Neutral mouth shapes";
    }

    // ---- Mouth -------------------------------------------------------------

    public ObservableCollection<FaceShapeViewModel> Shapes { get; } = new();

    private void OnShapeChanged(FaceShapeViewModel shape)
    {
        if (_rig is null)
            return;
        _rig.SetMouthWeight(shape.Name, (float)shape.Value);
        Changed();
    }

    [RelayCommand]
    private void ClearMouth()
    {
        foreach (var shape in Shapes)
            shape.Value = 0;
    }

    // ---- Gesture -----------------------------------------------------------

    public ObservableCollection<FaceGestureViewModel> Gestures { get; } = new();

    private FaceGestureViewModel? _selectedGesture;

    public FaceGestureViewModel? SelectedGesture
    {
        get => _selectedGesture;
        set
        {
            if (value == _selectedGesture)
                return;
            _selectedGesture = value;
            OnPropertyChanged();
            if (value?.Gesture is null)
                StopGesture();
            else
                _ = PlayGestureAsync();
        }
    }

    [ObservableProperty]
    private bool _isGesturePlaying;

    [ObservableProperty]
    private bool _gestureLoop;

    [RelayCommand]
    private async Task ToggleGestureAsync()
    {
        if (IsGesturePlaying)
            StopGesture();
        else
            await PlayGestureAsync();
    }

    private async Task PlayGestureAsync()
    {
        if (_rig is not { } rig || SelectedGesture?.Gesture is not { } gesture)
            return;
        var clip = await Task.Run(() => LoadClip(gesture.FileName));
        if (rig != _rig || SelectedGesture?.Gesture != gesture)
            return;
        rig.SetGesture(clip, gesture.FileName);
        if (!rig.HasGesture)
        {
            IsGesturePlaying = false;
            return;
        }
        rig.GestureTime = 0;
        _gestureClock.Restart();
        IsGesturePlaying = true;
        _timer.Start();
    }

    private void StopGesture()
    {
        _gestureClock.Reset();
        IsGesturePlaying = false;
        if (_rig is { HasGesture: true } rig)
        {
            rig.SetGesture(null, null);
            Changed();
        }
    }

    // ---- Fine controls ------------------------------------------------------

    public ObservableCollection<FaceBoneGroupViewModel> BoneGroups { get; } = new();

    private void OnBoneChanged(FaceBoneViewModel bone)
    {
        if (_rig is null)
            return;
        _rig.SetBoneOffset(bone.Bone.Joint, new Vector3((float)bone.Pitch, (float)bone.Yaw, (float)bone.Roll));
        Changed();
    }

    // ---- Voice lines -------------------------------------------------------

    public ObservableCollection<FaceLineViewModel> Lines { get; } = new();

    [ObservableProperty]
    private string _lineFilter = "";

    partial void OnLineFilterChanged(string value) => RefreshLines();

    [ObservableProperty]
    private FaceLineViewModel? _selectedLine;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayLineCommand))]
    private bool _isLinePlaying;

    [ObservableProperty]
    private string _lineStatus = "";

    /// <summary>Lists the character's voiced lines that match the search.</summary>
    public void RefreshLines()
    {
        Lines.Clear();
        if (_catalog is null || _characterName.Length == 0)
        {
            LineStatus = _catalog is null ? "Voice lines need an open game." : "";
            return;
        }
        if (DialogueSource?.Invoke() is not { } index)
        {
            LineStatus = "Voice lines are still being read…";
            return;
        }
        var names = new[] { _characterName, FaceClipSet.IdStem(_characterId), _set?.Prefix ?? "" }
            .Select(FaceClipSet.Compact).Where(n => n.Length > 0).ToHashSet();
        var terms = LineFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int total = 0;
        foreach (var entry in index.Lines)
        {
            if (!entry.HasAudio || !names.Contains(FaceClipSet.Compact(entry.Line.Speaker)))
                continue;
            total++;
            var text = entry.Line.Text;
            if (Lines.Count < 400 && terms.All(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)))
                Lines.Add(new FaceLineViewModel(entry));
        }
        LineStatus = total == 0 ? $"No voiced lines found for {_characterName}."
            : Lines.Count == total ? $"{total:N0} voiced lines"
            : $"{Lines.Count:N0} of {total:N0} voiced lines";
    }

    partial void OnSelectedLineChanged(FaceLineViewModel? value) => PlayLineCommand.NotifyCanExecuteChanged();

    private bool CanPlayLine() => SelectedLine is not null && _audio is not null;

    /// <summary>Plays the selected line and drives the mouth from its phoneme track.</summary>
    [RelayCommand(CanExecute = nameof(CanPlayLine))]
    private async Task PlayLineAsync()
    {
        if (SelectedLine?.Entry is not { Audio: { } audio } entry || _audio is not { } source || _catalog is not { } catalog)
            return;
        StopLine();
        int generation = ++_lineGeneration;
        LineStatus = "Loading…";
        var keys = await Task.Run(() =>
        {
            var name = Path.ChangeExtension(audio.Name, ".anm");
            var file = catalog.Find(name) ?? catalog.Find(entry.Line.LipSyncFileName);
            return file is null ? Array.Empty<PhonemeKey>() : PhonemeTrack.Read(catalog.Read(file));
        });
        if (generation != _lineGeneration)
            return;

        // With no mood picked, the line plays in the mood its direction tags name.
        _lineMood = SelectedMood?.Mood is null ? _set?.FindMood(entry.Line.Directions) : null;
        if (_lineMood is not null)
            await ApplyMoodAsync();
        await Player.LoadAsync(() => source.LoadClip(audio), autoPlay: true);
        if (generation != _lineGeneration)
            return;
        if (!Player.IsPlaying)
        {
            LineStatus = Player.Error ?? "Could not play this line.";
            EndLineMood();
            return;
        }
        _rig?.SetLine(keys);
        _lineClock.Restart();
        IsLinePlaying = true;
        LineStatus = (keys.Count == 0 ? "No lip-sync track for this line" : $"{keys.Count} mouth keys") +
                     (_lineMood is null ? "" : $" · mood from the line: {_lineMood.Label}");
        _timer.Start();
    }

    [RelayCommand]
    private void StopLine()
    {
        _lineGeneration++;
        if (Player.IsPlaying)
            Player.StopCommand.Execute(null);
        _lineClock.Reset();
        bool was = IsLinePlaying || _rig?.HasLine == true;
        IsLinePlaying = false;
        _rig?.SetLine(null);
        EndLineMood();
        if (was)
            Changed();
    }

    private void EndLineMood()
    {
        if (_lineMood is null)
            return;
        _lineMood = null;
        _ = ApplyMoodAsync();
    }

    // ---- Playback clock ------------------------------------------------------

    private void Tick()
    {
        bool animating = false;
        if (_rig is { } rig)
        {
            if (IsGesturePlaying && rig.HasGesture)
            {
                float t = (float)_gestureClock.Elapsed.TotalSeconds;
                if (t >= rig.GestureLength)
                {
                    if (GestureLoop && rig.GestureLength > 0)
                    {
                        t %= rig.GestureLength;
                    }
                    else
                    {
                        StopGesture();
                        t = -1;
                    }
                }
                if (t >= 0)
                {
                    rig.GestureTime = t;
                    animating = true;
                }
            }
            if (IsLinePlaying)
            {
                rig.LineTime = (float)_lineClock.Elapsed.TotalSeconds;
                animating = true;
            }
        }
        if (!animating)
            _timer.Stop();
        Changed();
    }
}

/// <summary>A mood chip (null mood = neutral).</summary>
public sealed class FaceMoodViewModel
{
    public FaceMoodViewModel(FaceMood? mood) => Mood = mood;

    public FaceMood? Mood { get; }

    public string Label => Mood?.Label ?? "Neutral";

    public string Tip => Mood is null ? "The body clip's own face"
        : Mood.ExpressionClip is not null && Mood.PhonemeSuffix is not null ? $"{Mood.Key}: expression and mouth shapes"
        : Mood.ExpressionClip is not null ? $"{Mood.Key}: expression"
        : $"{Mood.Key}: mouth shapes";
}

/// <summary>One mouth shape slider.</summary>
public sealed partial class FaceShapeViewModel : ViewModelBase
{
    private readonly Action<FaceShapeViewModel> _changed;

    public FaceShapeViewModel(string name, double value, Action<FaceShapeViewModel> changed)
    {
        Name = name;
        _value = value;
        _changed = changed;
    }

    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    private double _value;

    public string ValueText => $"{Value:0.00}";

    partial void OnValueChanged(double value) => _changed(this);
}

/// <summary>A gesture in the picker (null = none).</summary>
public sealed class FaceGestureViewModel
{
    public FaceGestureViewModel(FaceGesture? gesture) => Gesture = gesture;

    public FaceGesture? Gesture { get; }

    public string Label => Gesture is null ? "No gesture" : $"{Gesture.Kind} · {Gesture.Label}";

    public string Tip => Gesture is null ? "" : Gesture.FileName + (Gesture.IsAdditive ? " (additive)" : "");
}

/// <summary>A group of face bones in the fine controls.</summary>
public sealed partial class FaceBoneGroupViewModel : ViewModelBase
{
    public FaceBoneGroupViewModel(string label) => Label = label;

    public string Label { get; }

    public ObservableCollection<FaceBoneViewModel> Bones { get; } = new();

    public string CountText => $"{Bones.Count} bone{(Bones.Count == 1 ? "" : "s")} · ±30°";

    [RelayCommand]
    private void Reset()
    {
        foreach (var bone in Bones)
            bone.Reset();
    }
}

/// <summary>One face bone's rotation offsets (degrees about its own axes).</summary>
public sealed partial class FaceBoneViewModel : ViewModelBase
{
    private readonly Action<FaceBoneViewModel> _changed;
    private bool _batch;

    public FaceBoneViewModel(FaceBone bone, Action<FaceBoneViewModel> changed)
    {
        Bone = bone;
        _changed = changed;
    }

    public FaceBone Bone { get; }

    public string Label => Bone.Label;

    public string Tip => Bone.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private double _pitch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private double _yaw;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private double _roll;

    public bool IsModified => Pitch != 0 || Yaw != 0 || Roll != 0;

    partial void OnPitchChanged(double value) => Notify();
    partial void OnYawChanged(double value) => Notify();
    partial void OnRollChanged(double value) => Notify();

    private void Notify()
    {
        if (!_batch)
            _changed(this);
    }

    /// <summary>Sets all three offsets with one change notification.</summary>
    public void Set(double pitch, double yaw, double roll)
    {
        _batch = true;
        (Pitch, Yaw, Roll) = (pitch, yaw, roll);
        _batch = false;
    }

    [RelayCommand]
    public void Reset()
    {
        Set(0, 0, 0);
        _changed(this);
    }
}

/// <summary>A voiced line of the character.</summary>
public sealed class FaceLineViewModel
{
    public FaceLineViewModel(DialogueEntry entry) => Entry = entry;

    public DialogueEntry Entry { get; }

    public string Text => Entry.Line.Text;

    public string Meta => $"{Entry.Episode} · {Entry.Scene}";
}
