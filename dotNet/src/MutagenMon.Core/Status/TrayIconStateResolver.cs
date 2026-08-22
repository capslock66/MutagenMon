namespace MutagenMon.Core.Status;

/// <summary>Everything the tray icon's decision table needs as input — see
/// requirements/03-tray-icon-requirements.md §3.</summary>
public readonly record struct TrayIconInput(
    SessionStatusCode WorstCode,
    bool Enabled,
    bool ProfileJustUpdated,
    StalenessTier Staleness);

/// <summary>IconKey is one of the 15 basenames under requirements/icons/
/// (e.g. "green-success", "green-timeout-red") — the App layer maps it to an
/// actual bitmap; Core knows nothing about bitmaps.</summary>
public readonly record struct TrayIconState(string IconKey, string Tooltip);

/// <summary>
/// Direct implementation of the full icon-state decision table in
/// requirements/03-tray-icon-requirements.md §3 — ports
/// mutagenmonlib/wx/icon.py: TaskBarIcon.update_icon(). Per that document's
/// §7 guidance ("fix by default unless there's a reason to preserve legacy
/// parity" — none was raised), two documented quirks are fixed here rather
/// than replicated:
///   1. "ready + updated" is reachable for every base state (Ready/Syncing/
///      Scanning), not just Syncing/Scanning as in the legacy branching.
///   2. Stale tooltips keep the underlying state's own description instead
///      of always falling back to the generic "watching for changes
///      (stale)" text.
/// De-duplicating repeated icon/tooltip updates (§7 item 4) is a
/// presentation concern handled by the App layer's TrayIconController, not
/// here.
/// </summary>
public static class TrayIconStateResolver
{
    public static TrayIconState Resolve(TrayIconInput input, string appName)
    {
        var prefix = appName + ": ";

        switch (input.WorstCode)
        {
            case SessionStatusCode.Unknown:
                return new TrayIconState("lightgray-init", prefix + "waiting for status...");

            case SessionStatusCode.NotRunning:
                return input.Enabled
                    ? new TrayIconState("darkgray-restart", prefix + "mutagen is not running (starting)")
                    : new TrayIconState("darkgray", prefix + "mutagen is not running (disabled)");

            case SessionStatusCode.ConnectionError:
                return input.Enabled
                    ? new TrayIconState("orange-restart", prefix + "error (starting)")
                    : new TrayIconState("orange", prefix + "error (disabled)");
        }

        // From here on: Ready / Conflicts / Problems / Syncing / Scanning.

        if (input.WorstCode == SessionStatusCode.Ready && !input.Enabled)
        {
            // Disabled/stopping takes priority over both staleness and "updated" —
            // matches the legacy's unconditional green-stop for this combination.
            return new TrayIconState("green-stop", prefix + "mutagen is stopping");
        }

        var baseDescription = BaseDescription(input.WorstCode);

        switch (input.Staleness)
        {
            case StalenessTier.Error:
                return new TrayIconState("green-timeout-red", prefix + baseDescription + " (stale)");
            case StalenessTier.Warning:
                return new TrayIconState("green-timeout", prefix + baseDescription + " (stale)");
            case StalenessTier.Info:
                return new TrayIconState("green-timeout-white", prefix + baseDescription + " (stale)");
        }

        if (input.ProfileJustUpdated && IsUpdateFlashEligible(input.WorstCode))
        {
            return new TrayIconState("green-success", prefix + baseDescription + " (updated)");
        }

        return new TrayIconState(BaseIconKey(input.WorstCode), prefix + baseDescription);
    }

    private static bool IsUpdateFlashEligible(SessionStatusCode code) =>
        code is SessionStatusCode.Ready or SessionStatusCode.Syncing or SessionStatusCode.Scanning;

    private static string BaseDescription(SessionStatusCode code) => code switch
    {
        SessionStatusCode.Ready => "mutagen is watching for changes",
        SessionStatusCode.Conflicts => "conflicts",
        SessionStatusCode.Problems => "problems",
        SessionStatusCode.Syncing => "mutagen is syncing",
        SessionStatusCode.Scanning => "mutagen is scanning",
        _ => "mutagen is watching for changes",
    };

    private static string BaseIconKey(SessionStatusCode code) => code switch
    {
        SessionStatusCode.Ready => "green",
        SessionStatusCode.Conflicts => "green-conflict",
        SessionStatusCode.Problems => "green-error",
        SessionStatusCode.Syncing => "green-sync",
        SessionStatusCode.Scanning => "green-scan",
        _ => "green",
    };
}
