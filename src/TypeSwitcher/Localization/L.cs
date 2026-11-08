using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TypeSwitcher.Localization;

/// <summary>UI-only culture. Dictionary analysis and numeric settings retain their own formats.</summary>
public static class L
{
    public sealed class LanguageState : INotifyPropertyChanged
    {
        private string _language = "ru";
        public string Language => _language;
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Set(string language)
        {
            if (_language == language) return;
            _language = language;
            PropertyChanged?.Invoke(this, new(nameof(Language)));
        }
    }

    public static LanguageState State { get; } = new();
    public static string Language => State.Language;
    public static event EventHandler? Changed;
    private static readonly IReadOnlyDictionary<string, Dictionary<string, string>> Catalogs =
        new Dictionary<string, Dictionary<string, string>> { ["en"] = Load("en"), ["uk"] = Load("uk") };
    private static readonly ConcurrentDictionary<string, byte> Missing = new(StringComparer.Ordinal);
    public static IReadOnlyCollection<string> MissingKeys => Missing.Keys.ToArray();
    public static IReadOnlyCollection<string> Keys => Catalogs["en"].Keys;
    public static IReadOnlyDictionary<string, string> Catalog(string language) => Catalogs[language];
    public static string NormalizeLanguage(string? language) => language?.ToLowerInvariant() is "en" or "uk" ? language.ToLowerInvariant() : "ru";

    private static Dictionary<string, string> Load(string language)
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream($"TypeSwitcher.Localization.{language}.json")
            ?? throw new InvalidOperationException("Localization resource is missing: " + language);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    public static void SetLanguage(string language)
    {
        string normalized = NormalizeLanguage(language);
        if (State.Language == normalized) return;
        State.Set(normalized);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static string T(string message, params object?[] args) => Get(message, Language, args);
    public static string Get(string message, string language, params object?[] args)
    {
        if (message.Length == 0) return "";
        string text = message;
        if (NormalizeLanguage(language) != "ru")
        {
            if (!Catalogs[NormalizeLanguage(language)].TryGetValue(message, out text!))
            {
                Missing.TryAdd(message, 0);
                text = message;
            }
        }
        if (args.Length == 0) return text;
        var culture = CultureInfo.GetCultureInfo(language switch { "en" => "en-US", "uk" => "uk-UA", _ => "ru-RU" });
        return string.Format(culture, text, args);
    }

    public static void Bind(DependencyObject target, DependencyProperty property, string message, params object?[] args)
    {
        BindingOperations.SetBinding(target, property, new Binding(nameof(LanguageState.Language))
        {
            Source = State, Mode = BindingMode.OneWay, Converter = new Translation(message, args)
        });
    }

    public static T Localize<T>(this T target, DependencyProperty property, string message, params object?[] args) where T : DependencyObject
    {
        Bind(target, property, message, args); return target;
    }
    public static TextBlock Localize(this TextBlock target, string message, params object?[] args)
        => target.Localize(TextBlock.TextProperty, message, args);

    private sealed class Translation(string message, object?[] args) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Get(message, (string)value, args);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}

/// <summary>Stable value and live translated caption for list/menu choices.</summary>
public sealed class UiChoice : INotifyPropertyChanged
{
    public string Id { get; }
    private readonly string _message;
    private readonly bool _nativeName;
    public string Label => _nativeName ? _message : L.T(_message);
    public UiChoice(string id, string message, bool nativeName = false)
    {
        Id = id; _message = message; _nativeName = nativeName;
        PropertyChangedEventManager.AddHandler(L.State, Changed, nameof(L.LanguageState.Language));
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) => PropertyChanged?.Invoke(this, new(nameof(Label)));
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => Label;
}
