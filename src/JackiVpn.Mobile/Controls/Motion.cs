using Microsoft.Maui.Controls.Shapes;

namespace JackiVpn.Mobile.Controls;

/// <summary>
/// Shared motion rules: spring-based and short (in the spirit of animate-ui / Motion), and off when the user
/// turned system animations off. Every touch gets feedback; things that appear, slide in.
/// </summary>
public static class Motion
{
    /// <summary>A soft spring with a little overshoot (Easing.SpringOut is too bouncy for UI chrome).</summary>
    public static readonly Easing Spring = new(t => 1 - (Math.Cos(t * Math.PI * 2.4) * Math.Exp(-6.2 * t)));

    /// <summary>Snappy ease-out (exponential).</summary>
    public static readonly Easing Snap = new(t => t >= 1 ? 1 : 1 - Math.Pow(2, -10 * t));

    /// <summary>Android "Remove animations" (animator duration scale 0) disables decorative motion.</summary>
    public static bool Reduced
    {
        get
        {
            try
            {
                var resolver = global::Android.App.Application.Context.ContentResolver;
                return global::Android.Provider.Settings.Global.GetFloat(resolver, global::Android.Provider.Settings.Global.AnimatorDurationScale, 1f) == 0f;
            }
            catch (global::Android.Provider.Settings.SettingNotFoundException)
            {
                return false;
            }
        }
    }

    public static uint Ms(uint ms) => Reduced ? 0u : ms;

    /// <summary>Fade + rise into place (page and card entrances).</summary>
    public static Task EnterAsync(VisualElement element, uint delay = 0, double rise = 18)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (Reduced)
        {
            element.Opacity = 1;
            element.TranslationY = 0;
            return Task.CompletedTask;
        }

        element.Opacity = 0;
        element.TranslationY = rise;
        return Task.Delay((int)delay).ContinueWith(
            _ => Task.WhenAll(element.FadeToAsync(1, 320, Easing.CubicOut), element.TranslateToAsync(0, 0, 520, Spring)),
            TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
    }

    /// <summary>Cross-fades a label's text: out, swap, in.</summary>
    public static async Task SwapTextAsync(Label label, string text)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (label.Text == text)
        {
            return;
        }

        if (Reduced)
        {
            label.Text = text;
            return;
        }

        await Task.WhenAll(label.FadeToAsync(0, 110, Easing.CubicIn), label.TranslateToAsync(0, -8, 110, Easing.CubicIn));
        label.Text = text;
        label.TranslationY = 8;
        await Task.WhenAll(label.FadeToAsync(1, 220, Easing.CubicOut), label.TranslateToAsync(0, 0, 420, Spring));
    }

    /// <summary>Press feedback: a quick squeeze and a springy release.</summary>
    public static async Task TapAsync(VisualElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (Reduced)
        {
            return;
        }

        await element.ScaleToAsync(0.92, 90, Easing.CubicOut);
        await element.ScaleToAsync(1, 520, Spring);
    }

    /// <summary>Stretchy feedback: squash wide, then spring back (tab icons, toggles).</summary>
    public static async Task StretchAsync(VisualElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (Reduced)
        {
            return;
        }

        await Task.WhenAll(element.ScaleXToAsync(1.28, 110, Easing.CubicOut), element.ScaleYToAsync(0.78, 110, Easing.CubicOut));
        await Task.WhenAll(element.ScaleXToAsync(1, 620, Spring), element.ScaleYToAsync(1, 620, Spring));
    }

    // ------------------------------------------------------------------ attached: Appear

    /// <summary>
    /// Attached "Appear": the element fades and rises in when it is loaded. Elements loaded together (a page of
    /// cards, a list filling up) are staggered so they cascade in one after another.
    /// </summary>
    public static readonly BindableProperty AppearProperty = BindableProperty.CreateAttached("Appear", typeof(bool), typeof(Motion), false,
        propertyChanged: (b, _, n) =>
        {
            if (b is VisualElement element && n is true)
            {
                element.Loaded += OnAppearLoaded;
            }
        });

    private static long _lastAppearTicks;
    private static int _appearSequence;

    public static bool GetAppear(BindableObject view) => (bool)view.GetValue(AppearProperty);

    public static void SetAppear(BindableObject view, bool value) => view.SetValue(AppearProperty, value);

    private static void OnAppearLoaded(object? sender, EventArgs e)
    {
        if (sender is not VisualElement element || Reduced)
        {
            return;
        }

        var now = Environment.TickCount64;
        _appearSequence = now - _lastAppearTicks > 250 ? 0 : _appearSequence + 1;
        _lastAppearTicks = now;
        var delay = (uint)Math.Min(_appearSequence * 40, 320);
        element.Opacity = 0;
        element.TranslationY = 18;
        element.Scale = 0.97;
        element.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(delay), () =>
        {
            _ = element.FadeToAsync(1, 280, Easing.CubicOut);
            _ = element.TranslateToAsync(0, 0, 560, Spring);
            _ = element.ScaleToAsync(1, 560, Spring);
        });
    }

    // ------------------------------------------------------------------ attached: Press

    /// <summary>Attached "Press": buttons squeeze while pressed and spring back on release.</summary>
    public static readonly BindableProperty PressProperty = BindableProperty.CreateAttached("Press", typeof(bool), typeof(Motion), false,
        propertyChanged: (b, _, n) =>
        {
            if (b is Button button && n is true)
            {
                button.Pressed += (_, _) =>
                {
                    if (!Reduced)
                    {
                        _ = button.ScaleToAsync(0.94, 90, Easing.CubicOut);
                    }
                };
                button.Released += (_, _) =>
                {
                    if (!Reduced)
                    {
                        _ = button.ScaleToAsync(1, 560, Spring);
                    }
                };
            }
        });

    public static bool GetPress(BindableObject view) => (bool)view.GetValue(PressProperty);

    public static void SetPress(BindableObject view, bool value) => view.SetValue(PressProperty, value);
}

/// <summary>Base page: midnight background; content glides in whenever the page appears.</summary>
public class AnimatedPage : ContentPage
{
    public AnimatedPage()
    {
        Background = (Brush)Microsoft.Maui.Controls.Application.Current!.Resources["PageGradient"];
        FlowDirection = Localization.L.Flow;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        FlowDirection = Localization.L.Flow;
        if (Content is { } content)
        {
            _ = Motion.EnterAsync(content, rise: 14);
        }
    }
}

/// <summary>A dot with a soft, repeating pulse — marks live things (the active server, a running test).</summary>
public sealed class LiveDot : Grid
{
    public static readonly BindableProperty ColorProperty = BindableProperty.Create(nameof(Color), typeof(Color), typeof(LiveDot), Colors.LimeGreen,
        propertyChanged: (b, _, n) => ((LiveDot)b).ApplyColor((Color)n));

    private readonly Ellipse _halo = new() { WidthRequest = 10, HeightRequest = 10, Opacity = 0.6 };
    private readonly Ellipse _core = new() { WidthRequest = 10, HeightRequest = 10 };

    public LiveDot()
    {
        WidthRequest = 22;
        HeightRequest = 22;
        Children.Add(_halo);
        Children.Add(_core);
        ApplyColor(Color);
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => this.AbortAnimation("pulse");
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsVisible))
            {
                if (IsVisible)
                {
                    Start();
                }
                else
                {
                    this.AbortAnimation("pulse");
                }
            }
        };
    }

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    private void ApplyColor(Color c)
    {
        _halo.Fill = new SolidColorBrush(c);
        _core.Fill = new SolidColorBrush(c);
    }

    private void Start()
    {
        if (Motion.Reduced || this.AnimationIsRunning("pulse"))
        {
            return;
        }

        new Animation(v =>
        {
            _halo.Scale = 1 + v * 1.2;
            _halo.Opacity = 0.55 * (1 - v);
        }, 0, 1, Easing.CubicOut).Commit(this, "pulse", 16, 1400, repeat: () => IsVisible);
    }
}
