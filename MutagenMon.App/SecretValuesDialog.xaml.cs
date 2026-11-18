using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
/// nor the labels nor the defaults are logged. The label of a required row (FR-55.13)
/// carries a red asterisk and OK stays disabled while a required input is empty.
/// </summary>
public partial class SecretValuesDialog : Window
{
    private readonly IReadOnlyList<ScriptValueRequest> _requests;
    private readonly List<Control> _inputs = new();

    /// <summary>Label to typed value; set only when the window closes via OK.
    /// A value left empty is returned empty: accepted unless the macro is required (FR-55.13), which OK prevents.</summary>
    private IReadOnlyDictionary<string, string>? Result { get; set; }

    private SecretValuesDialog(IReadOnlyList<ScriptValueRequest> requests)
    {
        InitializeComponent();
        _requests = requests;
        BuildRows();
        RequiredNote.Visibility = requests.Any(request_ => request_.IsRequired) ? Visibility.Visible : Visibility.Collapsed;
        UpdateOkState();
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
                Margin = new Thickness(0, 4, 4, 4),
            };
            // The asterisk has its own column so that the asterisks of the required rows are aligned.
            var requiredMark = new TextBlock
            {
                Text = _requests[i].IsRequired ? "*" : "",
                Foreground = Brushes.Red,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 8, 4),
            };
            Grid.SetRow(requiredMark, i);
            Grid.SetColumn(requiredMark, 1);
            RowsGrid.Children.Add(requiredMark);
            Control input = CreateInput(_requests[i]);
            Grid.SetRow(label, i);
            Grid.SetRow(input, i);
            Grid.SetColumn(input, 2);
            RowsGrid.Children.Add(label);
            RowsGrid.Children.Add(input);
            _inputs.Add(input);
        }
    }

    // A secret hides the typed characters; a prompt shows them, starts with its default and selects it so that typing replaces it.
    private Control CreateInput(ScriptValueRequest request)
    {
        var margin = new Thickness(0, 4, 0, 4);
        if (request.IsSecret)
        {
            var passwordBox = new PasswordBox { Margin = margin };
            passwordBox.PasswordChanged += (_, _) => UpdateOkState();
            return passwordBox;
        }

        // No undo history: Clear() after OK must not leave the typed text in the undo stack.
        var textBox = new TextBox { Margin = margin, Text = request.Default ?? "", IsUndoEnabled = false };
        textBox.TextChanged += (_, _) => UpdateOkState();
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

    // OK is disabled while a required input is empty (FR-55.13). A PasswordBox is tested through SecurePassword, so that no
    // string copy of the typed prefix is created at each keystroke.
    private void UpdateOkState() =>
        OkButton.IsEnabled = !_requests.Where((request_, i_) => request_.IsRequired && IsInputEmpty(_inputs[i_])).Any();

    private static bool IsInputEmpty(Control input)
    {
        if (input is not PasswordBox passwordBox)
            return ((TextBox)input).Text.Length == 0;

        using var securePassword = passwordBox.SecurePassword;
        return securePassword.Length == 0;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!OkButton.IsEnabled)
            return;

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
