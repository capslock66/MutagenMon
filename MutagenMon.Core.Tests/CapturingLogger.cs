using Microsoft.Extensions.Logging;

namespace MutagenMon.Core.Tests;

/// <summary>Records every formatted log message instead of writing anywhere,
/// so tests can assert on logged content without touching the filesystem.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public readonly List<string> Messages = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}
