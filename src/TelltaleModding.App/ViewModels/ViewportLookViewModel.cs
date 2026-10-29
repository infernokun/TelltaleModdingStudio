using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.App.ViewModels;

/// <summary>
/// How the 3D viewports shade: studio lighting, the open game's stylised look, or that look
/// with full graphic black. One
/// instance is shared by every workspace, so switching in one viewport switches them all.
/// </summary>
public sealed partial class ViewportLookViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStudio), nameof(IsGameLook), nameof(IsGraphicBlack), nameof(ShowEngineIntensity))]
    private RenderMode _mode = RenderMode.Studio;

    /// <summary>The open game's look, used in <see cref="RenderMode.GameLook"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameLookTip), nameof(GraphicBlackTip), nameof(ShowEngineIntensity))]
    private GameLookStyle _style = GameLookStyle.Default;

    /// <summary>Camera and background preferences for every viewport.</summary>
    public Services.UserPreferences Preferences { get; set; } = Services.UserPreferences.Current;

    public bool IsStudio => Mode == RenderMode.Studio;

    public bool IsGameLook => Mode == RenderMode.GameLook;

    public bool IsGraphicBlack => Mode == RenderMode.GraphicBlack;

    /// <summary>True in the Game look of a game whose own look has graphic black, so its intensity applies.</summary>
    public bool ShowEngineIntensity => IsGameLook && Style.InkStrength > 0f;

    public string GameLookTip => Style == GameLookStyle.Default
        ? "Game look: ink outlines and cel-shaded light"
        : $"Game look: {Style.Name}'s ink outlines and shading";

    public string GraphicBlackTip => Style == GameLookStyle.Default
        ? "Graphic black: the game look with every shadow inked solid black"
        : $"Graphic black: {Style.Name}'s look with every shadow inked solid black";

    [RelayCommand]
    private void ShowStudio() => Mode = RenderMode.Studio;

    [RelayCommand]
    private void ShowGameLook() => Mode = RenderMode.GameLook;

    [RelayCommand]
    private void ShowGraphicBlack() => Mode = RenderMode.GraphicBlack;
}
