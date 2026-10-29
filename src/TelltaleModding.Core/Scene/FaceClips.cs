using System.Text;
using System.Text.RegularExpressions;

namespace TelltaleModding.Core.Scene;

/// <summary>
/// A mood a character's face can take: an expression clip (<c>rhys_phonemes_smugA.anm</c> or
/// <c>rhys_face_moodSmugA.anm</c>) and/or mouth shapes made for it (<c>rhys_phoneme_AA_smugA.anm</c>).
/// </summary>
/// <param name="Key">The mood's name as the games use it in direction tags (<c>smugA</c>).</param>
/// <param name="ExpressionClip">The whole-face expression clip, when there is one.</param>
/// <param name="PhonemeSuffix">The suffix of this mood's mouth shape clips, when there are any.</param>
public sealed record FaceMood(string Key, string Label, string? ExpressionClip, string? PhonemeSuffix);

/// <summary>What a gesture clip moves.</summary>
public enum FaceGestureKind { Head, Face, Eyes }

/// <summary>
/// A short head, face or eye gesture (<c>rhys_headGesture_nodYes_add.anm</c>); additive
/// clips (named <c>_add</c>) layer onto whatever pose is underneath.
/// </summary>
public sealed record FaceGesture(string Name, string Label, string FileName, FaceGestureKind Kind, bool IsAdditive);

/// <summary>
/// The facial clips one character prefix ships with, found by file name: mouth shapes
/// (<c>&lt;prefix&gt;_phoneme_&lt;shape&gt;[_&lt;mood&gt;].anm</c>), expressions and gestures.
/// </summary>
public sealed partial class FaceClipSet
{
    /// <summary>Mouth shape names in display order; <c>default</c> is the closed mouth the others blend from.</summary>
    public static readonly IReadOnlyList<string> KnownShapes =
        new[] { "default", "closed", "AA", "AH", "EE", "FV", "I", "LL", "MM", "NN", "O", "SH", "SS", "TH", "U" };

    /// <summary>The closed-mouth shape.</summary>
    public const string ClosedShape = "default";

    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    private FaceClipSet(string prefix) => Prefix = prefix;

    public string Prefix { get; }

    /// <summary>Mouth shapes the character has a pose for (in any mood), without <see cref="ClosedShape"/>.</summary>
    public IReadOnlyList<string> MouthShapes { get; private set; } = Array.Empty<string>();

    /// <summary>True when there are mouth shapes outside any mood.</summary>
    public bool HasNeutralMouth { get; private set; }

    public IReadOnlyList<FaceMood> Moods { get; private set; } = Array.Empty<FaceMood>();

    public IReadOnlyList<FaceGesture> Gestures { get; private set; } = Array.Empty<FaceGesture>();

    /// <summary>Every clip file of the set.</summary>
    public IReadOnlyCollection<string> Files => _files;

    /// <summary>
    /// The pose clip for a mouth shape in <paramref name="mood"/>, falling back to the neutral
    /// pose, and for characters without neutral poses to a plain mood (<c>normalA</c>) or any mood.
    /// </summary>
    public string? MouthClip(string shape, FaceMood? mood)
    {
        if (mood?.PhonemeSuffix is { } suffix && Has($"{Prefix}_phoneme_{shape}_{suffix}.anm", out var moody))
            return moody;
        if (Has($"{Prefix}_phoneme_{shape}.anm", out var neutral))
            return neutral;
        foreach (var fallback in FallbackMoods())
            if (Has($"{Prefix}_phoneme_{shape}_{fallback.PhonemeSuffix}.anm", out var other))
                return other;
        return null;
    }

    /// <summary>The mood of the first direction tag that names one (<c>smugA</c>), or null.</summary>
    public FaceMood? FindMood(IEnumerable<string> directions)
    {
        foreach (var direction in directions)
        {
            var key = NormalizeMood(direction);
            var mood = Moods.FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (mood is not null)
                return mood;
        }
        return null;
    }

    public FaceMood? Mood(string key) => Moods.FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    // Plain moods first, so a character without neutral poses still has a resting mouth.
    private IEnumerable<FaceMood> FallbackMoods() => Moods
        .Where(m => m.PhonemeSuffix is not null)
        .OrderBy(m => m.Key.StartsWith("normal", StringComparison.OrdinalIgnoreCase) || m.Key.StartsWith("neutral", StringComparison.OrdinalIgnoreCase) ? 0 : 1);

    private bool Has(string name, out string actual)
    {
        actual = name;
        return _files.TryGetValue(name, out actual!);
    }

    /// <summary>
    /// Groups the facial clips among <paramref name="fileNames"/> by character prefix
    /// (<c>rhys</c>, <c>clementine400</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, FaceClipSet> Index(IEnumerable<string> fileNames)
    {
        var builders = new Dictionary<string, Builder>(StringComparer.OrdinalIgnoreCase);
        Builder For(string prefix) => builders.TryGetValue(prefix, out var b) ? b : builders[prefix] = new Builder(prefix);

        foreach (var name in fileNames)
        {
            if (!name.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) || !MightBeFace(name))
                continue;
            if (PoseName().Match(name) is { Success: true } pose)
            {
                var b = For(pose.Groups["p"].Value);
                b.Files.Add(name);
                var shape = KnownShapes.First(s => s.Equals(pose.Groups["s"].Value, StringComparison.OrdinalIgnoreCase));
                b.Shapes.Add(shape);
                if (pose.Groups["m"].Success)
                    b.MoodFor(pose.Groups["m"].Value).Suffixes.Add(pose.Groups["m"].Value);
                else
                    b.NeutralMouth = true;
            }
            else if (ExpressionName().Match(name) is { Success: true } expression)
            {
                var b = For(expression.Groups["p"].Value);
                b.Files.Add(name);
                var mood = b.MoodFor(expression.Groups["m"].Value);
                if (expression.Groups["k"].Value.Equals("phonemes", StringComparison.OrdinalIgnoreCase))
                    mood.PhonemesClip ??= name;
                else
                    mood.FaceClip ??= name;
            }
            else if (GestureName().Match(name) is { Success: true } gesture)
            {
                var gestureName = gesture.Groups["n"].Value;
                if (gestureName.Equals("default", StringComparison.OrdinalIgnoreCase))
                    continue;
                var b = For(gesture.Groups["p"].Value);
                b.Files.Add(name);
                var kind = gesture.Groups["k"].Value.ToLowerInvariant() switch
                {
                    "headgesture" => FaceGestureKind.Head,
                    "eyes" or "headeyegesture" => FaceGestureKind.Eyes,
                    _ => FaceGestureKind.Face,
                };
                bool additive = gesture.Groups["a"].Success;
                var key = (kind, gestureName.ToLowerInvariant());
                // One entry per gesture, preferring the additive clip.
                if (!b.Gestures.TryGetValue(key, out var existing) || (additive && !existing.IsAdditive))
                    b.Gestures[key] = new FaceGesture(gestureName, Prettify(gestureName), name, kind, additive);
            }
        }
        return builders.ToDictionary(kv => kv.Key, kv => kv.Value.Build(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The prefix of a character's facial clips: the character id without its <c>skNN_</c> rig
    /// tag (<c>sk54_rhys</c> → <c>rhys</c>), else a prefix that matches the display name.
    /// </summary>
    public static string? MatchPrefix(IReadOnlyDictionary<string, FaceClipSet> index, string characterId, string displayName)
    {
        var stem = IdStem(characterId);
        if (index.ContainsKey(stem))
            return index.Keys.First(k => k.Equals(stem, StringComparison.OrdinalIgnoreCase));
        var name = Compact(displayName);
        if (name.Length == 0)
            return null;
        return index.Keys.FirstOrDefault(k => Compact(k) == name)
               ?? index.Keys.Where(k => Compact(k).StartsWith(name, StringComparison.Ordinal)).OrderBy(k => k.Length).FirstOrDefault();
    }

    /// <summary>"sk54_rhys" → "rhys".</summary>
    public static string IdStem(string id)
    {
        int underscore = id.IndexOf('_');
        return underscore > 0 && id.StartsWith("sk", StringComparison.OrdinalIgnoreCase) ? id[(underscore + 1)..] : id;
    }

    /// <summary>A name lowercased with only its letters and digits ("Lady Forrester" → "ladyforrester").</summary>
    public static string Compact(string name) => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>"moodSmugA" → "smugA", "SmugA" → "smugA".</summary>
    public static string NormalizeMood(string mood)
    {
        if (mood.Length > 4 && mood.StartsWith("mood", StringComparison.OrdinalIgnoreCase) && char.IsUpper(mood[4]))
            mood = mood[4..];
        return mood.Length == 0 ? mood : char.ToLowerInvariant(mood[0]) + mood[1..];
    }

    /// <summary>"nodYesQuick" → "Nod yes quick", "smugA" → "Smug A".</summary>
    public static string Prettify(string name)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (char c in name.Replace('_', ' '))
        {
            bool split = c == ' ' || (char.IsUpper(c) && current.Length > 0) ||
                         (char.IsDigit(c) && current.Length > 0 && !char.IsDigit(current[^1]));
            if (split)
            {
                if (current.Length > 0)
                    words.Add(current.ToString());
                current.Clear();
                if (c == ' ')
                    continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
            words.Add(current.ToString());
        if (words.Count == 0)
            return name;
        var text = string.Join(" ", words.Select(w => w.Length > 1 ? w.ToLowerInvariant() : w));
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static bool MightBeFace(string name) =>
        name.Contains("_phoneme", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("_face", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Gesture_", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("_eyes_", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(?<p>.+?)_phoneme_(?<s>default|closed|AA|AH|EE|FV|I|LL|MM|NN|O|SH|SS|TH|U)(?:_(?<m>[A-Za-z0-9]+))?\.anm$", RegexOptions.IgnoreCase)]
    private static partial Regex PoseName();

    [GeneratedRegex(@"^(?<p>.+?)_(?:(?<k>phonemes)_|(?<k>face)_mood)(?<m>[A-Za-z0-9]+)\.anm$", RegexOptions.IgnoreCase)]
    private static partial Regex ExpressionName();

    [GeneratedRegex(@"^(?<p>.+?)_(?<k>headGesture|headEyeGesture|faceGesture|face|eyes)_(?<n>[A-Za-z0-9]+)(?<a>_add)?\.anm$", RegexOptions.IgnoreCase)]
    private static partial Regex GestureName();

    private sealed class Builder
    {
        public Builder(string prefix) => Prefix = prefix;
        public string Prefix { get; }
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Shapes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool NeutralMouth { get; set; }
        public Dictionary<string, MoodBuilder> Moods { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(FaceGestureKind, string), FaceGesture> Gestures { get; } = new();

        public MoodBuilder MoodFor(string raw)
        {
            var key = NormalizeMood(raw);
            return Moods.TryGetValue(key, out var m) ? m : Moods[key] = new MoodBuilder(key);
        }

        public FaceClipSet Build()
        {
            var set = new FaceClipSet(Prefix)
            {
                MouthShapes = KnownShapes.Where(s => s != ClosedShape && Shapes.Contains(s)).ToList(),
                HasNeutralMouth = NeutralMouth,
                Moods = Moods.Values
                    .Select(m => new FaceMood(m.Key, Prettify(m.Key), m.PhonemesClip ?? m.FaceClip,
                        // The suffix spelled like the mood itself wins over a "mood…" spelling.
                        m.Suffixes.OrderBy(s => s.Equals(m.Key, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                            .ThenBy(s => s, StringComparer.OrdinalIgnoreCase).FirstOrDefault()))
                    .OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Gestures = Gestures.Values.OrderBy(g => g.Kind).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            };
            set._files.UnionWith(Files);
            return set;
        }
    }

    private sealed class MoodBuilder
    {
        public MoodBuilder(string key) => Key = key;
        public string Key { get; }
        public string? PhonemesClip { get; set; }
        public string? FaceClip { get; set; }
        public HashSet<string> Suffixes { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
