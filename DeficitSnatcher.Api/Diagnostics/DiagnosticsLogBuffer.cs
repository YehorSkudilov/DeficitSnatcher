using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DeficitSnatcher.Api.Diagnostics;

public sealed record DiagnosticLog(DateTimeOffset At, string Source, string Level, string Message);

/// <summary>A bounded, in-memory copy of recent API and browser-service logs for the authenticated Diagnostics page.</summary>
public sealed partial class DiagnosticsLogBuffer : ILoggerProvider
{
    private const int Capacity = 3000;
    private readonly ConcurrentQueue<DiagnosticLog> _entries = new();

    public ILogger CreateLogger(string categoryName) => new BufferLogger(this, categoryName);

    public void AddCamoufox(string message) => Add("Camoufox", "Information", message);

    public DiagnosticLog[] Recent(int limit = 1000)
    {
        var snapshot = _entries.ToArray();
        return snapshot.Skip(Math.Max(0, snapshot.Length - Math.Clamp(limit, 1, Capacity))).ToArray();
    }

    private void Add(string source, string level, string message)
    {
        message = Redact(message);
        if (message.Length > 6000)
            message = message[..6000] + " [truncated]";
        _entries.Enqueue(new(DateTimeOffset.UtcNow, source, level, message));
        while (_entries.Count > Capacity)
            _entries.TryDequeue(out _);
    }

    private static string Redact(string value)
    {
        value = CookieHeader().Replace(value, "$1=[REDACTED]");
        value = SensitiveAssignment().Replace(value, "$1=[REDACTED]");
        value = UrlQuery().Replace(value, "$1?[REDACTED]");
        value = Email().Replace(value, "[REDACTED_EMAIL]");
        value = LongNumber().Replace(value, "[REDACTED_NUMBER]");
        return value;
    }

    private sealed class BufferLogger(DiagnosticsLogBuffer owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var message = formatter(state, exception);
            if (exception != null)
                message += Environment.NewLine + exception;
            owner.Add("API/" + category, logLevel.ToString(), message);
        }
    }

    [GeneratedRegex("(?im)\\b(cookie|set-cookie|authorization)\\s*[:=][^\\r\\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex CookieHeader();

    [GeneratedRegex("(?i)\\b(password|passwd|token|secret|cvv|card(?:\\.number|number)?)\\s*[:=]\\s*[^\\s,;&]+", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignment();

    [GeneratedRegex("(?i)(https?://[^\\s?<>\\\"]+)\\?[^\\s<>\\\"]*", RegexOptions.CultureInvariant)]
    private static partial Regex UrlQuery();

    [GeneratedRegex("(?i)\\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex("(?<!\\d)(?:\\d[ -]?){13,19}(?!\\d)", RegexOptions.CultureInvariant)]
    private static partial Regex LongNumber();

    public void Dispose() { }
}
