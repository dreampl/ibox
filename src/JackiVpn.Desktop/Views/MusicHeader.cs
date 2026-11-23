using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Input;

namespace JackiVpn.Desktop.Views;

public sealed class MusicHeader : Border
{
    private readonly TextBlock _title;
    private readonly Button _pause;
    private readonly Button _open;
    private readonly ProgressBar _progress;
    public MusicHeader()
    {
        Height = 44; Width = 44; CornerRadius = new CornerRadius(12); ClipToBounds = true;
        SetResourceReference(BackgroundProperty, "B.CardFill");
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var open = _open = new Button { Content = "\uE8D6", ToolTip = "Music", Width = 44, Height = 44 };
        open.SetResourceReference(StyleProperty, "Button.Round");
        open.SetBinding(Button.CommandProperty, new System.Windows.Data.Binding("OpenMusicCommand"));
        row.Children.Add(open);
        _title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4,0,8,0), Cursor = Cursors.Hand };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
        _title.MouseLeftButtonUp += (_, _) => { if (DataContext is ViewModels.MainViewModel vm) { vm.OpenMusicCommand.Execute(null); } };
        Grid.SetColumn(_title, 1); row.Children.Add(_title);
        _pause = new Button { Content = "\uE769", ToolTip = "Pause music", Width = 36, Height = 36, Margin = new Thickness(0,0,4,0) };
        _pause.SetResourceReference(StyleProperty, "Button.Icon");
        _pause.Click += async (_, _) => await MiniAppWindow.TogglePlaybackAsync();
        Grid.SetColumn(_pause, 2); row.Children.Add(_pause);
        _progress = new ProgressBar { Height = 2, Margin = new Thickness(12, 0, 12, 5), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Bottom, Maximum = 1, IsHitTestVisible = false, BorderThickness = new Thickness(0) };
        _progress.SetResourceReference(ProgressBar.ForegroundProperty, "B.Accent");
        Grid.SetColumnSpan(_progress, 3);
        row.Children.Add(_progress);
        Child = row;
        Loaded += OnLoaded;
        Unloaded += (_, _) => { MiniAppWindow.PlaybackChanged -= Update; MiniAppWindow.PlaybackProgress -= Progress; };
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MiniAppWindow.PlaybackChanged -= Update;
        MiniAppWindow.PlaybackChanged += Update;
        MiniAppWindow.PlaybackProgress -= Progress;
        MiniAppWindow.PlaybackProgress += Progress;
        Update(MiniAppWindow.Playing, MiniAppWindow.TrackTitle);
    }
    private void Progress(double position, double duration)
    {
        _progress.Value = duration > 0 ? Math.Clamp(position / duration, 0, 1) : 0;
        ToolTip = $@"{MiniAppWindow.TrackTitle} · {TimeSpan.FromSeconds(Math.Max(0, position)):m\:ss} / {TimeSpan.FromSeconds(Math.Max(0, duration)):m\:ss}";
        System.Windows.Automation.AutomationProperties.SetHelpText(_open, ToolTip.ToString());
    }
    private void Update(bool playing, string title)
    {
        _title.Text = title;
        _progress.Visibility = _title.Visibility = _pause.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        var target = playing ? 260d : 44d;
        if (Motion.Reduced) { BeginAnimation(WidthProperty, null); Width = target; }
        else { BeginAnimation(WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }); }
    }
}
