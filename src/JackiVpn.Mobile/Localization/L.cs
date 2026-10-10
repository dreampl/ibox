using System.ComponentModel;
using System.Globalization;

namespace JackiVpn.Mobile.Localization;

/// <summary>
/// UI language (English / Persian). XAML binds through <see cref="TExtension"/>, so switching the language
/// updates every visible text at once; Persian also flips the layout to right-to-left.
/// </summary>
public sealed class L : INotifyPropertyChanged
{
    public static L Instance { get; } = new();

    private bool _persian;

    public event PropertyChangedEventHandler? PropertyChanged;

    public static bool IsPersian => Instance._persian;

    public static FlowDirection Flow => IsPersian ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public string this[string key] => Get(key);

    /// <summary>"auto" follows the phone's language.</summary>
    public void Apply(string? language)
    {
        var persian = language switch
        {
            "fa" => true,
            "en" => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "fa",
        };
        if (persian == _persian)
        {
            return;
        }

        _persian = persian;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Flow)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public static event EventHandler? LanguageChanged;

    public static string Get(string key) =>
        Strings.Table.TryGetValue(key, out var s) ? (IsPersian ? s.Fa : s.En) : key;

    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>Digits in Persian when the UI is Persian (numbers stay readable in both).</summary>
    public static string N(string text)
    {
        if (!IsPersian || string.IsNullOrEmpty(text))
        {
            return text;
        }

        return string.Create(text.Length, text, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                span[i] = c is >= '0' and <= '9' ? (char)('۰' + (c - '0')) : c;
            }
        });
    }
}

/// <summary>{loc:T Key} — a live binding to the current language.</summary>
[ContentProperty(nameof(Key))]
public sealed class TExtension : IMarkupExtension<BindingBase>
{
    public string Key { get; set; } = string.Empty;

    public BindingBase ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]", BindingMode.OneWay, source: L.Instance);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}
