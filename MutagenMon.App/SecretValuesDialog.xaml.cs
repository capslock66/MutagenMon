using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Scripts;

namespace MutagenMon.App;

/// <summary>
/// The single dialog that asks for the <c>{{secret:Label}}</c> and
/// <c>{{prompt:Label}}</c> values of a script before it runs (FR-55.11,
/// FR-55.12): a grid with one row per distinct label, in the order given,
/// with OK / Cancel. A <c>secret</c> row has a <see cref="PasswordBox"/>; a
/// <c>prompt</c> row has a visible <see cref="TextBox"/>, pre-filled with its
/// default value and with the text selected when it takes the focus. The typed
/// values are returned to the caller and kept nowhere else; neither the values
/// nor the labels nor the defaults are logged.
/// </summary>
public partial class SecretValuesDialog : Window
{
    private readonly IReadOnlyList<ScriptValueRequest> _requests;
    private readonly List<Control> _inputs = new();

    /// <summary>Label to typed value; set only when the window closes via OK.
    /// A value left empty is returned empty (the expander reports it).</summary>
    private IReadOnlyDictionary<string, string>? Result { get; set; }

    private SecretValuesDialog(IReadOnlyList<ScriptValueRequest> requests)
    {
        InitializeComponent();
        _requests = requests;
        BuildRows();
        Loaded += (_, _) => _inputs[0].Focus();
    }

    /// <summary>Shows the dialog and returns the value typed for each label,
    /// or null when the user cancelled or closed it. <paramref name="requests"/>
    /// must not be empty.</summary>
    public static IReadOnlyDictionary<string, string>? Ask(Window? owner, ILogger logger, IReadOnlyList<ScriptValueRequest> requests)
    {
        var dialog = new SecretValuesDialog(requests) { Owner = owner };
        logger.LogInformation("Scripts values dialog opened for {Count} value(s)", requests.Count);
        var accepted = dialog.ShowDialog() == true;
        logger.LogInformation("User action: Scripts values dialog {Outcome}", accepted ? "OK clicked" : "cancelled");
        return accepted ? dialog.Result : null;
    }

    private void BuildRows()
    {
        for (var i = 0; i < _requests.Count; i++)
        {
            RowsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock
            {
                Text = _requests[i].Label,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 12, 4),
            };
            Control input = CreateInput(_requests[i]);
            Grid.SetRow(label, i);
            Grid.SetRow(input, i);
            Grid.SetColumn(input, 1);
            RowsGrid.Children.Add(label);
            RowsGrid.Children.Add(input);
            _inputs.Add(input);
        }
    }

    // A secret hides the typed characters; a prompt shows them, starts with its default and selects it so that typing replaces it.
    private static Control CreateInput(ScriptValueRequest request)
    {
        var margin = new Thickness(0, 4, 0, 4);
        if (request.IsSecret)
            return new PasswordBox { Margin = margin };

        // No undo history: Clear() after OK must not leave the typed text in the undo stack.
        var textBox = new TextBox { Margin = margin, Text = request.Default ?? "", IsUndoEnabled = false };
        textBox.GotKeyboardFocus += (_, _) => textBox.SelectAll();
        // A click would otherwise place the caret and drop the selection made on focus: the first click only gives the focus.
        textBox.PreviewMouseLeftButtonDown += (_, e_) =>
        {
            if (textBox.IsKeyboardFocusWithin)
                return;

            e_.Handled = true;
            textBox.Focus();
        };
        return textBox;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < _requests.Count; i++)
            values[_requests[i].Label] = ReadInput(_inputs[i]);

        Result = values;
        // The typed text must not stay in the controls once the caller has it.
        foreach (var input in _inputs)
            ClearInput(input);
        DialogResult = true;
    }

    private static string ReadInput(Control input) => input is PasswordBox passwordBox ? passwordBox.Password : ((TextBox)input).Text;

    private static void ClearInput(Control input)
    {
        if (input is PasswordBox passwordBox)
            passwordBox.Clear();
        else
            ((TextBox)input).Clear();
    }
}
