using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Infrastructure.Logging;

public sealed record LogRecord(DateTimeOffset TimestampUtc, LogLevel Level, string Category, string Message);

/// <summary>Bounded in-memory log ring feeding the UI log viewer (already redacted).</summary>
public sealed class LogBuffer
{
    private readonly ConcurrentQueue<LogRecord> _queue = new();
    private readonly int _capacity;

    public LogBuffer(int capacity = 5000)
    {
        _capacity = capacity;
    }

    public event EventHandler<LogRecord>? Appended;

    public IReadOnlyList<LogRecord> Snapshot() => _queue.ToArray();

    public void Append(LogRecord record)
    {
        _queue.Enqueue(record);
        while (_queue.Count > _capacity && _queue.TryDequeue(out _))
        {
        }

        Appended?.Invoke(this, record);
    }
}

public sealed class LoggingOptions
{
    public string Directory { get; set; } = string.Empty;
    public string FilePrefix { get; set; } = "jackivpn";
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    public int RetainDays { get; set; } = 7;
    public long MaxFileBytes { get; set; } = 20 * 1024 * 1024;
}

/// <summary>
/// Daily rolling file logger. <b>Every</b> message, exception and scope passes through
/// <see cref="SecretRedactor"/> before reaching disk or the UI buffer — redaction cannot be bypassed by
/// a careless log call.
/// </summary>
public sealed class RedactingFileLoggerProvider : ILoggerProvider
{
    private readonly LoggingOptions _options;
    private readonly LogBuffer? _buffer;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 50_000);
    private readonly Thread _writer;
    private bool _disposed;

    public RedactingFileLoggerProvider(LoggingOptions options, LogBuffer? buffer = null)
    {
        _options = options;
        _buffer = buffer;
        System.IO.Directory.CreateDirectory(options.Directory);
        Prune();
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "JackiVpn log writer" };
        _writer.Start();
    }

    public LogLevel MinimumLevel
    {
        get => _options.MinimumLevel;
        set => _options.MinimumLevel = value;
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = DateTimeOffset.UtcNow;
        var text = SecretRedactor.Redact(message);
        if (exception is not null)
        {
            text += " | " + SecretRedactor.Redact(exception.GetType().Name + ": " + exception.Message);
            if (level >= LogLevel.Error && exception.StackTrace is { } st)
            {
                text += Environment.NewLine + SecretRedactor.Redact(st);
            }
        }

        var shortCategory = category.Contains('.', StringComparison.Ordinal) ? category[(category.LastIndexOf('.') + 1)..] : category;
        _buffer?.Append(new LogRecord(now, level, shortCategory, text));
        var line = string.Create(CultureInfo.InvariantCulture, $"{now:yyyy-MM-dd HH:mm:ss.fff}Z [{Abbrev(level)}] {shortCategory}: {text}");
        if (!_disposed)
        {
            _queue.TryAdd(line);
        }
    }

    private void WriterLoop()
    {
        StreamWriter? writer = null;
        string? currentDay = null;
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                var day = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (day != currentDay || writer is null || writer.BaseStream.Length > _options.MaxFileBytes)
                {
                    writer?.Dispose();
                    var path = NextPath(day);
                    writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = false };
                    currentDay = day;
                }

                writer.WriteLine(line);
                if (_queue.Count == 0)
                {
                    writer.Flush();
                }
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private string NextPath(string day)
    {
        var basePath = Path.Combine(_options.Directory, $"{_options.FilePrefix}-{day}.log");
        if (!File.Exists(basePath) || new FileInfo(basePath).Length < _options.MaxFileBytes)
        {
            return basePath;
        }

        for (var i = 1; i < 100; i++)
        {
            var p = Path.Combine(_options.Directory, $"{_options.FilePrefix}-{day}.{i}.log");
            if (!File.Exists(p) || new FileInfo(p).Length < _options.MaxFileBytes)
            {
                return p;
            }
        }

        return basePath;
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, _options.RetainDays));
        foreach (var f in System.IO.Directory.EnumerateFiles(_options.Directory, _options.FilePrefix + "-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(f) < cutoff)
                {
                    File.Delete(f);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static string Abbrev(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(3));
        _queue.Dispose();
    }

    private sealed class Logger(RedactingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider.MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);
            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
