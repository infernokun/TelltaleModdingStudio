using System.ComponentModel;
using Avalonia.Controls;
using AvaloniaEdit.Search;
using AvaloniaEdit.TextMate;
using TelltaleModding.App.ViewModels;
using TextMateSharp.Grammars;

namespace TelltaleModding.App.Views;

public partial class ScriptsView : UserControl
{
    private ScriptsViewModel? _vm;

    public ScriptsView()
    {
        InitializeComponent();

        var options = new RegistryOptions(ThemeName.DarkPlus);
        var textMate = Editor.InstallTextMate(options);
        textMate.SetGrammar(options.GetScopeByLanguageId(options.GetLanguageByExtension(".lua").Id));
        SearchPanel.Install(Editor);
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.HighlightCurrentLine = true;

        OutlineList.SelectionChanged += (_, _) =>
        {
            if (OutlineList.SelectedItem is string name)
                Reveal(name);
        };

        DataContextChanged += (_, _) =>
        {
            AttachEditorPreferences();
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnViewModelChanged;
                _vm.RevealRequested -= Reveal;
            }
            _vm = DataContext as ScriptsViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnViewModelChanged;
                _vm.RevealRequested += Reveal;
                Editor.Text = _vm.Code;
            }
        };
    }

    // The editor's text is not bindable; mirror the view model's code into it.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScriptsViewModel.Code) && _vm is not null)
        {
            Editor.Text = _vm.Code;
            Editor.ScrollToHome();
        }
    }

    // Jumps to where a function is defined ("function Name", "Name = function"),
    // falling back to its first mention.
    private void Reveal(string name)
    {
        var text = Editor.Text;
        var bare = name.Split(' ', '(')[0];
        int at = -1;
        foreach (var pattern in new[] { $"function {bare}(", $"function {bare} (", $"{bare} = function", bare })
        {
            at = text.IndexOf(pattern, StringComparison.Ordinal);
            if (at >= 0)
                break;
        }
        if (at < 0)
            return;
        var line = Editor.Document.GetLineByOffset(at);
        Editor.TextArea.Caret.Offset = at;
        Editor.ScrollTo(line.LineNumber, 0);
        Editor.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(Editor.TextArea, at, at + Math.Min(bare.Length, text.Length - at));
    }

    private Services.UserPreferences? _prefs;

    // Font size and wrapping follow Settings (the shell's preferences exist once the view model does).
    private void AttachEditorPreferences()
    {
        var prefs = Services.UserPreferences.Current;
        if (ReferenceEquals(prefs, _prefs))
            return;
        if (_prefs is not null)
            _prefs.PropertyChanged -= OnPreferenceChanged;
        _prefs = prefs;
        prefs.PropertyChanged += OnPreferenceChanged;
        ApplyEditorPreferences();
    }

    private void OnPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Services.UserPreferences.CodeFontSize) or nameof(Services.UserPreferences.CodeWordWrap))
            ApplyEditorPreferences();
    }

    private void ApplyEditorPreferences()
    {
        if (_prefs is null)
            return;
        Editor.FontSize = _prefs.CodeFontSize;
        Editor.WordWrap = _prefs.CodeWordWrap;
    }
}
