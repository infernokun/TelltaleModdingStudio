using CommunityToolkit.Mvvm.ComponentModel;
using TelltaleModding.Core.Projects;

namespace TelltaleModding.App.ViewModels;

/// <summary>One asset replacement row in the mod project panel.</summary>
public sealed partial class SwapViewModel : ViewModelBase
{
    public SwapViewModel(AssetSwap swap)
    {
        Swap = swap;
        _enabled = swap.Enabled;
        _replacementFilePath = swap.ReplacementFilePath;
    }

    /// <summary>The underlying project model this row edits.</summary>
    public AssetSwap Swap { get; }

    public string TargetAssetName => Swap.TargetAssetName;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _replacementFilePath;

    public string ReplacementFileName =>
        string.IsNullOrEmpty(ReplacementFilePath) ? "(none)" : Path.GetFileName(ReplacementFilePath);

    partial void OnEnabledChanged(bool value) => Swap.Enabled = value;

    partial void OnReplacementFilePathChanged(string value)
    {
        Swap.ReplacementFilePath = value;
        OnPropertyChanged(nameof(ReplacementFileName));
    }
}
