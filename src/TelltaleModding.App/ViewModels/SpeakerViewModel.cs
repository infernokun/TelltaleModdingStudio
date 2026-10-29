using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using TelltaleModding.App.Services;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Dialogue;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.App.ViewModels;

/// <summary>
/// The speaker of the dialogue line being played, shown in 3D and lip-synced to the
/// recording: the line's phoneme keys drive the character's mouth poses (in the mood
/// its direction tags name), over the character's idle.
/// </summary>
public sealed partial class SpeakerViewModel : ViewModelBase
{
    private readonly Dictionary<string, Task<SpeakerModel?>> _models = new(StringComparer.OrdinalIgnoreCase);
    private GameCatalog? _catalog;
    private ModelAssembler? _assembler;
    private IReadOnlySet<string> _phonemePrefixes = new HashSet<string>();
    private LipSyncPlayer? _lipSync;
    private string? _shownCharacter;
    private int _generation;

    /// <summary>Raised with a model to show; true when the camera should frame it afresh.</summary>
    public event Action<AssembledModel, bool>? ModelReady;

    public event Action? ModelCleared;

    /// <summary>Raised with the posed vertices of each frame.</summary>
    public event Action<Matrix4x4[]?>? PoseReady;

    /// <summary>Raised for a newly shown speaker with the posed top of the head and the body's height, to frame the face.</summary>
    public event Action<Vector3, float>? FaceFramingReady;

    [ObservableProperty]
    private bool _hasModel;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _caption = "";

    public void Load(GameCatalog catalog)
    {
        _catalog = catalog;
        _assembler = new ModelAssembler(new CatalogAssetResolver(catalog));
        _models.Clear();
        _phonemePrefixes = PhonemePoses.Prefixes(catalog.Entries.Select(e => e.Name));
        Clear();
    }

    public void Clear()
    {
        _generation++;
        _lipSync = null;
        _shownCharacter = null;
        HasModel = false;
        IsLoading = false;
        Caption = "";
        ModelCleared?.Invoke();
    }

    /// <summary>Shows <paramref name="line"/>'s speaker, ready to lip-sync it.</summary>
    public async Task ShowAsync(DialogueEntry line)
    {
        if (_catalog is null || _assembler is null)
            return;
        int generation = ++_generation;
        var character = FindCharacter(line.Line.Speaker);
        if (character is null)
        {
            _lipSync = null;
            _shownCharacter = null;
            HasModel = false;
            Caption = line.Line.Speaker.Length == 0 ? "" : $"No 3D model found for {DialogueLineViewModel.TitleCase(line.Line.Speaker)}";
            ModelCleared?.Invoke();
            return;
        }

        IsLoading = character.Id != _shownCharacter;
        SpeakerModel? model;
        try
        {
            if (!_models.TryGetValue(character.Id, out var task))
                _models[character.Id] = task = Task.Run(() => BuildModel(character));
            model = await task;
            var keys = await Task.Run(() => ReadKeys(line));
            if (generation != _generation)
                return;
            if (model is null)
            {
                HasModel = false;
                Caption = $"Could not build {character}";
                ModelCleared?.Invoke();
                return;
            }

            var mood = model.Prefix is null ? null : FindMood(model.Prefix, line.Line.Directions);
            var player = await Task.Run(() => BuildLipSync(model, mood));
            if (generation != _generation)
                return;
            player?.SetLine(keys);
            _lipSync = player;
            bool frame = _shownCharacter != character.Id;
            _shownCharacter = character.Id;
            HasModel = true;
            ModelReady?.Invoke(model.Model, frame);
            if (frame && player is not null && await Task.Run(() => PosedHead(player)) is { } head && generation == _generation)
                FaceFramingReady?.Invoke(head.Top, head.Height);
            Caption = player is null
                ? $"{character} · no lip-sync poses"
                : $"{character}{(mood is null ? "" : $" · {mood}")}{(keys.Count == 0 ? " · no lip-sync for this line" : "")}";
            Render(0);
        }
        finally
        {
            if (generation == _generation)
                IsLoading = false;
        }
    }

    /// <summary>Poses the speaker at <paramref name="seconds"/> into the line.</summary>
    public void Render(double seconds)
    {
        if (_lipSync is not { } player)
            return;
        PoseReady?.Invoke(player.SkinMatrices((float)seconds));
    }

    // Where the head is in the first posed frame: the top of the posed body, centred on
    // the vertices in its top eighth (the head, whatever the arms are doing).
    private static (Vector3 Top, float Height)? PosedHead(LipSyncPlayer player)
    {
        int count = player.Geometry.Positions.Length;
        if (count == 0)
            return null;
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        player.Evaluate(0, positions, normals);
        float top = positions.Max(p => p.Y), bottom = positions.Min(p => p.Y), height = top - bottom;
        if (height <= 0)
            return null;
        var head = positions.Where(p => p.Y >= top - height * 0.125f).ToList();
        return (new Vector3(head.Average(p => p.X), top, head.Average(p => p.Z)), height);
    }

    // The speaker's character: names compared without spaces or case ("LADYFORRESTER"),
    // preferring the plain variant, then one with mouth poses, then the fullest outfit.
    private CharacterEntry? FindCharacter(string speaker)
    {
        if (_catalog is null || speaker.Length == 0)
            return null;
        var key = Normalize(speaker);
        return _catalog.Characters
            .Where(c => c.Skeleton is not null && c.Parts.Count > 0 &&
                        (Normalize(c.DisplayName) == key || Normalize(IdStem(c.Id)) == key))
            .OrderByDescending(c => _phonemePrefixes.Contains(IdStem(c.Id)))
            .ThenBy(c => c.Variant is null ? 0 : 1)
            .ThenByDescending(c => c.Parts.Count(p => p.DefaultOn))
            .FirstOrDefault();
    }

    private static string Normalize(string name) => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // "sk54_rhys" → "rhys"
    private static string IdStem(string id)
    {
        int underscore = id.IndexOf('_');
        return underscore > 0 && id.StartsWith("sk", StringComparison.OrdinalIgnoreCase) ? id[(underscore + 1)..] : id;
    }

    private sealed record SpeakerModel(AssembledModel Model, string? Prefix, (AnimationFile Clip, string Name)? Idle);

    private SpeakerModel? BuildModel(CharacterEntry character)
    {
        var catalog = _catalog!;
        AssembledModel model;
        lock (_assembler!)
            model = _assembler.Build(CharactersViewModel.DefaultOutfitMeshes(character).ToList(), character.Skeleton?.Name);
        if (!model.CanAnimate)
            return new SpeakerModel(model, null, null);

        var stem = IdStem(character.Id);
        var prefix = _phonemePrefixes.Contains(stem) ? stem
            : _phonemePrefixes.FirstOrDefault(p => Normalize(p).StartsWith(Normalize(character.DisplayName)));

        // A standing idle keeps the body out of its bind pose. Many idles sit, kneel or
        // crouch, which would drop the head below the face-framed camera: keep the first
        // (preferring "stand" in the name) whose top stays near the rest pose's height.
        (AnimationFile, string)? idle = null;
        var candidates = catalog.AnimationsFor(character)
            .Where(a => a.NamedForCharacter && a.File.Name.Contains("idle", StringComparison.OrdinalIgnoreCase) &&
                        !a.File.Name.Contains("_add", StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.File.Name.Contains("stand", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(a => a.File.Name.Length)
            .Take(12);
        float restTop = model.Geometry.Positions.Length > 0 ? model.Geometry.Positions.Max(p => p.Y) : 0f;
        var posed = new Vector3[model.Geometry.Positions.Length];
        var normals = new Vector3[posed.Length];
        foreach (var candidate in candidates)
        {
            if (!AnimationFile.TryLoad(catalog.Read(candidate.File), out var clip, out _) || !clip!.IsSkeletal)
                continue;
            var player = new AnimationPlayer(model.Geometry, model.Skeleton!);
            player.Load(clip, candidate.File.Name);
            player.Evaluate(0, posed, normals);
            if (posed.Length > 0 && posed.Max(p => p.Y) >= restTop * 0.92f)
            {
                idle = (clip, candidate.File.Name);
                break;
            }
        }
        return new SpeakerModel(model, prefix, idle);
    }

    // The first direction tag the character has mouth poses for ("smugA").
    private string? FindMood(string prefix, IReadOnlyList<string> directions) =>
        directions.FirstOrDefault(d => d.All(char.IsLetterOrDigit) &&
                                       _catalog!.Find($"{prefix}_phoneme_AA_{d}.anm") is not null);

    private LipSyncPlayer? BuildLipSync(SpeakerModel speaker, string? mood)
    {
        var model = speaker.Model;
        if (!model.CanAnimate)
            return null;
        var catalog = _catalog!;
        var poses = new Dictionary<ulong, AnimationFile>();
        if (speaker.Prefix is { } prefix)
        {
            foreach (var (phoneme, name) in PhonemePoses.ClipNames(prefix, mood, n => catalog.Find(n) is not null))
            {
                if (AnimationFile.TryLoad(catalog.Read(catalog.Find(name)!), out var clip, out _))
                    poses[phoneme] = clip!;
            }
        }
        AnimationFile? expression = null;
        if (speaker.Prefix is { } p && mood is not null && catalog.Find($"{p}_phonemes_{mood}.anm") is { } face &&
            AnimationFile.TryLoad(catalog.Read(face), out var faceClip, out _))
            expression = faceClip;
        return new LipSyncPlayer(model.Geometry, model.Skeleton!, poses, expression, speaker.Idle);
    }

    // The line's phoneme track sits beside its recording (<id>.anm next to <id>.wav).
    private IReadOnlyList<PhonemeKey> ReadKeys(DialogueEntry line)
    {
        var name = line.Audio is { } audio ? Path.ChangeExtension(audio.Name, ".anm") : line.Line.LipSyncFileName;
        var entry = _catalog!.Find(name);
        return entry is null ? Array.Empty<PhonemeKey>() : PhonemeTrack.Read(_catalog.Read(entry));
    }
}
