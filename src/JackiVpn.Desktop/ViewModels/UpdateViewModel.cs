using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JackiVpn.Desktop.Composition;
using JackiVpn.Domain.Connection;
using JackiVpn.Infrastructure.Updates;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Desktop.ViewModels;

/// <summary>
/// App updates on Windows: the banner (new version, notes, Install / Skip) and the About page controls.
/// Installing downloads the MSI, verifies it against the signed checksum, disconnects, starts Windows Installer
/// (which asks for administrator approval) and closes the app so its files can be replaced.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly AppServices _s;

    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _dismissed;

    public UpdateViewModel(AppServices services)
    {
        _s = services;
        services.Updates.Changed += (_, _) => Ui.Post(Refresh);
        services.Settings.Changed += (_, _) => Ui.Post(() => OnPropertyChanged(nameof(AutoCheck)));
    }

    public string CurrentVersion => AppServices.CurrentVersion.ToString(3);

    public AppUpdate? Available => _s.Updates.Available;

    public bool HasUpdate => Available is not null;

    /// <summary>Banner: an update the user has not skipped or dismissed in this session.</summary>
    public bool ShowBanner => _s.Updates.ShouldOffer && !Dismissed;

    public string Title => Available is { } a ? $"IBOX {a.VersionText} is available" : "IBOX is up to date";

    public string Notes => Available?.Notes(persian: false) ?? string.Empty;

    public bool AutoCheck
    {
        get => _s.Settings.Current.Updates.AutoCheck;
        set => _s.Settings.Update(x => x with { Updates = x.Updates with { AutoCheck = value } });
    }

    public string LastCheckText => _s.Updates.LastCheckUtc is { } t
        ? $"Last checked {t.ToLocalTime():g}" + (_s.Updates.LastError is { } e ? $" — {e}" : string.Empty)
        : "Not checked yet";

    partial void OnDismissedChanged(bool value) => OnPropertyChanged(nameof(ShowBanner));

    private void Refresh()
    {
        OnPropertyChanged(nameof(Available));
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(ShowBanner));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(LastCheckText));
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsChecking)
        {
            return;
        }

        IsChecking = true;
        Status = "Checking…";
        try
        {
            var update = await Task.Run(() => _s.Updates.CheckNowAsync(_s.Lifetime));
            Status = update is null ? (_s.Updates.LastError ?? "You have the latest version.") : null;
            Dismissed = false;
        }
        finally
        {
            IsChecking = false;
            Refresh();
        }
    }

    [RelayCommand]
    private void Later() => Dismissed = true;

    [RelayCommand]
    private void Skip()
    {
        if (Available is { } a)
        {
            _s.Settings.Update(x => x with { Updates = x.Updates with { SkippedVersion = a.VersionText } });
        }

        Dismissed = true;
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Available is not { } update || IsDownloading)
        {
            return;
        }

        IsDownloading = true;
        Progress = 0;
        Status = "Downloading…";
        try
        {
            var dir = Path.Combine(_s.Paths.UserDataRoot, "updates");
            var progress = new Progress<double>(p => Progress = p);
            var file = await Task.Run(() => _s.Updates.Service.DownloadAsync(update, dir, progress, _s.Lifetime));

            // Hold the verified file open (read-only sharing) so nothing can swap it before Windows Installer reads it.
            var guard = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            Status = "Installing… Windows will ask for permission.";
            if (!ConnectionStateRules.IsIdle(_s.Orchestrator.State))
            {
                await _s.Orchestrator.DisconnectAsync();
            }

            Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{file}\" /passive /norestart") { UseShellExecute = true });
            _s.Logger.LogInformation("Installing update {Version}.", update.VersionText);
            await Task.Delay(TimeSpan.FromSeconds(4));
            guard.Dispose();
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is AppUpdateException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            Status = ex is System.ComponentModel.Win32Exception ? "Installation was cancelled." : SecretRedactor.Redact(ex.Message);
        }
        finally
        {
            IsDownloading = false;
        }
    }
}
