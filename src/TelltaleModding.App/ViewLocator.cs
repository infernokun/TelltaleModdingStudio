using Avalonia.Controls;
using Avalonia.Controls.Templates;
using TelltaleModding.App.ViewModels;

namespace TelltaleModding.App;

/// <summary>Resolves a view for a view-model by naming convention (…ViewModel -> …View).</summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? data)
    {
        if (data is null)
            return null;

        var name = data.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type != null)
            return (Control)Activator.CreateInstance(type)!;

        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
