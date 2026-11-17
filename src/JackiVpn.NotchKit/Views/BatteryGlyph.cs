using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using JackiVpn.Domain.Bar;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// One Bluetooth device on the notch: its kind as an icon over a small battery whose fill glides to the level. A
/// charging battery breathes and a bolt pulses through it; a low one wobbles now and then and its fill blinks red.
/// The endless parts run only while <see cref="Live"/> (the island is open), so a closed island costs nothing.
/// </summary>
public sealed class BatteryGlyph : StackPanel
{
    private const double BodyWidth = 20;
    private const double BodyHeight = 10;
    private const double Inset = 1.6;

    private static readonly Color Good = Color.FromRgb(0x34, 0xD3, 0x99);
    private static readonly Color Middle = Color.FromRgb(0xFB, 0xBF, 0x24);
    private static readonly Color Low = Color.FromRgb(0xF8, 0x71, 0x71);
    private static readonly Color Charge = Color.FromRgb(0x5E, 0xB8, 0xFF);

    private readonly TextBlock _icon = new()
    {
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = 13,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 0, 0, 3),
    };

    private readonly Rectangle _fill = new() { RadiusX = 1.4, RadiusY = 1.4, Height = BodyHeight - (2 * Inset), Width = 0 };
    private readonly SolidColorBrush _fillBrush = new(Good);
    private readonly Path _bolt = new()
    {
        Data = Geometry.Parse("M 11.2,1.2 L 7,5.4 L 9.6,5.4 L 8.6,8.8 L 12.8,4.4 L 10.2,4.4 Z"),
        Fill = Brushes.White,
        Opacity = 0,
        IsHitTestVisible = false,
    };

    private readonly RotateTransform _tilt = new();
    private readonly ScaleTransform _hop = new(1, 1);
    private readonly ScaleTransform _breath = new(1, 1, BodyWidth / 2, BodyHeight / 2);
    private readonly Canvas _battery;
    private BatteryDevice? _device;
    private bool _live;

    public BatteryGlyph()
    {
        Orientation = Orientation.Vertical;
        Margin = new Thickness(0, 0, 14, 0);
        VerticalAlignment = VerticalAlignment.Center;
        _icon.RenderTransformOrigin = new Point(0.5, 1);
        _icon.RenderTransform = _hop;
        Children.Add(_icon);
        _fill.Fill = _fillBrush;
        _battery = new Canvas { Width = BodyWidth + 3, Height = BodyHeight, HorizontalAlignment = HorizontalAlignment.Center };
        _battery.Children.Add(new Rectangle { Width = BodyWidth, Height = BodyHeight, RadiusX = 3, RadiusY = 3, Stroke = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1.1 });
        var nub = new Rectangle { Width = 2, Height = 4, RadiusX = 1, RadiusY = 1, Fill = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)) };
        Canvas.SetLeft(nub, BodyWidth + 0.6);
        Canvas.SetTop(nub, (BodyHeight - 4) / 2);
        _battery.Children.Add(nub);
        Canvas.SetLeft(_fill, Inset);
        Canvas.SetTop(_fill, Inset);
        _battery.Children.Add(_fill);
        _battery.Children.Add(_bolt);
        _battery.RenderTransform = _breath;
        Children.Add(_battery);
        RenderTransformOrigin = new Point(0.5, 0.9);
        RenderTransform = _tilt;
        ToolTipService.SetInitialShowDelay(this, 250);
    }

    public string? DeviceId => _device?.Id;

    /// <summary>The island is open: the endless animations (pulse, wobble, blink) may run.</summary>
    public bool Live
    {
        get => _live;
        set
        {
            if (_live == value)
            {
                return;
            }

            _live = value;
            Loop();
        }
    }

    public static string Glyph(BluetoothKind kind) => kind switch
    {
        BluetoothKind.Headphones => "",
        BluetoothKind.Speaker => "",
        BluetoothKind.Mouse => "",
        BluetoothKind.Keyboard => "",
        BluetoothKind.Controller => "",
        BluetoothKind.Pen => "",
        BluetoothKind.Phone => "",
        BluetoothKind.Watch => "",
        _ => "",
    };

    public static Color LevelColor(BatteryDevice d) => d.Charging == true ? Charge : d.IsLow ? Low : d.Percent <= 50 ? Middle : Good;

    public void Show(BatteryDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var old = _device;
        _device = device;
        _icon.Text = Glyph(device.Kind);
        ToolTip = $"{device.Name}  ·  {device.Percent}%" + (device.Charging == true ? "  ·  charging / در حال شارژ" : device.IsLow ? "  ·  low / کم" : string.Empty);
        var reduced = Motion.Reduced;

        // The fill glides to its level (from empty the first time: it fills up as it appears).
        var width = Math.Max(1.5, (BodyWidth - (2 * Inset)) * device.Percent / 100.0);
        var glide = new DoubleAnimation(width, reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(old is null ? 900 : 520))
        {
            EasingFunction = new BackEase { Amplitude = old is null ? 0.35 : 0.2, EasingMode = EasingMode.EaseOut },
        };
        _fill.BeginAnimation(WidthProperty, glide);
        _fillBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _fillBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(LevelColor(device), reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(400)));

        if (old is not null && old.Charging != true && device.Charging == true && !reduced)
        {
            // Plugged in: the icon hops once.
            var hop = new DoubleAnimation(1, 1.3, TimeSpan.FromMilliseconds(170)) { AutoReverse = true, EasingFunction = new QuadraticEase() };
            _hop.BeginAnimation(ScaleTransform.ScaleXProperty, hop);
            _hop.BeginAnimation(ScaleTransform.ScaleYProperty, hop);
        }

        Loop();
    }

    /// <summary>Starts or stops the endless parts for the current state.</summary>
    private void Loop()
    {
        var d = _device;
        var run = _live && d is not null && !Motion.Reduced;
        var charging = d?.Charging == true;
        _bolt.BeginAnimation(OpacityProperty, null);
        _breath.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _tilt.BeginAnimation(RotateTransform.AngleProperty, null);
        _fill.BeginAnimation(OpacityProperty, null);
        _bolt.Opacity = charging ? 1 : 0;
        _fill.Opacity = 1;
        _tilt.Angle = 0;
        if (!run || d is null)
        {
            return;
        }

        if (charging)
        {
            // The bolt pulses and the battery breathes, slowly.
            _bolt.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            _breath.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.06, TimeSpan.FromMilliseconds(1300)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        }
        else if (d.IsLow)
        {
            // Every 2.6 s a short wobble, like a shiver; the critical one also blinks.
            var wobble = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(2600), RepeatBehavior = RepeatBehavior.Forever };
            foreach (var (ms, angle) in new (int, double)[] { (0, 0), (80, -11), (170, 9), (260, -6), (350, 3), (440, 0), (2600, 0) })
            {
                wobble.KeyFrames.Add(new EasingDoubleKeyFrame(angle, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), new SineEase()));
            }

            _tilt.BeginAnimation(RotateTransform.AngleProperty, wobble);
            if (d.IsCritical)
            {
                _fill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(620)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            }
        }
    }
}
