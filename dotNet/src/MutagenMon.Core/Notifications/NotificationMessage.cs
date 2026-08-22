namespace MutagenMon.Core.Notifications;

/// <summary>One desktop notification (FR-11) queued by the background poller
/// for the UI thread to actually display via the tray icon.</summary>
public sealed record NotificationMessage(string Title, string Body);
