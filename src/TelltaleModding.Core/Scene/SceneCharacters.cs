using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Meshes;

namespace TelltaleModding.Core.Scene;

/// <summary>
/// The character model a scene places under an agent name. A scene agent ("Clementine" in
/// adv_forestBarn.scene) inherits from the character's <c>.prop</c> (sk62_clementine402.prop),
/// which names the exact model, where the agent name alone fits every Clementine in the game.
/// Scenes are read once and kept.
/// </summary>
public sealed class SceneCharacters
{
    private const int MaxDepth = 4;
    private const int MaxScenes = 32;

    private readonly GameCatalog _catalog;
    private readonly Dictionary<string, CharacterEntry> _byId;
    private readonly Dictionary<string, SceneFile?> _scenes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public SceneCharacters(GameCatalog catalog)
    {
        _catalog = catalog;
        _byId = catalog.Characters
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The character <paramref name="sceneFile"/> places as <paramref name="agentName"/>, or null
    /// when the scene, the agent or a character prop in its inheritance is not found. Names are
    /// compared without case, spaces or punctuation ("LADYFORRESTER" finds "Lady_Forrester").
    /// </summary>
    public CharacterEntry? Find(string sceneFile, string agentName)
    {
        if (sceneFile.Length == 0 || agentName.Length == 0)
            return null;
        lock (_gate)
        {
            if (Load(sceneFile) is not { } scene)
                return null;
            var key = Normalize(agentName);
            var agent = scene.Find(agentName) ?? scene.Agents.FirstOrDefault(a => Normalize(a.Name) == key);
            return agent is null ? null : FromProps(agent.Properties.Parents);
        }
    }

    // The first character among the props an agent inherits, nearest first.
    private CharacterEntry? FromProps(IReadOnlyList<ulong> parents)
    {
        var seen = new HashSet<ulong>();
        var level = parents.ToList();
        for (int depth = 0; depth < MaxDepth && level.Count > 0; depth++)
        {
            var next = new List<ulong>();
            foreach (var symbol in level)
            {
                if (!seen.Add(symbol) || _catalog.FindBySymbol(symbol) is not { } entry)
                    continue;
                if (_byId.TryGetValue(Path.GetFileNameWithoutExtension(entry.Name), out var character))
                    return character;
                if (!entry.Name.EndsWith(".prop", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if (MaterialData.TryParse(_catalog.Read(entry)) is { } prop)
                        next.AddRange(prop.Parents);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                }
            }
            level = next;
        }
        return null;
    }

    private SceneFile? Load(string sceneFile)
    {
        if (_scenes.TryGetValue(sceneFile, out var cached))
            return cached;
        if (_scenes.Count >= MaxScenes)
            _scenes.Clear();
        SceneFile? scene = null;
        try
        {
            if (_catalog.Find(sceneFile) is { } entry && SceneFile.TryLoad(_catalog.Read(entry), out var loaded, out _))
                scene = loaded;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
        }
        _scenes[sceneFile] = scene;
        return scene;
    }

    private static string Normalize(string name) => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
