using System;
using System.Collections.Generic;
using System.Linq;
using JackiVpn.Desktop.Composition;
using JackiVpn.Desktop.Services;
using JackiVpn.Domain.Settings;
using JackiVpn.Application.Settings;

namespace JackiVpn.Desktop.ViewModels;

public sealed class NotificationToggle(NotificationCategory category, string label, SettingsStore store)
{
    public string Label { get; } = label;

    public bool Enabled
    {
        get => store.Current.Notifications.IsEnabled(category);
        set => store.Update(s => s with
        {
            Notifications = s.Notifications with
            {
                Disabled = value
                    ? s.Notifications.Disabled.Where(c => c != category).ToArray()
                    : s.Notifications.Disabled.Append(category).Distinct().ToArray(),
            },
        });
    }
}

public sealed class SettingsViewModel : SettingsPageViewModel
{
    private readonly AppServices _s;

    public SettingsViewModel(AppServices services)
        : base(services.Settings)
    {
        _s = services;
        Notifications =
        [
            new(NotificationCategory.Connected, "Connected", Store),
            new(NotificationCategory.Disconnected, "Disconnected", Store),
            new(NotificationCategory.ConnectionFailed, "Connection failed", Store),
            new(NotificationCategory.AutomaticFailover, "Automatic server switch", Store),
            new(NotificationCategory.SubscriptionUpdated, "Subscription updated", Store),
            new(NotificationCategory.SubscriptionExpired, "Subscription expired", Store),
            new(NotificationCategory.TrafficAlmostFinished, "Traffic almost used up", Store),
            new(NotificationCategory.CoreCrashed, "Core crashed", Store),
            new(NotificationCategory.KillSwitchActivated, "Kill switch activated", Store),
            new(NotificationCategory.UpdateAvailable, "Update available", Store),
        ];
    }

    public IReadOnlyList<AppTheme> Themes { get; } = [AppTheme.System, AppTheme.Light, AppTheme.Dark];

    public IReadOnlyList<AppLogLevel> LogLevels { get; } = Choices.Of<AppLogLevel>();

    public IReadOnlyList<NotificationToggle> Notifications { get; }

    // ------------------------------------------------------------------ notch

    private void SetNotch(Func<NotchSettings, NotchSettings> change) => Set(s => s with { Ui = s.Ui with { Notch = change(s.Ui.Notch) } });

    public bool NotchEnabled { get => S.Ui.Notch.Enabled; set => SetNotch(n => n with { Enabled = value }); }

    public bool NotchShowStatus { get => S.Ui.Notch.ShowStatus; set => SetNotch(n => n with { ShowStatus = value }); }

    public bool NotchShowPing { get => S.Ui.Notch.ShowPing; set => SetNotch(n => n with { ShowPing = value }); }

    public bool NotchShowTunnel { get => S.Ui.Notch.ShowTunnel; set => SetNotch(n => n with { ShowTunnel = value }); }

    public bool NotchShowServer { get => S.Ui.Notch.ShowServer; set => SetNotch(n => n with { ShowServer = value }); }

    public bool NotchShowCountry { get => S.Ui.Notch.ShowCountry; set => SetNotch(n => n with { ShowCountry = value }); }

    public bool NotchShowDuration { get => S.Ui.Notch.ShowDuration; set => SetNotch(n => n with { ShowDuration = value }); }

    public bool NotchHideInFullScreen { get => S.Ui.Notch.HideInFullScreen; set => SetNotch(n => n with { HideInFullScreen = value }); }

    public bool NotchSound
    {
        get => S.Ui.Notch.Sound;
        set
        {
            SetNotch(n => n with { Sound = value });
            if (value)
            {
                Services.Chime.PlayConnected();
            }
        }
    }

    public int NotchFadeAfterSeconds { get => S.Ui.Notch.FadeAfterSeconds; set => SetNotch(n => n with { FadeAfterSeconds = Clamp(value, 0, 120) }); }

    public IReadOnlyList<int> NotchFadeChoices { get; } = [0, 3, 6, 10, 20, 30, 60];

    public AppTheme Theme
    {
        get => S.Ui.Theme;
        set
        {
            Set(s => s with { Ui = s.Ui with { Theme = value } });
            ThemeManager.Apply(value);
        }
    }

    public bool CloseToTray
    {
        get => S.Ui.CloseToTray;
        set => Set(s => s with { Ui = s.Ui with { CloseToTray = value } });
    }

    public bool ReduceMotion
    {
        get => S.Ui.ReduceMotion;
        set => Set(s => s with { Ui = s.Ui with { ReduceMotion = value } });
    }

    public bool StartWithWindows
    {
        get => S.Startup.StartWithWindows;
        set
        {
            StartupManager.Set(value);
            Set(s => s with { Startup = s.Startup with { StartWithWindows = value } });
        }
    }

    public bool StartMinimized
    {
        get => S.Startup.StartMinimized;
        set => Set(s => s with { Startup = s.Startup with { StartMinimized = value } });
    }

    public bool AutoConnect
    {
        get => S.Startup.AutoConnect;
        set => Set(s => s with { Startup = s.Startup with { AutoConnect = value } });
    }

    public int TrafficWarningPercent
    {
        get => S.Notifications.TrafficWarningPercent;
        set => Set(s => s with { Notifications = s.Notifications with { TrafficWarningPercent = Clamp(value, 50, 100) } });
    }

    public AppLogLevel LogLevel
    {
        get => S.Logging.MinimumLevel;
        set
        {
            Set(s => s with { Logging = s.Logging with { MinimumLevel = value } });
            _s.ApplyLogLevel(value);
        }
    }

    public int RetainDays
    {
        get => S.Logging.RetainDays;
        set => Set(s => s with { Logging = s.Logging with { RetainDays = Clamp(value, 1, 90) } });
    }

    /// <summary>Telemetry stays off: nothing is collected or sent. Shown for transparency only.</summary>
    public bool Telemetry => S.Privacy.TelemetryEnabled;

    public string DataFolder => _s.Paths.UserDataRoot;

    public void OpenDataFolder() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_s.Paths.UserDataRoot}\"") { UseShellExecute = false });
}
