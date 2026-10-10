using Microsoft.Maui.Controls.Shapes;

namespace JackiVpn.Mobile.Controls;

/// <summary>
/// Bottom navigation: a frosted pill with the tabs — the active one sits in a light capsule with its name, the
/// others show only their icon — and a round "+" button beside it. Everything moves on real springs: the capsule's
/// leading edge runs ahead and its trailing edge catches up (it stretches like a drop and settles), icons glide to
/// their new places, a tap squashes and bounces back. Taps carry their momentum into the next move.
/// </summary>
public sealed class FloatingNavBar : ContentView
{
    private const double BarHeight = 64;
    private const double Inset = 6;
    private static readonly Color ActiveInk = Color.FromArgb("#0E1626");
    private static readonly Color IdleInk = Color.FromArgb("#97A0B3");
    private static readonly Color Capsule = Color.FromArgb("#F3F5FA");

    private readonly (string Icon, string LabelKey)[] _tabs = [("home", "Nav_Home"), ("servers", "Nav_Servers"), ("layers", "Nav_Subscriptions"), ("sliders", "Nav_Settings")];
    private readonly AbsoluteLayout _track = new() { FlowDirection = FlowDirection.LeftToRight };
    private readonly GraphicsView _indicatorView = new() { InputTransparent = true, BackgroundColor = Colors.Transparent };
    private readonly IndicatorDrawable _indicator = new();
    private readonly List<Tab> _items = [];
    private readonly Grid _fab;
    private readonly Icon _plus;
    private readonly Spring _left;
    private readonly Spring _right;
    private readonly Spring _plusTurn;
    private int _selected;
    private bool _laidOut;

    private sealed class Tab
    {
        public required Icon Icon { get; init; }
        public required Label Label { get; init; }
        public required Spring IconX { get; init; }
        public required Spring LabelX { get; init; }
        public required Spring LabelShow { get; init; }
        public required Spring Press { get; init; }
        public double X;
        public double Width;
    }

    public FloatingNavBar()
    {
        HeightRequest = BarHeight;
        Margin = new Thickness(16, 0, 16, 16);
        VerticalOptions = LayoutOptions.End;

        _indicatorView.Drawable = _indicator;
        _track.Add(_indicatorView);
        AbsoluteLayout.SetLayoutFlags(_indicatorView, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.SizeProportional);
        AbsoluteLayout.SetLayoutBounds(_indicatorView, new Rect(0, 0, 1, 1));
        _left = new Spring(v => { _indicator.Left = v; _indicatorView.Invalidate(); }, stiffness: 600, damping: Spring.DampingFor(600, 0.78));
        _right = new Spring(v => { _indicator.Right = v; _indicatorView.Invalidate(); }, stiffness: 600, damping: Spring.DampingFor(600, 0.78));

        for (var i = 0; i < _tabs.Length; i++)
        {
            var icon = new Icon { Name = _tabs[i].Icon, Size = 24, Color = IdleInk, WidthRequest = 24, HeightRequest = 24, InputTransparent = true };
            var label = new Label
            {
                FontSize = 14,
                FontFamily = "OpenSansSemibold",
                TextColor = ActiveInk,
                LineBreakMode = LineBreakMode.NoWrap,
                VerticalTextAlignment = TextAlignment.Center,
                HeightRequest = BarHeight,
                Opacity = 0,
                InputTransparent = true,
            };
            label.SetBinding(Label.TextProperty, new Binding($"[{_tabs[i].LabelKey}]", source: Localization.L.Instance));
            _track.Add(icon);
            AbsoluteLayout.SetLayoutBounds(icon, new Rect(0, (BarHeight - 24) / 2, 24, 24));
            _track.Add(label);
            AbsoluteLayout.SetLayoutBounds(label, new Rect(0, 0, 160, BarHeight));
            var press = new Spring(v => icon.Scale = v, 1, stiffness: 520, damping: Spring.DampingFor(520, 0.38));
            _items.Add(new Tab
            {
                Icon = icon,
                Label = label,
                IconX = new Spring(v => icon.TranslationX = v, stiffness: 420, damping: Spring.DampingFor(420, 0.74)),
                LabelX = new Spring(v => label.TranslationX = v, stiffness: 420, damping: Spring.DampingFor(420, 0.8)),
                LabelShow = new Spring(v =>
                {
                    label.Opacity = Math.Clamp(v, 0, 1);
                    label.Scale = 0.86 + (0.14 * Math.Clamp(v, 0, 1.2));
                }, stiffness: 360, damping: Spring.DampingFor(360, 0.9)),
                Press = press,
            });
        }

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, e) =>
        {
            if (e.GetPosition(_track) is { } p && HitTest(p.X) is var index and >= 0)
            {
                _items[index].Press.Snap(0.8);
                _items[index].Press.To(1, velocity: 2);
                Select(index, raise: true);
            }
        };
        _track.GestureRecognizers.Add(tap);
        _track.SizeChanged += (_, _) => Layout(animate: _laidOut);

        // Frosted glass under the tabs (live blur of the content, Android 12+), with a translucent midnight tint.
        var glass = new Grid
        {
            Children =
            {
                new BlurBackdrop(),
                new Border
                {
                    StrokeShape = new RoundRectangle { CornerRadius = 32 },
                    StrokeThickness = 1,
                    Stroke = (Brush)Microsoft.Maui.Controls.Application.Current!.Resources["GlassRim"],
                    BackgroundColor = Color.FromArgb("#B8101521"),
                },
                _track,
            },
        };
        var pill = new Border { StrokeShape = new RoundRectangle { CornerRadius = 32 }, StrokeThickness = 0, BackgroundColor = Colors.Transparent, Content = glass };
        var shadow = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 32 },
            StrokeThickness = 0,
            BackgroundColor = Color.FromArgb("#01000000"),
            Shadow = new Shadow { Brush = new SolidColorBrush(Colors.Black), Opacity = 0.55f, Radius = 26, Offset = new Point(0, 10) },
            InputTransparent = true,
        };

        // The "+" button: add a server by QR code, clipboard or link.
        _plus = new Icon { Name = "plus", Size = 28, Color = Colors.White, StrokeThickness = 2.6, InputTransparent = true, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        _fab = new Grid
        {
            WidthRequest = BarHeight,
            HeightRequest = BarHeight,
            Children =
            {
                new Ellipse
                {
                    Fill = new LinearGradientBrush(new GradientStopCollection { new(Color.FromArgb("#2B7FF0"), 0f), new(Color.FromArgb("#6BB8FF"), 1f) }, new Point(0.5, 0), new Point(0.5, 1)),
                },
                new Ellipse { Stroke = new SolidColorBrush(Colors.White.WithAlpha(0.28f)), StrokeThickness = 1, Margin = new Thickness(2) },
                _plus,
            },
            Shadow = new Shadow { Brush = new SolidColorBrush(Color.FromArgb("#3D9BFF")), Opacity = 0.6f, Radius = 22, Offset = new Point(0, 8) },
        };
        AutomationProperties.SetName(_fab, "Add");
        _plusTurn = new Spring(v => _plus.Rotation = v, stiffness: 300, damping: Spring.DampingFor(300, 0.55));
        var fabPress = Squish.Attach(_fab, 0.86);
        var fabTap = new TapGestureRecognizer();
        fabTap.Tapped += (_, _) =>
        {
            Haptic();
            fabPress.Impulse(-2.5);
            _plusTurn.To(_plusTurn.Target + 90);
            AddTapped?.Invoke(this, EventArgs.Empty);
        };
        _fab.GestureRecognizers.Add(fabTap);

        var row = new Grid { ColumnDefinitions = [new(GridLength.Star), new(new GridLength(12)), new(new GridLength(BarHeight))] };
        row.Add(new Grid { Children = { shadow, pill } }, 0, 0);
        row.Add(_fab, 2, 0);
        Content = row;
        Localization.L.LanguageChanged += (_, _) => MainThread.BeginInvokeOnMainThread(() => Layout(animate: true));
    }

    public event EventHandler<int>? SelectedChanged;

    /// <summary>The "+" button (add servers).</summary>
    public event EventHandler? AddTapped;

    /// <summary>Kept for callers that mirror the connection (the power button now lives on Home only).</summary>
    public event EventHandler? PowerTapped;

    public int Selected => _selected;

    /// <summary>The "+" button glows in the connection's colour.</summary>
    public void SetPower(PowerVisual visual, string gradientKey, Color accent)
    {
        _ = gradientKey;
        _ = PowerTapped;
        var tint = visual == PowerVisual.Connected ? accent : Color.FromArgb("#3D9BFF");
        _fab.Shadow = new Shadow { Brush = new SolidColorBrush(tint), Opacity = 0.6f, Radius = 22, Offset = new Point(0, 8) };
    }

    public void Select(int index, bool raise, bool animate = true)
    {
        var previous = _selected;
        _selected = index;
        Layout(animate && _laidOut && !Motion.Reduced, previous);
        if (raise && previous != index)
        {
            Haptic();
            SelectedChanged?.Invoke(this, index);
        }
    }

    private int HitTest(double x)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (x >= _items[i].X && x < _items[i].X + _items[i].Width)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Visual order follows the reading direction (Persian: first tab on the right).</summary>
    private int Slot(int index) => Localization.L.IsPersian ? _items.Count - 1 - index : index;

    private void Layout(bool animate, int? previous = null)
    {
        var w = _track.Width;
        if (w <= 0)
        {
            return;
        }

        var active = _items[_selected];
        var labelWidth = active.Label.Measure(double.PositiveInfinity, BarHeight).Width;
        var activeWidth = Math.Clamp(24 + 10 + labelWidth + 40, 96, (w - (2 * Inset)) * 0.5);
        var otherWidth = (w - (2 * Inset) - activeWidth) / (_items.Count - 1);

        // Slot rectangles in visual order.
        var x = Inset;
        var bySlot = new (double X, double W)[_items.Count];
        for (var s = 0; s < _items.Count; s++)
        {
            var index = Localization.L.IsPersian ? _items.Count - 1 - s : s;
            var width = index == _selected ? activeWidth : otherWidth;
            bySlot[s] = (x, width);
            x += width;
        }

        for (var i = 0; i < _items.Count; i++)
        {
            var tab = _items[i];
            (tab.X, tab.Width) = bySlot[Slot(i)];
            var isActive = i == _selected;
            var rtl = Localization.L.IsPersian;
            var contentWidth = 24 + 10 + labelWidth;
            var start = tab.X + ((tab.Width - contentWidth) / 2);
            var iconX = isActive ? (rtl ? start + contentWidth - 24 : start) : tab.X + ((tab.Width - 24) / 2);
            var labelX = rtl ? start : start + 34;
            tab.Icon.Color = isActive ? ActiveInk : IdleInk;
            tab.Icon.StrokeThickness = isActive ? 2.3 : 1.8;
            Move(tab.IconX, iconX, animate);
            Move(tab.LabelX, labelX, animate && tab.Label.Opacity > 0.05);
            if (animate)
            {
                tab.LabelShow.To(isActive ? 1 : 0);
            }
            else
            {
                tab.LabelShow.Snap(isActive ? 1 : 0);
            }
        }

        // The capsule: the edge in the direction of travel is stiffer, the other one lags — it stretches, then settles.
        var left = active.X;
        var right = active.X + active.Width;
        _indicator.RestWidth = active.Width;
        if (!animate)
        {
            _left.Snap(left);
            _right.Snap(right);
        }
        else
        {
            var forward = previous is { } p && _items[p].X < active.X;
            (_left.Stiffness, _right.Stiffness) = forward ? (210.0, 620.0) : (620.0, 210.0);
            _left.Damping = Spring.DampingFor(_left.Stiffness, 0.74);
            _right.Damping = Spring.DampingFor(_right.Stiffness, 0.74);
            _left.To(left);
            _right.To(right);
        }

        _laidOut = true;
    }

    private static void Move(Spring spring, double target, bool animate)
    {
        if (animate)
        {
            spring.To(target);
        }
        else
        {
            spring.Snap(target);
        }
    }

    private static void Haptic()
    {
        try
        {
            HapticFeedback.Default.Perform(HapticFeedbackType.Click);
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or PermissionException)
        {
        }
    }

    /// <summary>The light capsule; while stretched wider than its resting width it gets a little thinner (volume is kept).</summary>
    private sealed class IndicatorDrawable : IDrawable
    {
        public double Left;
        public double Right;
        public double RestWidth = 1;

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            var width = Right - Left;
            if (width <= 1)
            {
                return;
            }

            var stretch = Math.Clamp((width / Math.Max(1, RestWidth)) - 1, -0.3, 0.6);
            var squeeze = (float)(stretch * 7);
            var top = (float)Inset + squeeze;
            var height = dirtyRect.Height - (2 * (float)Inset) - (2 * squeeze);
            canvas.FillColor = Capsule;
            canvas.SetShadow(new SizeF(0, 3), 10, Colors.Black.WithAlpha(0.28f));
            canvas.FillRoundedRectangle((float)Left, top, (float)width, height, height / 2);
        }
    }
}
