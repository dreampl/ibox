using JackiVpn.Mobile.Controls;
using JackiVpn.Mobile.Localization;
using JackiVpn.Mobile.ViewModels;

namespace JackiVpn.Mobile.Views;

/// <summary>Servers grouped by subscription, with search, segments (All / Official / Favorites) and auto selection.</summary>
public partial class ServersView : ContentView
{
    private readonly ServersViewModel _vm;

    public ServersView(ServersViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        List.ItemTemplate = new DataTemplate(() => new ServerCell(
            i => vm.UseCommand.ExecuteAsync(i),
            i => vm.MoreCommand.ExecuteAsync(i),
            i => vm.FavoriteCommand.ExecuteAsync(i)));
        List.GroupHeaderTemplate = new DataTemplate(() => new ServerGroupHeader());
    }

    public async Task AppearAsync()
    {
        try
        {
            await _vm.LoadAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _vm.Message = Security.SecretRedactor.Redact(ex.Message);
        }
    }

    private async void OnTestTapped(object? sender, TappedEventArgs e)
    {
        _ = Motion.StretchAsync(TestButton);
        await _vm.TestAllCommand.ExecuteAsync(null);
    }
}

/// <summary>Server picker drawer (Home → Change): "Fastest automatically" on top, then the grouped list.</summary>
public sealed class ServerPicker : ContentView
{
    public ServerPicker(ServersViewModel servers, HomeViewModel home)
    {
        var auto = new Border
        {
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            StrokeThickness = 1,
            Stroke = Rtl.Res("Stroke"),
            BackgroundColor = home.SmartMode ? Color.FromArgb("#F2F5FF") : Rtl.Res("Surface2"),
            Padding = new Thickness(12, 12),
            Margin = new Thickness(0, 0, 0, 6),
        };
        var autoGrid = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 12 };
        autoGrid.Add(new Border
        {
            WidthRequest = 40,
            HeightRequest = 40,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 20 },
            StrokeThickness = 0,
            Background = (Brush)Microsoft.Maui.Controls.Application.Current!.Resources["PrimaryGradient"],
            Content = new Icon { Name = "bolt", Size = 20 },
        }, 0, 0);
        autoGrid.Add(new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = L.Get("Home_AutoServer"), FontFamily = "OpenSansSemibold", FontSize = 15, TextColor = home.SmartMode ? Color.FromArgb("#0B1220") : Rtl.Res("Text") },
                new Label { Text = L.Get("Home_AutoServerDetail"), FontSize = 12, TextColor = home.SmartMode ? Color.FromArgb("#4B5675") : Rtl.Res("Text3") },
            },
        }, 1, 0);
        auto.Content = autoGrid;
        var autoTap = new TapGestureRecognizer();
        autoTap.Tapped += async (_, _) =>
        {
            await Motion.TapAsync(auto);
            home.SmartMode = true;
            await Sheets.HideAsync();
        };
        auto.GestureRecognizers.Add(autoTap);

        var list = new CollectionView
        {
            ItemsSource = servers.Groups,
            IsGrouped = true,
            SelectionMode = SelectionMode.None,
            HeightRequest = DeviceDisplay.Current.MainDisplayInfo.Height / DeviceDisplay.Current.MainDisplayInfo.Density * 0.55,
            ItemTemplate = new DataTemplate(() => new ServerCell(
                async i =>
                {
                    await Sheets.HideAsync();
                    await servers.UseCommand.ExecuteAsync(i);
                },
                i => servers.MoreCommand.ExecuteAsync(i),
                i => servers.FavoriteCommand.ExecuteAsync(i))),
            GroupHeaderTemplate = new DataTemplate(() => new ServerGroupHeader()),
        };
        Content = new VerticalStackLayout { Padding = new Thickness(16, 0), Children = { auto, list } };
    }
}
