using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Retweak.Service.Infra;

/// <summary>
/// A deliberately small size-rolling file logger. The service must be able to say why it
/// failed before the Event Log source exists, and before any third-party logging package
/// has been configured, so this has no dependencies beyond the logging abstractions.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>Floor for the configured max file size, so a bad config can't roll on every write.</summary>
    private const long MinLogFileBytes = 64 * 1024;

    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly string _baseName;
    private readonly long _maxBytes;
    private readonly int _retain;

    public FileLoggerProvider(string directory, string baseName, long maxBytes, int retain)
    {
        _directory = Environment.ExpandEnvironmentVariables(directory);
        _baseName = baseName;
        _maxBytes = Math.Max(MinLogFileBytes, maxBytes);
        _retain = Math.Max(1, retain);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    internal void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                string path = Path.Combine(_directory, _baseName + ".log");

                var info = new FileInfo(path);
                if (info.Exists && info.Length >= _maxBytes)
                {
                    Roll(path);
                }

                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // If we cannot even write the fallback log there is nowhere left to
                // complain to. Drop this line and let the next write try again, so a
                // transient failure (e.g. a momentary AV lock) does not silence logging
                // for the rest of the process lifetime.
            }
        }
    }

    private void Roll(string path)
    {
        var oldest = $"{path}.{_retain}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int i = _retain - 1; i >= 1; i--)
        {
            var from = $"{path}.{i}";
            if (File.Exists(from))
            {
                File.Move(from, $"{path}.{i + 1}", overwrite: true);
            }
        }

        File.Move(path, $"{path}.1", overwrite: true);
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var sb = new StringBuilder(256);
            sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
              .Append(" [").Append(Abbreviate(logLevel)).Append("] ")
              .Append(ShortCategory(_category)).Append(": ")
              .Append(formatter(state, exception));

            if (exception is not null)
            {
                sb.AppendLine().Append(exception);
            }

            _provider.Write(sb.ToString());
        }

        private static string ShortCategory(string category)
        {
            int i = category.LastIndexOf('.');
            return i >= 0 && i < category.Length - 1 ? category[(i + 1)..] : category;
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none",
        };
    }
}
