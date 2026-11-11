using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;

namespace MutagenMon.App;

/// <summary>
/// The modal dialog that shows one script run started from the Sync tab
/// (FR-56.5): the output as it arrives, the status line, Cancel while the script
/// runs and Close once it has ended. The run starts as soon as the dialog opens;
/// the confirmation and the macro expansion were done before
/// (<see cref="ScriptRunCoordinator.Prepare"/>). The dialog cannot be closed
/// while the script runs: the user cancels first. Neither the output nor the
/// expanded script text is logged (FR-54.2, FR-54.3).
/// </summary>
public partial class ScriptRunWindow : Window
{
    private readonly ILogger _logger;
    private readonly ScriptRunCoordinator _coordinator;
    private readonly ScriptRunPreparation _preparation;
    private bool _isFinished;

    private ScriptRunWindow(
        Window? owner, ILogger logger, ScriptRunCoordinator coordinator, ScriptRunPreparation preparation, string sessionName)
    {
        InitializeComponent();
        Owner = owner;
        _logger = logger;
        _coordinator = coordinator;
        _preparation = preparation;
        Title = $"Run '{preparation.Script.Name}' on session '{sessionName}'";
    }

    /// <summary>Opens the dialog for <paramref name="preparation"/> and blocks
    /// until it is closed. A preparation that carries a failure (macro error,
    /// cancelled secret dialog) is shown as its message, without running.</summary>
    public static void ShowRun(
        Window? owner, ILogger logger, ScriptRunCoordinator coordinator, ScriptRunPreparation preparation, string sessionName)
    {
        var window = new ScriptRunWindow(owner, logger, coordinator, preparation, sessionName);
        logger.LogInformation("Script run dialog opened: script '{Script}' on session '{Session}'", preparation.Script.Name, sessionName);
        window.ShowDialog();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_preparation.IsReady)
        {
            SetStatus(_preparation.FailureMessage, _preparation.FailureIsError);
            MarkFinished();
            return;
        }

        var callbacks = new ScriptRunCallbacks(
            NotStarted: SetStatus,
            Starting: () => CancelRunButton.IsEnabled = true,
            Status: SetStatus,
            Output: line_ => ScriptOutputPane.Append(OutputBox, line_),
            Finished: MarkFinished);
        // An async void handler: an unexpected failure is shown in the status line, never left to the global
        // handler, and the dialog must always become closable.
        try
        {
            await _coordinator.RunPreparedAsync(_preparation, callbacks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Script '{Script}': the run dialog failed unexpectedly", _preparation.Script.Name);
            SetStatus($"Script run failed: {ex.Message}", true);
        }
        finally
        {
            MarkFinished();
        }
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError ? Brushes.Red : SystemColors.ControlTextBrush;
    }

    /// <summary>The run ended (or never started): Cancel off, Close on. Safe to call twice.</summary>
    private void MarkFinished()
    {
        _isFinished = true;
        CancelRunButton.IsEnabled = false;
        CloseButton.IsEnabled = true;
        CloseButton.Focus();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("User action: script run dialog Cancel clicked ({Script})", _preparation.Script.Name);
        if (_coordinator.Cancel())
            CancelRunButton.IsEnabled = false;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Logged once whatever closed the dialog: Close, Esc, the title bar or Alt+F4.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _logger.LogInformation("User action: script run dialog closed ({Script})", _preparation.Script.Name);
        base.OnClosed(e);
    }

    /// <summary>FR-56.5: the title bar, Alt+F4 and Esc cannot close the dialog while the script runs.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isFinished)
            e.Cancel = true;
        base.OnClosing(e);
    }
}
