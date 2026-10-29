using CommunityToolkit.Mvvm.Input;
using TelltaleModding.App.Services;
using TelltaleModding.Core.Export;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.App.ViewModels;

/// <summary>Exporting the shown character to glTF.</summary>
public sealed partial class CharactersViewModel
{
    /// <summary>File pickers; set by the shell.</summary>
    public IDialogService? Dialogs { get; set; }

    /// <summary>Raised with a short message for the status bar.</summary>
    public event Action<string>? StatusMessage;

    /// <summary>
    /// Saves the character as shown (the worn parts, their textures and the skeleton)
    /// as a .glb file, with the loaded clip baked in as an animation.
    /// </summary>
    [RelayCommand]
    private async Task ExportGltfAsync()
    {
        if (Dialogs is null || _model is not { } model)
            return;

        var clip = model.CanAnimate ? _clip : null;
        string baseName = SafeFileName(SelectedCharacter?.Entry.Id ?? Title, "model");
        string suggested = clip is { } c
            ? $"{baseName}_{SafeFileName(Path.GetFileNameWithoutExtension(c.Name), "clip")}.glb"
            : $"{baseName}.glb";
        var path = await Dialogs.SaveFileAsync("Export character as glTF", suggested, ("glTF binary", new[] { "*.glb" }));
        if (path is null)
            return;

        string name = string.IsNullOrWhiteSpace(Title) ? baseName : Title;
        var facePose = FacePoseSnapshot();
        StatusMessage?.Invoke($"Exporting {name}…");
        try
        {
            var summary = await Task.Run(() =>
            {
                var clips = new List<GltfClip>();
                if (clip is { } loaded)
                {
                    // A player of its own, so exporting never disturbs playback.
                    var player = new AnimationPlayer(model.Geometry, model.Skeleton!);
                    player.Load(loaded.Clip, loaded.Name);
                    clips.Add(new GltfClip(Path.GetFileNameWithoutExtension(loaded.Name), player));
                }
                if (facePose is not null)
                    clips.Add(new GltfClip("face pose", new AnimationPlayer(model.Geometry, model.Skeleton!)) { Pose = facePose });
                return GltfExporter.WriteGlb(model, path, clips, new GltfExportOptions
                {
                    Name = name,
                    FramesPerSecond = Services.UserPreferences.Current.GltfFramesPerSecond,
                    IncludeNormalMaps = Services.UserPreferences.Current.GltfNormalMaps,
                });
            });
            StatusMessage?.Invoke(
                $"Exported {Path.GetFileName(path)} · {summary.Bytes / 1048576.0:0.0} MB · " +
                $"{summary.Textures} texture{(summary.Textures == 1 ? "" : "s")}" +
                (summary.Joints > 0 ? $" · {summary.Joints} bones" : "") +
                (summary.Animations > 0 ? " · with animation" : ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StatusMessage?.Invoke($"Could not export: {ex.Message}");
        }
    }

    private static string SafeFileName(string text, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(text.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return clean.Length == 0 ? fallback : clean;
    }
}
