using System.Collections.ObjectModel;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using CommunityToolkit.Mvvm.ComponentModel;
using JackiVpn.Domain.Settings;
using JackiVpn.Mobile.Services;

namespace JackiVpn.Mobile.ViewModels;

public sealed partial class AppItem(string label, string package, ImageSource? icon, bool selected, Action<AppItem> changed) : ObservableObject
{
    [ObservableProperty] private bool _isSelected = selected;

    public string Label { get; } = label;

    public string Package { get; } = package;

    public ImageSource? Icon { get; } = icon;

    partial void OnIsSelectedChanged(bool value) => changed(this);
}

/// <summary>Picks the apps for per-app VPN routing (launchable apps only; changes are saved immediately).</summary>
public sealed partial class AppsViewModel(MobileServices services) : ObservableObject
{
    private List<AppItem> _all = [];

    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _header = string.Empty;

    public ObservableCollection<AppItem> Items { get; } = [];

    partial void OnFilterChanged(string value) => ApplyFilter();

    public async Task LoadAsync()
    {
        IsLoading = true;
        var split = services.Settings.Current.SplitTunnel;
        Header = split.Mode switch
        {
            SplitTunnelMode.OnlySelected => "Only the selected apps use the VPN.",
            SplitTunnelMode.ExcludeSelected => "The selected apps bypass the VPN.",
            _ => "All apps use the VPN. Choose a mode in Settings to use this list.",
        };
        var selected = split.Packages.ToHashSet(StringComparer.Ordinal);
        _all = await Task.Run(() => Query(selected));
        ApplyFilter();
        IsLoading = false;
    }

    private List<AppItem> Query(HashSet<string> selected)
    {
        var pm = services.Context.PackageManager!;
        var main = new Intent(Intent.ActionMain).AddCategory(Intent.CategoryLauncher);
        var own = services.Context.PackageName;
        var launchable = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? pm.QueryIntentActivities(main, PackageManager.ResolveInfoFlags.Of(0))
#pragma warning disable CA1422 // the int overload is the only one before Android 13
            : pm.QueryIntentActivities(main, 0);
#pragma warning restore CA1422
        return launchable
            .Select(r => r.ActivityInfo?.ApplicationInfo)
            .OfType<ApplicationInfo>()
            .Where(a => a.PackageName is { } p && p != own)
            .DistinctBy(a => a.PackageName)
            .Select(a => new AppItem(a.LoadLabel(pm)?.ToString() ?? a.PackageName!, a.PackageName!, IconOf(a.LoadIcon(pm)), selected.Contains(a.PackageName!), Changed))
            .OrderByDescending(a => a.IsSelected)
            .ThenBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static ImageSource? IconOf(Drawable? drawable)
    {
        if (drawable is null)
        {
            return null;
        }

        using var bitmap = Bitmap.CreateBitmap(96, 96, Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bitmap);
        drawable.SetBounds(0, 0, 96, 96);
        drawable.Draw(canvas);
        using var ms = new MemoryStream();
        bitmap.Compress(Bitmap.CompressFormat.Png!, 100, ms);
        var bytes = ms.ToArray();
        return ImageSource.FromStream(() => new MemoryStream(bytes));
    }

    private void Changed(AppItem item)
    {
        var packages = _all.Where(a => a.IsSelected).Select(a => a.Package).ToArray();
        services.Settings.Update(s => s with { SplitTunnel = s.SplitTunnel with { Packages = packages } });
    }

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var a in _all.Where(a => Filter.Length == 0 || a.Label.Contains(Filter, StringComparison.CurrentCultureIgnoreCase) || a.Package.Contains(Filter, StringComparison.OrdinalIgnoreCase)))
        {
            Items.Add(a);
        }
    }
}
