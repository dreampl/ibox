using JackiVpn.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Notifications;

public sealed record AppNotification(NotificationCategory Category, string Title, string Message);

/// <summary>Platform toast sink (WinRT toasts in the desktop; a no-op/log sink elsewhere).</summary>
public interface INotificationSink
{
    void Show(AppNotification notification);
}

/// <summary>Applies per-category user preferences, de-duplicates bursts and forwards to the sink.</summary>
public sealed class NotificationService(INotificationSink sink, Func<NotificationSettings> settings, ILogger logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public static TimeSpan DuplicateWindow { get; } = TimeSpan.FromSeconds(30);

    public event EventHandler<AppNotification>? Raised;

    public bool Notify(NotificationCategory category, string title, string message)
    {
        if (!settings().IsEnabled(category))
        {
            return false;
        }

        var key = $"{category}|{title}|{message}";
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_recent.TryGetValue(key, out var last) && now - last < DuplicateWindow)
            {
                return false;
            }

            _recent[key] = now;
        }

        var n = new AppNotification(category, title, Security.SecretRedactor.Redact(message));
        try
        {
            sink.Show(n);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogDebug("Notification sink failed: {Error}", ex.Message);
        }

        Raised?.Invoke(this, n);
        return true;
    }
}

public sealed class LogNotificationSink(ILogger logger) : INotificationSink
{
    public void Show(AppNotification notification) => logger.LogInformation("[notify:{Category}] {Title}: {Message}", notification.Category, notification.Title, notification.Message);
}
