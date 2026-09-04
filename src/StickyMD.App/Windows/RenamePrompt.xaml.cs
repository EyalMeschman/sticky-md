using System.Windows;

namespace StickyMD.App.Windows;

public partial class RenamePrompt : Window
{
    public RenamePrompt(string currentName)
    {
        InitializeComponent();

        NameBox.Text = currentName;

        // SelectAll/Focus belong on Loaded, not here. Before the window is
        // shown, focus has nowhere to land and Select/SelectAll on an
        // unrendered TextBox is the fragile ordering -- it works today only
        // because nothing else asks for focus first.
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };

        // Disabling on blank -- not name VALIDATION -- gives a visible reason
        // the button does nothing, instead of a click that silently returns
        // and looks like a dead button. NoteRepository.Rename still owns
        // every actual validation rule.
        NameBox.TextChanged += (_, _)
            => OkButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

        OkButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text)) return;

            NewName = NameBox.Text.Trim();
            DialogResult = true;
        };
    }

    public string NewName { get; private set; } = string.Empty;
}
