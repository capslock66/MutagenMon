using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;

namespace MutagenMon.App;

/// <summary>
/// The single dialog that asks for the <c>{{secret:Label}}</c> values of a
/// script before it runs (FR-55.11): a grid with one row per distinct label,
/// in the order given, each with a <see cref="PasswordBox"/>, and OK / Cancel.
/// The typed values are returned to the caller and kept nowhere else; neither
/// the values nor the labels are logged.
/// </summary>
public partial class SecretValuesDialog : Window
{
    private readonly IReadOnlyList<string> _labels;
    private readonly List<PasswordBox> _inputs = new();

    /// <summary>Label to typed value; set only when the window closes via OK.
    /// A value left empty is returned empty (the expander reports it).</summary>
    private IReadOnlyDictionary<string, string>? Result { get; set; }

    private SecretValuesDialog(IReadOnlyList<string> labels)
    {
        InitializeComponent();
        _labels = labels;
        BuildRows();
        Loaded += (_, _) => _inputs[0].Focus();
    }

    /// <summary>Shows the dialog and returns the value typed for each label,
    /// or null when the user cancelled or closed it. <paramref name="labels"/>
    /// must not be empty.</summary>
    public static IReadOnlyDictionary<string, string>? Ask(Window? owner, ILogger logger, IReadOnlyList<string> labels)
    {
        var dialog = new SecretValuesDialog(labels) { Owner = owner };
        logger.LogInformation("Scripts secret dialog opened for {Count} secret(s)", labels.Count);
        var accepted = dialog.ShowDialog() == true;
        logger.LogInformation("User action: Scripts secret dialog {Outcome}", accepted ? "OK clicked" : "cancelled");
        return accepted ? dialog.Result : null;
    }

    private void BuildRows()
    {
        for (var i = 0; i < _labels.Count; i++)
        {
            RowsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock
            {
                Text = _labels[i],
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 12, 4),
            };
            var input = new PasswordBox { Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(label, i);
            Grid.SetRow(input, i);
            Grid.SetColumn(input, 1);
            RowsGrid.Children.Add(label);
            RowsGrid.Children.Add(input);
            _inputs.Add(input);
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < _labels.Count; i++)
            values[_labels[i]] = _inputs[i].Password;

        Result = values;
        // The typed text must not stay in the controls once the caller has it.
        foreach (var input in _inputs)
            input.Clear();
        DialogResult = true;
    }
}
