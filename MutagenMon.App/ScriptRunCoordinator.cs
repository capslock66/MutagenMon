using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MutagenMon.Core.Scripts;
using MutagenMon.Core.Status;

namespace MutagenMon.App;

/// <summary>
/// What a caller of <see cref="ScriptRunCoordinator.RunAsync"/> shows of a
/// run. Every callback runs on the UI thread.
/// <list type="bullet">
/// <item><see cref="NotStarted"/>: the run did not start (macro error, secret
/// dialog cancelled): the message and whether it is an error (FR-55.6, FR-55.11).</item>
/// <item><see cref="Starting"/>: the run is about to start (after the
/// confirmation and the macro expansion); the caller clears its output.</item>
/// <item><see cref="Status"/>: the status line (FR-52.3), with an error flag.</item>
/// <item><see cref="Output"/>: one line of output (FR-52.1).</item>
/// <item><see cref="Finished"/>: the run ended, whatever the outcome; not
/// called when the run did not start.</item>
/// </list>
/// </summary>
public sealed record ScriptRunCallbacks(
    Action<string, bool> NotStarted,
    Action Starting,
    Action<string, bool> Status,
    Action<ScriptOutputLine> Output,
    Action Finished);

/// <summary>
/// The run of one script, shared by every place that runs a script (FR-51,
/// FR-53, FR-55, FR-56): the `@confirm` confirmation, the macro expansion
/// (with the secret dialog), the <see cref="ScriptRunner"/> call, the cancel
/// and the outcome text. Only one script runs at a time (FR-51.1, FR-56.6):
/// <see cref="IsRunning"/> is the single source of that rule. What is shown —
/// the Scripts tab pane or the Sync tab dialog — is the caller's, through
/// <see cref="ScriptRunCallbacks"/>. UI thread only; shares no state with
/// polling or the tray icon (NFR-1).
/// </summary>
public sealed class ScriptRunCoordinator(ILogger logger, Func<ScriptRunner> runnerFactory, Dispatcher dispatcher)
{
    private CancellationTokenSource? _runSource;
    private ScriptRunCallbacks? _callbacks;
    private DispatcherTimer? _ticker;

    /// <summary>Raised on the UI thread while a script runs, each time the
    /// elapsed seconds change (FR-52.6): the text "Running '&lt;name&gt;' on
    /// &lt;host&gt;, N seconds"; and with null when the run ends. The status
    /// window shows it in its title.</summary>
    public event EventHandler<string?>? RunInfoChanged;

    /// <summary>True from the start of a run to its end.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>True once Cancel was accepted and until the run ends.</summary>
    public bool IsCancelling { get; private set; }

    /// <summary>Confirms (when the script asks for it), expands the macros
    /// and runs <paramref name="script"/> with the session selected at the
    /// click (FR-55.3). Returns when the run has ended or did not start.
    /// Failures of the macro expansion and of the run are reported through
    /// <paramref name="callbacks"/>; an exception thrown by a callback itself
    /// or by the confirmation dialog propagates to the caller.</summary>
    public async Task RunAsync(Window? owner, ScriptDefinition script, SessionSummaryRow? session, ScriptRunCallbacks callbacks)
    {
        if (IsRunning)
            return;

        if (script.Confirm
            && !GenericMessageDialog.ShowConfirm(owner, logger, "Run script", DescribeConfirmation(script, session), "Run"))
            return;

        // After the @confirm check (FR-55.7); the script is the one passed to the runner.
        // An unexpected failure of a reader or of the dialog is shown in the status line, not left
        // to the global handler. The messages of these exceptions never carry a macro value.
        ScriptMacroExpansion? expansion;
        try
        {
            if (!TryExpandMacros(owner, script, session, callbacks, out expansion))
                return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Script '{Script}': the macro expansion failed unexpectedly", script.Name);
            callbacks.NotStarted($"Macro error: {ex.Message}", true);
            return;
        }

        IsRunning = true;
        _callbacks = callbacks;
        _runSource = new CancellationTokenSource();
        // Inside the try so that the finally always clears IsRunning: the flag is shared by the tabs, a stuck
        // value would block every Run.
        try
        {
            callbacks.Starting();
            StartTicker(script, callbacks);
            var result = await runnerFactory().RunAsync(
                script, line => dispatcher.BeginInvoke(() => callbacks.Output(line)), _runSource.Token, expansion);
            // Before the outcome: a tick must not overwrite it.
            StopTicker();
            callbacks.Status(DescribeOutcome(result), result.Outcome != ScriptRunOutcome.Completed || result.ExitCode != 0);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Script '{Script}' failed unexpectedly", script.Name);
            StopTicker();
            callbacks.Status($"Script run failed: {ex.Message}", true);
        }
        finally
        {
            StopTicker();
            _runSource.Dispose();
            _runSource = null;
            _callbacks = null;
            IsRunning = false;
            IsCancelling = false;
            callbacks.Finished();
        }
    }

    /// <summary>Shows the running text now, then again each time the elapsed
    /// seconds change (FR-52.3). The value is read from a stopwatch, not counted
    /// from ticks: it never drifts or repeats. The timer ticks four times a
    /// second but only publishes on a new second; a UI busy for over a second
    /// delays the refresh and the next value shown is the exact elapsed time.
    /// After a cancel request the status line keeps "Cancelling...".</summary>
    private void StartTicker(ScriptDefinition script, ScriptRunCallbacks callbacks)
    {
        var stopwatch = Stopwatch.StartNew();
        var shownSeconds = -1;

        void Publish()
        {
            var seconds = (int)stopwatch.Elapsed.TotalSeconds;
            if (seconds == shownSeconds)
                return;

            shownSeconds = seconds;
            var text = DescribeRunning(script, seconds);
            if (!IsCancelling)
                callbacks.Status(text, false);
            RunInfoChanged?.Invoke(this, text);
        }

        Publish();
        // This constructor starts the timer; it ticks on the UI thread.
        _ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => PublishFromTimer(), dispatcher);

        // A tick runs outside the try of RunAsync: a failure must not reach the global handler (a modal message
        // box every second): log it and stop the refresh, the run itself carries on.
        void PublishFromTimer()
        {
            try
            {
                Publish();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Script '{Script}': the elapsed-time refresh failed", script.Name);
                StopTicker();
            }
        }
    }

    private void StopTicker()
    {
        if (_ticker is null)
            return;

        _ticker.Stop();
        _ticker = null;
        RunInfoChanged?.Invoke(this, null);
    }

    private static string DescribeRunning(ScriptDefinition script, int seconds) =>
        $"Running '{script.Name}' on {script.Host}, {seconds} {(seconds == 1 ? "second" : "seconds")}";

    /// <summary>Cancels the run in progress (FR-51.4): shows "Cancelling..."
    /// right away and kills ssh off the UI thread. Returns false when there
    /// is nothing to cancel or a cancel is already under way.</summary>
    public bool Cancel()
    {
        if (_runSource is null || _callbacks is null || IsCancelling)
            return false;

        IsCancelling = true;
        // The run only reports "Cancelled" once ssh is killed and has exited: say so right away.
        _callbacks.Status("Cancelling...", false);
        var source = _runSource;
        // Cancel() runs the cancellation callbacks on the calling thread, and the run's continuation (kill ssh, wait for it to exit,
        // which can take seconds) may be inlined there: on the UI thread that would freeze the window before "Cancelling..." is painted.
        _ = Task.Run(() =>
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run ended on its own in the meantime.
            }
        });
        return true;
    }

    /// <summary>The `@confirm` text (FR-53.1, FR-55.7): the script and its host,
    /// plus the resolved session name when the script uses a `session` macro.
    /// Never the expanded text.</summary>
    private static string DescribeConfirmation(ScriptDefinition script, SessionSummaryRow? session)
    {
        var text = $"Run script '{script.Name}' on {script.Host}?";
        if (ScriptMacro.Find(script.Body).Any(macro_ => macro_.Type == ScriptMacroType.Session))
            text += $"\n\nSelected session: {session?.Name ?? "(none)"}";
        return text;
    }

    /// <summary>Expands the macros of the body of <paramref name="script"/>
    /// (FR-55). Returns false when the run must not start — an unresolved
    /// macro, or the secret dialog cancelled — after reporting why through
    /// <see cref="ScriptRunCallbacks.NotStarted"/> (FR-55.6, FR-55.11).
    /// <paramref name="expansion"/> stays null for a script without macros,
    /// which is run as written.</summary>
    private bool TryExpandMacros(
        Window? owner, ScriptDefinition script, SessionSummaryRow? session, ScriptRunCallbacks callbacks,
        out ScriptMacroExpansion? expansion)
    {
        expansion = null;
        var macros = ScriptMacro.Find(script.Body);
        if (macros.Count == 0)
            return true;

        IReadOnlyDictionary<string, string>? typedSecrets = null;
        var labels = ScriptMacro.SecretLabels(macros);
        if (labels.Count > 0)
        {
            // The other macros are checked first, with a placeholder for the secrets, so the user is not asked
            // to type secrets for a run that cannot start anyway.
            var preCheck = ScriptMacroExpander.Expand(script.Body, BuildMacroSources(session, _ => "x"));
            if (!preCheck.IsSuccess)
            {
                ReportNotStarted(script, callbacks, "Macro error: " + string.Join("; ", preCheck.Errors), isError: true);
                return false;
            }

            typedSecrets = SecretValuesDialog.Ask(owner, logger, labels);
            if (typedSecrets is null)
            {
                ReportNotStarted(script, callbacks, "Run cancelled: the secret values were not entered.", isError: false);
                return false;
            }
        }

        var result = ScriptMacroExpander.Expand(
            script.Body,
            BuildMacroSources(session, label_ => typedSecrets is not null && typedSecrets.TryGetValue(label_, out var value_) ? value_ : null));
        if (!result.IsSuccess)
        {
            ReportNotStarted(script, callbacks, "Macro error: " + string.Join("; ", result.Errors), isError: true);
            return false;
        }

        expansion = result;
        return true;
    }

    /// <summary>The macro value sources (FR-55): the Windows readers for
    /// `env`, `reg` and `cred` (they return null instead of throwing), the
    /// given secret lookup, and the selected session.</summary>
    private static ScriptMacroSources BuildMacroSources(SessionSummaryRow? session, Func<string, string?> secret) =>
        new(WindowsMacroValueReader.ReadEnvironmentVariable, WindowsMacroValueReader.ReadRegistryValue,
            WindowsMacroValueReader.ReadCredentialPassword, secret, session);

    /// <summary>Logs and reports why the run did not start. The message names
    /// macros, never values.</summary>
    private void ReportNotStarted(ScriptDefinition script, ScriptRunCallbacks callbacks, string message, bool isError)
    {
        logger.Log(isError ? LogLevel.Warning : LogLevel.Information, "Script '{Script}' not run: {Message}", script.Name, message);
        callbacks.NotStarted(message, isError);
    }

    /// <summary>The outcome of a run (FR-52.3); cancel and timeout state
    /// that server-side processes already detached keep running (FR-51.5).</summary>
    private static string DescribeOutcome(ScriptRunResult result)
    {
        const string DetachedNote = "The local ssh connection was closed; processes the script already detached on the server keep running.";
        var duration = $"{result.Duration.TotalSeconds:0.0}s";
        return result.Outcome switch
        {
            ScriptRunOutcome.Completed => $"Exit code {result.ExitCode} ({duration})",
            ScriptRunOutcome.Cancelled => $"Cancelled after {duration}. {DetachedNote}",
            ScriptRunOutcome.TimedOut => $"Timed out after {duration}. {DetachedNote}",
            _ => $"Failed to start ssh: {result.Error}",
        };
    }
}
