using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MutagenMon.App;

/// <summary>One search request raised by <see cref="FindBar"/>.</summary>
/// <param name="Text">Text to look for (never empty).</param>
/// <param name="MatchCase">True for a case-sensitive search.</param>
/// <param name="Forward">True to search towards the end, false towards the start.</param>
/// <param name="IncludeCurrent">True when the current match may be kept (the search text or the case option was
/// edited), false when the search must move past it (Next / Previous).</param>
public sealed record FindRequest(string Text, bool MatchCase, bool Forward, bool IncludeCurrent)
{
    public StringComparison Comparison => MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}

/// <summary>
/// Search bar shared by the Scripts and Logs tabs. It only collects the search
/// text and options and raises <see cref="FindRequested"/>; the owning view
/// searches its own content and reports back through <see cref="ReportResult"/>.
/// </summary>
public partial class FindBar : UserControl
{
    public event EventHandler<FindRequest>? FindRequested;

    /// <summary>Raised after the bar was closed, so the view can give the focus back to its content.</summary>
    public event EventHandler? Closed;

    public FindBar()
    {
        InitializeComponent();
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public bool HasText => SearchBox.Text.Length > 0;

    /// <summary>Shows the bar and puts the focus in the search box, with its text selected.
    /// A single-line <paramref name="prefill"/> replaces the previous search text.</summary>
    public void Open(string? prefill)
    {
        // Never overwrite what is being typed: Ctrl+F pressed inside the box only selects its text.
        var isTyping = SearchBox.IsKeyboardFocusWithin;
        Visibility = Visibility.Visible;
        if (!isTyping && !string.IsNullOrEmpty(prefill) && !prefill.Contains('\n') && !prefill.Contains('\r'))
            SearchBox.Text = prefill;
        // Right after Visibility changes the box is not yet laid out, so focusing it synchronously fails;
        // the focus request is queued until the layout pass has run.
        Dispatcher.BeginInvoke(() =>
        {
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>Shows the bar without taking the focus (F3 with a remembered search text).</summary>
    public void Show() => Visibility = Visibility.Visible;

    public void Close()
    {
        Visibility = Visibility.Collapsed;
        ResultText.Text = "";
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Goes to the next (or previous) match of the current search text.</summary>
    public void FindNext(bool forward) => RaiseFind(forward, includeCurrent: false);

    public void ReportResult(bool found) => ResultText.Text = found ? "" : "Not found";

    /// <summary>Forgets the last result, e.g. when the searched content was replaced.</summary>
    public void ClearResult() => ResultText.Text = "";

    /// <summary>Handles the shortcuts shared by the views: Ctrl+F opens the bar, F3 / Shift+F3 go to the
    /// next / previous match, Esc closes the bar. Returns true when the key was handled.</summary>
    /// <param name="prefill">Text selected in the searched content, offered as the search text.</param>
    public bool HandleKey(KeyEventArgs e, string? prefill)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            Open(prefill);
        else if (e.Key == Key.F3)
        {
            if (!IsOpen && !HasText)
                Open(prefill);
            else
            {
                Show();
                FindNext(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
            }
        }
        else if (e.Key == Key.Escape && IsOpen)
            Close();
        else
            return false;

        e.Handled = true;
        return true;
    }

    private void RaiseFind(bool forward, bool includeCurrent)
    {
        if (!HasText)
        {
            ResultText.Text = "";
            return;
        }

        FindRequested?.Invoke(this, new FindRequest(SearchBox.Text, MatchCaseCheck.IsChecked == true, forward, includeCurrent));
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => RaiseFind(forward: true, includeCurrent: true);

    private void OnMatchCaseChanged(object sender, RoutedEventArgs e) => RaiseFind(forward: true, includeCurrent: true);

    private void OnNextClick(object sender, RoutedEventArgs e) => FindNext(forward: true);

    private void OnPreviousClick(object sender, RoutedEventArgs e) => FindNext(forward: false);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnSearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        FindNext(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
    }
}
