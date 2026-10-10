using System.ComponentModel;
using JackiVpn.Mobile.Controls;
using JackiVpn.Mobile.ViewModels;

namespace JackiVpn.Mobile.Pages;

/// <summary>"Sync device": scans as soon as it opens; the radar sweeps and pulses while the scan runs.</summary>
public partial class EcosystemPage : AnimatedPage
{
    private const string RadarAnimation = "radar";
    private readonly EcosystemViewModel _vm;

    public EcosystemPage(EcosystemViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _vm.PropertyChanged += OnChanged;
        try
        {
            await _vm.LoadAsync();
            await _vm.ScanAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _vm.Message = "Could not scan: " + Security.SecretRedactor.Redact(ex.Message);
        }
    }

    protected override void OnDisappearing()
    {
        _vm.PropertyChanged -= OnChanged;
        _vm.CancelScan();
        StopRadar();
        base.OnDisappearing();
    }

    private async void OnScanTapped(object? sender, TappedEventArgs e)
    {
        _ = Motion.TapAsync(Core);
        try
        {
            await _vm.ScanAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _vm.Message = "Could not scan: " + Security.SecretRedactor.Redact(ex.Message);
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EcosystemViewModel.IsScanning):
                if (_vm.IsScanning)
                {
                    StartRadar();
                }
                else
                {
                    StopRadar();
                }

                break;
            case nameof(EcosystemViewModel.HasDevices) when _vm.HasDevices && !Motion.Reduced:
                // A device just answered: the core pops.
                _ = PopAsync();
                break;
            case nameof(EcosystemViewModel.IsSending) when _vm.IsSending:
                _ = Motion.EnterAsync(CodeCard, 0, 12);
                break;
        }
    }

    private async Task PopAsync()
    {
        await Core.ScaleToAsync(1.12, 140, Easing.CubicOut);
        await Core.ScaleToAsync(1, 420, Easing.SpringOut);
    }

    private void StartRadar()
    {
        if (Motion.Reduced || this.AnimationIsRunning(RadarAnimation))
        {
            return;
        }

        _ = Sweep.FadeToAsync(1, 250);
        var rings = new[] { Ring1, Ring2, Ring3 };
        var parent = new Animation { { 0, 1, new Animation(v => Sweep.Rotation = v, 0, 360) } };
        for (var i = 0; i < rings.Length; i++)
        {
            var ring = rings[i];
            var start = i * 0.22;
            parent.Add(start, Math.Min(1, start + 0.56), new Animation(v =>
            {
                ring.Scale = 0.45 + (0.65 * v);
                ring.Opacity = 0.7 * (1 - v);
            }, 0, 1, Easing.CubicOut));
        }

        parent.Commit(this, RadarAnimation, 16, 2000, Easing.Linear, repeat: () => _vm.IsScanning);
    }

    private void StopRadar()
    {
        this.AbortAnimation(RadarAnimation);
        _ = Sweep.FadeToAsync(0, Motion.Ms(250));
        foreach (var ring in new[] { Ring1, Ring2, Ring3 })
        {
            _ = ring.FadeToAsync(0, Motion.Ms(250));
        }
    }
}
