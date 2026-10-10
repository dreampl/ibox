using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Mobile.Services;

/// <summary>
/// Mirrors informational and higher log lines to Android's logcat (tag "JackiVpn"), redacted like the log file,
/// so problems on a device can be diagnosed with adb without root.
/// </summary>
public sealed class LogcatLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var text = SecretRedactor.Redact($"{category}: {formatter(state, exception)}" + (exception is null ? string.Empty : " | " + exception.GetType().Name + ": " + exception.Message));
            switch (logLevel)
            {
                case LogLevel.Error or LogLevel.Critical:
                    Android.Util.Log.Error("JackiVpn", text);
                    break;
                case LogLevel.Warning:
                    Android.Util.Log.Warn("JackiVpn", text);
                    break;
                default:
                    Android.Util.Log.Info("JackiVpn", text);
                    break;
            }
        }
    }
}