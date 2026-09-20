using System.Windows;
using System.Windows.Controls;

namespace MutagenMon.App;

/// <summary>
/// Small reusable prompt for a single line of text — used by
/// <see cref="SshFolderPickerWindow"/>'s "New folder" action (FR-18.5,
/// ENDPOINT_PATH_PICKER_PLAN.md's Phase 3); no existing equivalent in the
/// app before this (<see cref="GenericMessageDialog"/> only shows messages,
/// it doesn't accept input).
/// </summary>
public partial class FolderNameInputDialog : Window
{
    /// <summary>Set only when the window closes via OK.</summary>
    public string? Result { get; private set; }

    public FolderNameInputDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e) =>
        OkButton.IsEnabled = IsValidFolderName(NameBox.Text.Trim());

    private static bool IsValidFolderName(string name) =>
        name.Length > 0 && name != "." && name != ".." && !name.Contains('/') && !name.Contains('\\');

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Result = NameBox.Text.Trim();
        DialogResult = true;
    }
}
