using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CastBridge.App;

/// <summary>
/// Minimal file logger. A tray application has no console to print to, so when something goes wrong
/// at startup there would otherwise be nowhere to look.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _sync = new();

    public FileLoggerProvider(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        // Debug noise is not worth a file write on every step of a volume drag.
        if (level < LogLevel.Information)
            return;

        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] ")
                .Append(category).Append(": ").Append(message);

            if (exception is not null)
                line.AppendLine().Append(exception);

            lock (_sync)
                File.AppendAllText(_path, line.AppendLine().ToString());
        }
        catch (Exception)
        {
            // Logging must never be the reason something fails.
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}