using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using TelltaleModding.App.Services;
using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Dialogue;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.App.ViewModels;

/// <summary>
/// The speaker of the dialogue line being played, shown in 3D and lip-synced to the
/// recording: the line's phoneme keys drive the character's mouth poses (in the mood
/// its direction tags name), over the character's idle. With <see cref="PlayPerformance"/>
/// on, the body language the game's chore plays with the line (turns, poses, gestures,
/// blinks) plays too.
/// </summary>
public sealed partial class SpeakerViewModel : ViewModelBase
{
    private readonly Dictionary<string, Task<SpeakerModel?>> _models = new(StringComparer.OrdinalIgnoreCase);

    // Where each scene's lines play; a scene's dialog and chores are read once, in the background.
    private readonly Dictionary<string, Task<SceneChoreLines>> _choreLines = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxIndexedScenes = 6;
    private SceneCharacters? _sceneCharacters;
    private IReadOnlyList<ArchiveEntryRef> _chores = Array.Empty<ArchiveEntryRef>();
    private readonly SizeBoundedCache<string, (AnimationFile? Clip, long Bytes)> _clips =
        new(64L * 1024 * 1024, c => c.Bytes + 512, StringComparer.OrdinalIgnoreCase);
    private GameCatalog? _catalog;
    private ModelAssembler? _assembler;
    private IReadOnlyDictionary<string, IReadOnlyList<string?>> _phonemeSets = new Dictionary<string, IReadOnlyList<string?>>();
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

    /// <summary>Raised with each rendered frame, while <see cref="FollowCamera"/> is on, with what the camera should hold.</summary>
    public event Action<SpeakerShot>? ShotReady;

    /// <summary>The camera follows the speaker through the line (see <see cref="SpeakerCamera"/>).</summary>
    [ObservableProperty]
    private bool _followCamera = true;

    /// <summary>
    /// Raised with each rendered frame, while <see cref="FollowCamera"/> and <see cref="UseGameCamera"/>
    /// are on and the line has one, with the camera the game shows it from.
    /// </summary>
    public event Action<CameraView>? GameViewReady;

    /// <summary>Shows a line from the game's own camera when its chore has one that frames the speaker (see <see cref="GameCamera"/>).</summary>
    [ObservableProperty]
    private bool _useGameCamera = true;

    private SpeakerCamera? _camera;
    private GameCamera? _gameCamera;

    [ObservableProperty]
    private bool _hasModel;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _caption = "";

    /// <summary>Plays the line's performance from the game's chore, when one is found.</summary>
    public bool PlayPerformance { get; set; } = true;

    public void Load(GameCatalog catalog, ModelAssembler? models = null)
    {
        _catalog = catalog;
        _assembler = models ?? new ModelAssembler(new CatalogAssetResolver(catalog));
        _models.Clear();
        _choreLines.Clear();
        _sceneCharacters = new SceneCharacters(catalog);
        _clips.Clear();
        _chores = catalog.Entries.Where(e => e.Name.EndsWith(".chore", StringComparison.OrdinalIgnoreCase)).ToList();
        _phonemeSets = PhonemePoses.Sets(catalog.Entries.Select(e => e.Name));
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
        // The chore that plays the line, and the scene it is staged in, name the speaker's
        // exact model (Clementine 402 in the barn, not whichever Clementine fits the name).
        var lines = await ChoreLinesFor(line.Scene);
        if (generation != _generation)
            return;
        var choreLine = lines.Find(line);
        var staged = await Task.Run(() => StagedCharacter(lines, choreLine, line.Line.Speaker));
        if (generation != _generation)
            return;
        var candidates = FindCharacters(line.Line.Speaker, line.Episode);
        if (staged is not null && candidates.Count > 0 && candidates.All(c => c.Id != staged.Id))
        {
            // The chore voices the line from someone else (Javier talking over a zombie he
            // looks at): show the speaker the scene places by name, without that performance.
            choreLine = null;
            staged = await Task.Run(() => StagedCharacter(lines, null, line.Line.Speaker));
            if (generation != _generation)
                return;
            if (staged is not null && candidates.All(c => c.Id != staged.Id))
                staged = null;
        }
        if (staged is not null)
            candidates = candidates.Where(c => c.Id != staged.Id).Prepend(staged).ToList();
        var character = candidates.FirstOrDefault();
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
            // A model whose textures the game lacks comes out blank; a later candidate may have them.
            model = null;
            foreach (var candidate in candidates.Take(MaxModelTries))
            {
                if (!_models.TryGetValue(candidate.Id, out var task))
                    _models[candidate.Id] = task = Task.Run(() => BuildModel(candidate));
                var built = await task;
                if (generation != _generation)
                    return;
                if (built is null)
                    continue;
                if (model is null || IsTextured(built.Model))
                {
                    model = built;
                    character = candidate;
                }
                if (IsTextured(built.Model))
                    break;
            }
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

            var mood = model.Prefix is null ? null : FindMood(model, line.Line.Directions);
            ChorePerformance? performance = null;
            if (PlayPerformance && model.Model.CanAnimate && choreLine is not null)
                performance = await Task.Run(() => BuildPerformance(model, choreLine));
            var player = await Task.Run(() => BuildLipSync(model, mood, performance, choreLine?.Start ?? 0));
            if (generation != _generation)
                return;
            player?.SetLine(keys);
            float duration = choreLine is { } span ? span.End - span.Start : keys.Count > 0 ? keys[^1].Time + 0.5f : 3f;
            var camera = player is null ? null : await Task.Run(() => SpeakerCamera.Build(player.Skeleton, player.Pose, duration));
            var gameCamera = camera is null || choreLine is not { Chore.SceneFile: { Length: > 0 } sceneFile } || _sceneCharacters is not { } scenes ? null
                : await Task.Run(() => scenes.Scene(sceneFile) is { } scene
                    ? GameCamera.Find(choreLine.Chore, scene, choreLine.Agent, choreLine.Start, choreLine.End, camera.At(duration / 2).Target,
                        player!.RootMotion, PlayedChore, CameraClip)
                    : null);
            if (generation != _generation)
                return;
            _camera = camera;
            _gameCamera = gameCamera;
            _lipSync = player;
            bool frame = _shownCharacter != character.Id;
            _shownCharacter = character.Id;
            HasModel = true;
            ModelReady?.Invoke(model.Model, frame);
            if (frame && player is not null && await Task.Run(() => PosedHead(player)) is { } head && generation == _generation)
                FaceFramingReady?.Invoke(head.Top, head.Height);
            Caption = player is null
                ? $"{character} · no lip-sync poses"
                : $"{character}{(mood is null ? "" : $" · {mood}")}{(keys.Count == 0 ? " · no lip-sync for this line" : "")}" +
                  (!PlayPerformance ? "" : performance is { ClipCount: > 0 } ? " · game performance" : " · no game performance for this line") +
                  (UseGameCamera && gameCamera is not null ? " · game camera" : "");
            Render(0);
        }
        finally
        {
            if (generation == _generation)
                IsLoading = false;
        }
    }

    /// <summary>The point the follow camera looks at <paramref name="seconds"/> into the line (the face), or null.</summary>
    public Vector3? FollowTarget(double seconds) => _camera?.At((float)seconds).Target;

    /// <summary>Poses the speaker at <paramref name="seconds"/> into the line.</summary>
    public void Render(double seconds)
    {
        if (_lipSync is not { } player)
            return;
        PoseReady?.Invoke(player.SkinMatrices((float)seconds));
        if (!FollowCamera)
            return;
        // The game's shot while it shows the speaker's face; the follow camera while it cuts away.
        if (_camera is not { } camera)
            return;
        var shot = camera.At((float)seconds);
        if (UseGameCamera && _gameCamera is { } game && game.ShowsFace(shot.Target, shot.Yaw, (float)seconds))
            GameViewReady?.Invoke(game.At((float)seconds));
        else
            ShotReady?.Invoke(shot);
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

    private const int MaxModelTries = 3;

    // The character the scene places under the line's speaker: the chore's own scene first,
    // then the scenes the line's other chores are staged in.
    private CharacterEntry? StagedCharacter(SceneChoreLines lines, ChoreLine? choreLine, string speaker)
    {
        if (_sceneCharacters is not { } scenes)
            return null;
        var agent = choreLine?.Agent ?? speaker;
        var files = new List<string>();
        if (choreLine?.Chore.SceneFile is { Length: > 0 } own)
            files.Add(own);
        files.AddRange(lines.SceneFiles);
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase).Take(4))
            if ((scenes.Find(file, agent) ?? (agent == speaker ? null : scenes.Find(file, speaker))) is { } character)
                return character;
        return null;
    }

    private static bool IsTextured(AssembledModel model) => model.BatchTextures.Any(t => t is not null);

    // The speaker's characters, best first, for when no scene names the model: names compared
    // without spaces or case ("LADYFORRESTER"), preferring one with mouth poses, then the
    // variant made for the line's episode ("AI 102" for a line from Borderlands102), then one
    // from the line's season (by its variant or the episode its skeleton ships in), then the
    // plain variant, then the fullest outfit. Subtitles often use a short name ("JACK" for Handsome
    // Jack), so when nothing matches in full, a character whose name ends with the
    // speaker's stands in.
    private IReadOnlyList<CharacterEntry> FindCharacters(string speaker, string episode)
    {
        if (_catalog is null || speaker.Length == 0)
            return Array.Empty<CharacterEntry>();
        var key = Normalize(speaker);
        var models = _catalog.Characters.Where(c => c.Skeleton is not null && c.Parts.Count > 0).ToList();
        var matches = models
            .Where(c => Normalize(c.DisplayName) == key || Normalize(IdStem(c.Id)) == key)
            .ToList();
        if (matches.Count == 0 && key.Length >= 3)
            matches = models
                .Where(c => Normalize(c.DisplayName).EndsWith(key) || Normalize(IdStem(c.Id)).EndsWith(key))
                .ToList();
        return matches
            .OrderByDescending(c => PhonemePrefixFor(c) is not null)
            .ThenByDescending(c => c.Variant is { } v && EpisodeNumber().Matches(v).Any(m => episode.Contains(m.Value)))
            .ThenByDescending(c => Season(episode) is { } season &&
                                   (Season(c.Variant) == season ||
                                    Season(c.Skeleton is { } skl ? DialogueIndex.EpisodeOf(skl.ArchivePath) : null) == season))
            .ThenBy(c => c.Variant is null ? 0 : 1)
            .ThenByDescending(c => c.Parts.Count(p => p.DefaultOn))
            .ToList();
    }

    // The character's mouth pose prefix: its own ("rhys"), or one named for it ("handsomeJack"
    // for a "Handsome Jack AI 102" variant).
    private string? PhonemePrefixFor(CharacterEntry character) => PhonemePrefixesFor(character).FirstOrDefault();

    // Every prefix that could hold the character's mouth poses, best first: its own, then those
    // named for it, the ones made for its season first ("clementine400" for Clementine 402),
    // then ones without a season, then the rest.
    private IEnumerable<string> PhonemePrefixesFor(CharacterEntry character)
    {
        var stem = IdStem(character.Id);
        if (_phonemeSets.ContainsKey(stem))
            yield return stem;
        var name = Normalize(character.DisplayName);
        var season = Season(character.Variant);
        foreach (var prefix in _phonemeSets.Keys
                     .Where(p => !p.Equals(stem, StringComparison.OrdinalIgnoreCase) && Normalize(p).StartsWith(name))
                     .OrderBy(p => Season(p) is not { } s ? 1 : s == season ? 0 : 2)
                     .ThenBy(p => p.Length)
                     .ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
            yield return prefix;
    }

    // The season digit of an episode number: '4' for WalkingDead404 or a "402" variant.
    private static char? Season(string? text) =>
        text is not null && EpisodeNumber().Match(text) is { Success: true } m ? m.Value[0] : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"\d{3}")]
    private static partial System.Text.RegularExpressions.Regex EpisodeNumber();

    private static string Normalize(string name) => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // "sk54_rhys" → "rhys"
    private static string IdStem(string id)
    {
        int underscore = id.IndexOf('_');
        return underscore > 0 && id.StartsWith("sk", StringComparison.OrdinalIgnoreCase) ? id[(underscore + 1)..] : id;
    }

    // Prefix and BaseMood name the set of mouth poses that fits the model's face rig (BaseMood
    // null for the neutral set).
    private sealed record SpeakerModel(AssembledModel Model, string? Prefix, (AnimationFile Clip, string Name)? Idle,
        AnimationFile? Face = null, string? BaseMood = null);

    // What a character's .prop names: its idle, its resting face and the prefix of its
    // phoneme table's mouth poses ("clementine" for clementine_phoneme_AA.anm).
    private sealed record PropReferenceSet(string? Idle, AnimationFile? Face, string? PhonemePrefix);

    private SpeakerModel? BuildModel(CharacterEntry character)
    {
        var catalog = _catalog!;
        AssembledModel model;
        model = _assembler!.Build(CharactersViewModel.DefaultOutfitMeshes(character).ToList(), character.Skeleton?.Name);
        if (!model.CanAnimate)
            return new SpeakerModel(model, null, null);

        // The character's .prop names its idle, resting face and phoneme table; the guesses
        // below are for characters whose .prop leaves them out.
        var named = PropReferencesFor(character);
        // The phoneme table's prefix first, then the ones named for the character, of those
        // whose clips move this model's face.
        var joints = model.Skeleton!.JointSymbols.ToHashSet();
        var prefixes = named.PhonemePrefix is { } own ? PhonemePrefixesFor(character).Prepend(own) : PhonemePrefixesFor(character);
        var set = PhonemePoses.ChooseSet(prefixes.Distinct(StringComparer.OrdinalIgnoreCase), _phonemeSets, clip => BonesDriven(clip, joints))
                  ?? PhonemePoses.ChooseSet(SharedPrefixes(joints), _phonemeSets, clip => BonesDriven(clip, joints));
        var prefix = set?.Prefix;
        var baseMood = set?.Mood;
        (AnimationFile, string)? idle = null;
        if (named.Idle is { } idleName && catalog.Find(idleName) is { } idleEntry &&
            AnimationFile.TryLoad(catalog.Read(idleEntry), out var namedIdle, out _) && namedIdle!.IsSkeletal)
            idle = (namedIdle, idleName);
        if (idle is not null)
            return new SpeakerModel(model, prefix, idle, named.Face, baseMood);

        // A standing idle keeps the body out of its bind pose. Many idles sit, kneel or
        // crouch, which would drop the head below the face-framed camera: keep the first
        // (preferring "stand" in the name) whose top stays near the rest pose's height.
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
        return new SpeakerModel(model, prefix, idle, named.Face, baseMood);
    }

    // For characters with no mouth poses of their own (Mariana, Lonnie), as the game does: the
    // generic sets ("wd400GM") first, then any set, of those whose first set fits the face rig.
    private IEnumerable<string> SharedPrefixes(IReadOnlySet<ulong> joints) =>
        _phonemeSets
            .Where(s => BonesDriven(PhonemePoses.ProbeClip(s.Key, s.Value[0]), joints) > 0)
            .Select(s => s.Key)
            .OrderBy(p => p.Contains("GM", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(p => p.Length)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase);

    // How many of the skeleton's bones a pose clip drives (0 when the clip is missing).
    private int BonesDriven(string clipName, IReadOnlySet<ulong> joints) =>
        Clip(clipName) is { } clip ? clip.FindBoneSymbols(joints).Count : 0;

    private AnimationFile? Clip(string name) =>
        _clips.GetOrAdd(name, n =>
        {
            var entry = _catalog!.Find(n);
            if (entry is null)
                return (null, 0);
            var bytes = _catalog.Read(entry);
            return (AnimationFile.TryLoad(bytes, out var loaded, out _) && loaded!.IsSkeletal ? loaded : null, bytes.Length);
        }).Clip;

    // A chore a line's chore plays (a cutscene's pieces), by its symbol.
    private ChoreFile? PlayedChore(ulong symbol) =>
        _catalog?.FindBySymbol(symbol) is { } entry && ChoreFile.TryRead(_catalog.Read(entry), out var chore, out _) ? chore : null;

    // A clip a camera plays (handheld drift), by its symbol.
    private AnimationFile? CameraClip(ulong symbol) =>
        _catalog?.FindBySymbol(symbol) is { } entry && AnimationFile.TryLoad(_catalog.Read(entry), out var clip, out _) ? clip : null;

    private PropReferenceSet PropReferencesFor(CharacterEntry character)
    {
        var catalog = _catalog!;
        string? NameOf(ulong symbol) => catalog.FindBySymbol(symbol)?.Name;
        byte[]? Read(string name) => catalog.Find(name) is { } entry ? catalog.Read(entry) : null;
        try
        {
            if (Read(character.Id + ".prop") is not { } prop)
                return new PropReferenceSet(null, null, null);
            var idle = MainAnimation(PropertyReferences.Find(prop, PropertyReferences.IdleAnimation, NameOf, Read, ".anm", ".chore"));
            AnimationFile? face = null;
            if (MainAnimation(PropertyReferences.Find(prop, PropertyReferences.IdleFace, NameOf, Read, ".chore", ".anm")) is { } faceName &&
                Read(faceName) is { } faceBytes && AnimationFile.TryLoad(faceBytes, out var faceClip, out _))
                face = faceClip;
            string? prefix = null;
            if (PropertyReferences.Find(prop, PropertyReferences.PhonemeTable, NameOf, Read, ".ptable") is { } tableName &&
                Read(tableName) is { } table)
                prefix = PropertyReferences.PhonemeClips(table, PhonemePoses.Phonemes, NameOf).Values
                    .Select(PhonemeClipPrefix).FirstOrDefault(p => p is not null);
            return new PropReferenceSet(idle, face, prefix);

            // An animation, or the one a chore plays throughout: its longest-playing animation
            // that is not layered on top (an idle chore adds breaths and glances over its stand).
            string? MainAnimation(string? name)
            {
                if (name is null || name.EndsWith(".anm", StringComparison.OrdinalIgnoreCase))
                    return name;
                if (Read(name) is not { } bytes || !ChoreFile.TryRead(bytes, out var chore, out _))
                    return null;
                return chore!.Resources
                    .Where(r => r.Type == ChoreFile.AnimationType && r.Enabled)
                    .Select(r => (Name: NameOf(r.Name), Span: r.Blocks.Sum(b => b.End - b.Start)))
                    .Where(x => x.Name is not null &&
                                !Path.GetFileNameWithoutExtension(x.Name).EndsWith("_add", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.Span)
                    .Select(x => x.Name)
                    .FirstOrDefault();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return new PropReferenceSet(null, null, null);
        }
    }

    // "clementine_phoneme_AA.anm" -> "clementine"
    private static string? PhonemeClipPrefix(string clip)
    {
        int at = clip.IndexOf("_phoneme_", StringComparison.OrdinalIgnoreCase);
        return at > 0 ? clip[..at] : null;
    }

    // The first direction tag the character has mouth poses for ("smugA") that fit its face rig.
    private string? FindMood(SpeakerModel speaker, IReadOnlyList<string> directions)
    {
        var joints = speaker.Model.Skeleton!.JointSymbols.ToHashSet();
        return directions.FirstOrDefault(d => d.All(char.IsLetterOrDigit) &&
                                              BonesDriven(PhonemePoses.ProbeClip(speaker.Prefix!, d), joints) > 0);
    }

    private LipSyncPlayer? BuildLipSync(SpeakerModel speaker, string? mood, ChorePerformance? performance = null, float performanceStart = 0)
    {
        var model = speaker.Model;
        if (!model.CanAnimate)
            return null;
        var catalog = _catalog!;
        var poses = new Dictionary<ulong, AnimationFile>();
        if (speaker.Prefix is { } prefix)
        {
            foreach (var (phoneme, name) in PhonemePoses.ClipNames(prefix, mood, n => catalog.Find(n) is not null, speaker.BaseMood))
            {
                if (AnimationFile.TryLoad(catalog.Read(catalog.Find(name)!), out var clip, out _))
                    poses[phoneme] = clip!;
            }
        }
        // The mood the line's directions name, otherwise the resting face the character's .prop names.
        AnimationFile? expression = speaker.Face;
        if (speaker.Prefix is { } p && mood is not null && catalog.Find($"{p}_phonemes_{mood}.anm") is { } face &&
            AnimationFile.TryLoad(catalog.Read(face), out var faceClip, out _))
            expression = faceClip;
        return new LipSyncPlayer(model.Geometry, model.Skeleton!, poses, expression, speaker.Idle, performance, performanceStart);
    }

    private Task<SceneChoreLines> ChoreLinesFor(string scene)
    {
        if (!_choreLines.TryGetValue(scene, out var task))
        {
            if (_choreLines.Count >= MaxIndexedScenes)
                _choreLines.Clear();
            var catalog = _catalog!;
            var chores = _chores;
            _choreLines[scene] = task = Task.Run(() => SceneChoreLines.Build(catalog, chores, scene));
        }
        return task;
    }

    // The speaker's clips in the line's chore, loaded and placed on its timeline.
    private ChorePerformance? BuildPerformance(SpeakerModel speaker, ChoreLine line)
    {
        var catalog = _catalog!;
        var specs = ChorePerformance.Collect(line.Chore, line.Agent,
            symbol => catalog.FindBySymbol(symbol)?.Name,
            name => catalog.Find(name) is { } entry && ChoreFile.TryRead(catalog.Read(entry), out var nested, out _) ? nested : null);
        var clips = new List<(PerformanceClip, AnimationFile)>();
        foreach (var spec in specs)
        {
            if (Clip(spec.Name) is { } clip)
                clips.Add((spec, clip));
        }
        return clips.Count == 0 ? null : new ChorePerformance(speaker.Model.Geometry, speaker.Model.Skeleton!, clips);
    }

    // The line's phoneme track sits beside its recording (<id>.anm next to <id>.wav).
    private IReadOnlyList<PhonemeKey> ReadKeys(DialogueEntry line)
    {
        var name = line.Audio is { } audio ? Path.ChangeExtension(audio.Name, ".anm") : line.Line.LipSyncFileName;
        var entry = _catalog!.Find(name);
        return entry is null ? Array.Empty<PhonemeKey>() : PhonemeTrack.Read(_catalog.Read(entry));
    }
}
