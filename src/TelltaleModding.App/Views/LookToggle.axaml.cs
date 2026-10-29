using System.ComponentModel;
using Avalonia.Controls;
using TelltaleModding.App.ViewModels;

namespace TelltaleModding.App.Views;

/// <summary>A small "Studio | Game | Graphic black" switch for a 3D viewport's shading, with the intensity of its black shadows.</summary>
public partial class LookToggle : UserControl
{
    public LookToggle()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Keeps <paramref name="viewport"/>'s render mode and style in step with
    /// <paramref name="look"/> until the returned handle is disposed.
    /// </summary>
    public static IDisposable Attach(MeshViewport viewport, ViewportLookViewModel look)
    {
        var prefs = look.Preferences;
        void Apply()
        {
            viewport.Style = look.Style;
            viewport.RenderMode = look.Mode;
            viewport.FieldOfView = (float)prefs.FieldOfView;
            viewport.OrbitSpeed = (float)prefs.OrbitSpeed;
            viewport.DollySpeed = (float)prefs.DollySpeed;
            viewport.FlySpeed = (float)prefs.FlySpeed;
            viewport.GraphicBlackIntensity = (float)prefs.GraphicBlackIntensity;
            viewport.EngineDefaultIntensity = (float)prefs.EngineDefaultIntensity;
            viewport.InvertLook = prefs.InvertLook;
            viewport.BackgroundColor = prefs.BackgroundColor;
        }
        void Changed(object? sender, PropertyChangedEventArgs e) => Apply();
        look.PropertyChanged += Changed;
        prefs.PropertyChanged += Changed;
        Apply();
        return new Detach(() =>
        {
            look.PropertyChanged -= Changed;
            prefs.PropertyChanged -= Changed;
        });
    }

    private sealed class Detach : IDisposable
    {
        private Action? _undo;
        public Detach(Action undo) => _undo = undo;
        public void Dispose()
        {
            _undo?.Invoke();
            _undo = null;
        }
    }
}
