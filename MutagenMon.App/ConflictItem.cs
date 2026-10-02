using System.ComponentModel;
using MutagenMon.Core.Resolution;

namespace MutagenMon.App;

/// <summary>One row of <see cref="ConflictResolutionWindow"/>'s grid: a
/// pending conflict plus the user's (not yet applied, or already applied)
/// resolution. Raises <see cref="PropertyChanged"/> so the Resolution column
/// follows the user's choice live.</summary>
public sealed class ConflictItem : INotifyPropertyChanged
{
    private string _resolution = "";

    public ConflictItem(PendingConflict conflict, string description, string details)
    {
        Conflict = conflict;
        Description = description;
        Details = details;
    }

    public string FileName => Conflict.FileName;

    public PendingConflict Conflict { get; }
    public string SessionName => Conflict.SessionName;
    public string Description { get; }

    /// <summary>The raw per-side states mutagen reported, shown in small
    /// print under the readable <see cref="Description"/>.</summary>
    public string Details { get; }

    /// <summary>Fetched lazily, the first time the row is selected (a stat
    /// can mean an SSH round trip).</summary>
    public FileStat? AlphaStat { get; set; }
    public FileStat? BetaStat { get; set; }

    /// <summary>The choice currently picked for this row; null until the row
    /// has been selected once.</summary>
    public ConflictResolutionChoice? Choice { get; private set; }

    /// <summary>True once the choice has actually been applied — the row can
    /// then no longer be edited.</summary>
    public bool IsResolved { get; private set; }

    public string Resolution
    {
        get => _resolution;
        private set
        {
            if (_resolution == value)
                return;
            _resolution = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Resolution)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetPending(ConflictResolutionChoice choice)
    {
        Choice = choice;
        Resolution = $"{Describe(choice)} (pending)";
    }

    public void MarkResolved(ConflictResolutionChoice choice)
    {
        Choice = choice;
        IsResolved = true;
        Resolution = $"{Describe(choice)} (resolved)";
    }

    public void MarkFailed(string message) => Resolution = $"Error: {message}";

    private static string Describe(ConflictResolutionChoice choice) => choice switch
    {
        ConflictResolutionChoice.VisualMerge => "Visual merge",
        ConflictResolutionChoice.AWins => "Alpha wins",
        _ => "Beta wins",
    };
}
