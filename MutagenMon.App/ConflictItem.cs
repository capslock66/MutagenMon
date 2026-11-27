using System.ComponentModel;
using MutagenMon.Core.Resolution;

namespace MutagenMon.App;

/// <summary>One row of <see cref="ConflictResolutionWindow"/>'s grid: a
/// pending conflict plus the resolution the user picked for it (not applied
/// until the dialog's OK). Raises <see cref="PropertyChanged"/> so the
/// Resolution cell follows the choice live.</summary>
public sealed class ConflictItem : INotifyPropertyChanged
{
    public const string VisualMergeText = "Visual merge";
    public const string AlphaWinsText = "Alpha wins";
    public const string BetaWinsText = "Beta wins";

    private ConflictResolutionChoice? _choice;
    private string _error = "";
    private bool _isResolved;

    public ConflictItem(PendingConflict conflict, string description, string details)
    {
        Conflict = conflict;
        Description = description;
        Details = details;
        ChoiceOptions = [AlphaWinsText, BetaWinsText];
    }

    public string FileName => Conflict.FileName;

    public PendingConflict Conflict { get; }
    public string SessionName => Conflict.SessionName;
    public string Description { get; }

    /// <summary>The raw per-side states mutagen reported, shown in small
    /// print under the readable <see cref="Description"/>.</summary>
    public string Details { get; }

    /// <summary>Fetched when the dialog opens (a stat can mean an SSH round trip).</summary>
    public FileStat? AlphaStat { get; private set; }
    public FileStat? BetaStat { get; private set; }

    /// <summary>The texts the Resolution cell offers. "Visual merge" is only there when both sides are
    /// actual files (not a directory, both existing).</summary>
    public IReadOnlyList<string> ChoiceOptions { get; private set; }

    /// <summary>The local copies a visual merge was run on and whose modification was detected; the merged
    /// result is only propagated when the dialog's OK is clicked.</summary>
    public VisualMergePreparation? MergePreparation { get; set; }

    /// <summary>True when the stat or the application of the choice failed: the error is shown in the row.</summary>
    public bool HasError => _error.Length > 0;

    /// <summary>True once the choice has been applied: the row can no longer be edited.</summary>
    public bool IsResolved
    {
        get => _isResolved;
        private set
        {
            if (_isResolved == value)
                return;
            _isResolved = value;
            Notify(nameof(IsResolved));
            Notify(nameof(CanEdit));
        }
    }

    /// <summary>False once the stat failed or the choice was applied.</summary>
    public bool CanEdit => !IsResolved && Choice is not null;

    /// <summary>The choice currently picked; null until the stats are known (or when they failed).</summary>
    public ConflictResolutionChoice? Choice => _choice;

    /// <summary>The choice that was picked before the last change by the user, to go back to it.</summary>
    public ConflictResolutionChoice? PreviousChoice { get; private set; }

    /// <summary>Raised when the user picks another option in the Resolution cell.</summary>
    public event EventHandler<ConflictResolutionChoice>? ChoiceChangedByUser;

    /// <summary>Two-way bound to the Resolution cell's combo.</summary>
    public string? SelectedChoiceText
    {
        get => _choice is { } choice ? Describe(choice) : null;
        set
        {
            var choice = Parse(value);
            if (choice is null || choice == _choice)
                return;

            PreviousChoice = _choice;
            _choice = choice;
            Notify(nameof(Choice));
            Notify(nameof(SelectedChoiceText));
            ChoiceChangedByUser?.Invoke(this, choice.Value);
        }
    }

    /// <summary>Text shown instead of the combo when the row cannot be edited (failed or resolved).</summary>
    public string StatusText => _error.Length > 0 ? $"Error: {_error}" : _isResolved && _choice is { } choice ? $"{Describe(choice)} (resolved)" : "";

    public bool ShowStatusText => !CanEdit;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stores the stats and the default choice (FR-9.3: the more recently modified side, "Beta wins" on a tie).</summary>
    public void SetStats(FileStat alpha, FileStat beta)
    {
        AlphaStat = alpha;
        BetaStat = beta;
        var canMerge = !alpha.IsDirectory && !beta.IsDirectory && alpha.Exists && beta.Exists;
        ChoiceOptions = canMerge ? [AlphaWinsText, BetaWinsText, VisualMergeText] : [AlphaWinsText, BetaWinsText];
        _choice = alpha.ModifiedUtc > beta.ModifiedUtc ? ConflictResolutionChoice.AWins : ConflictResolutionChoice.BWins;
        NotifyAll();
    }

    /// <summary>Puts the combo back on <paramref name="choice"/> without raising <see cref="ChoiceChangedByUser"/>
    /// (a merge tool that changed nothing).</summary>
    public void RestoreChoice(ConflictResolutionChoice choice)
    {
        _choice = choice;
        Notify(nameof(Choice));
        Notify(nameof(SelectedChoiceText));
    }

    public void MarkResolved()
    {
        _error = "";
        IsResolved = true;
        Notify(nameof(StatusText));
        Notify(nameof(ShowStatusText));
    }

    public void MarkFailed(string message)
    {
        _error = message;
        _choice = null;
        NotifyAll();
    }

    private void NotifyAll()
    {
        Notify(nameof(Choice));
        Notify(nameof(SelectedChoiceText));
        Notify(nameof(ChoiceOptions));
        Notify(nameof(CanEdit));
        Notify(nameof(StatusText));
        Notify(nameof(ShowStatusText));
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string Describe(ConflictResolutionChoice choice) => choice switch
    {
        ConflictResolutionChoice.VisualMerge => VisualMergeText,
        ConflictResolutionChoice.AWins => AlphaWinsText,
        _ => BetaWinsText,
    };

    private static ConflictResolutionChoice? Parse(string? text) => text switch
    {
        VisualMergeText => ConflictResolutionChoice.VisualMerge,
        AlphaWinsText => ConflictResolutionChoice.AWins,
        BetaWinsText => ConflictResolutionChoice.BWins,
        _ => null,
    };
}
