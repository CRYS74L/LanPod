using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LanPod.Controls;
using Binding = System.Windows.Data.Binding;

namespace LanPod.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    public const string DefaultLanguage = "zh-Hans";
    public static LocalizationService Instance { get; } = new();
    private static readonly ResourceManager Resources = new("LanPod.Resources.Strings", typeof(LocalizationService).Assembly);
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Translations = new();
    private static ResourceDictionary? _applicationResources;
    private string _language = DefaultLanguage;

    private LocalizationService() { }

    public static string Language => Instance._language;
    public string CurrentLanguage => _language;
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language);
    public event PropertyChangedEventHandler? PropertyChanged;

    public static string NormalizeLanguage(string? language) => language switch
    {
        "zh-Hant" or "ja" or "en" => language,
        _ => DefaultLanguage
    };

    public static IReadOnlyDictionary<string, string> GetStrings(string? language) =>
        Translations.GetOrAdd(NormalizeLanguage(language), name =>
        {
            var culture = CultureInfo.GetCultureInfo(name);
            var neutral = Resources.GetResourceSet(CultureInfo.InvariantCulture, true, true)
                ?? throw new MissingManifestResourceException("Missing LanPod string resources.");
            return neutral.Cast<DictionaryEntry>().ToDictionary(
                entry => (string)entry.Key,
                entry => Resources.GetString((string)entry.Key, culture)
                    ?? throw new MissingManifestResourceException($"Missing translation: {entry.Key} ({name})."),
                StringComparer.Ordinal);
        });

    public static string Text(string source) => GetStrings(Language).TryGetValue(source, out var value)
        ? value
        : throw new MissingManifestResourceException($"Missing LanPod string: {source}.");

    public static string Format(string source, params object?[] arguments) =>
        string.Format(Culture, Text(source), arguments);

    // Router-provided values and user-authored names retain their original text.
    public static string TranslateValue(string value) =>
        GetStrings(Language).TryGetValue(value, out var translated) ? translated : value;

    public static void Apply(string? language)
    {
        var normalized = NormalizeLanguage(language);
        var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
        if (Language == normalized && _applicationResources is not null && merged.Contains(_applicationResources)) return;
        var dictionary = new ResourceDictionary();
        foreach (var (source, value) in GetStrings(normalized)) dictionary.Add("Text." + source, value);
        var index = _applicationResources is null ? -1 : merged.IndexOf(_applicationResources);
        if (index < 0) merged.Add(dictionary);
        else merged[index] = dictionary;
        _applicationResources = dictionary;
        Instance._language = normalized;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
    }

    public static void SetText(FrameworkElement target, string source, params object?[] arguments)
    {
        var property = target is RollingText ? RollingText.TextProperty : TextBlock.TextProperty;
        BindingOperations.SetBinding(target, property, new Binding(nameof(CurrentLanguage))
        {
            Source = Instance,
            Mode = BindingMode.OneWay,
            Converter = MessageConverter.Instance,
            ConverterParameter = new Message(source, arguments)
        });
    }

    private sealed record Message(string Source, object?[] Arguments);

    private sealed class MessageConverter : IValueConverter
    {
        internal static readonly MessageConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var message = (Message)parameter;
            return message.Arguments.Length == 0
                ? TranslateValue(message.Source)
                : Format(message.Source, message.Arguments);
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

public sealed class LocalizedValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        LocalizationService.TranslateValue(value?.ToString() ?? string.Empty);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
