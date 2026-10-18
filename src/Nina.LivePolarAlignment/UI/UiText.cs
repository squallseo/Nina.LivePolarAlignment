using System;
using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using NINA.Core.Locale;

namespace Nina.LivePolarAlignment.UI;

/// <summary>Plugin translations follow the active NINA profile, with English fallback.</summary>
public sealed class UiText : INotifyPropertyChanged {
    private static readonly ResourceManager Resources = new("Nina.LivePolarAlignment.Locale.Strings", typeof(UiText).Assembly);
    public static UiText Instance { get; } = new();
    private static Func<string?>? cultureSource;
    private static string? previousCulture;
    public static CultureInfo Culture {
        get {
            try { return CultureInfo.GetCultureInfo(cultureSource?.Invoke() ?? "en-US"); }
            catch (CultureNotFoundException) { return CultureInfo.GetCultureInfo("en-US"); }
        }
    }
    public static void Configure(Func<string?> source) { cultureSource = source; Refresh(); }
    public static void Refresh() {
        string culture = Culture.Name;
        if (previousCulture == culture) return;
        previousCulture = culture;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
    }
    public static string Get(string key) => Resources.GetString(key, Culture) ?? key;
    public static string Format(string key, params object[] arguments) => string.Format(Culture, Get(key), arguments);
    public string this[string key] => Get(key);
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class UiLocExtension : Binding {
    public UiLocExtension(string key) : base($"[{key}]") { Source = UiText.Instance; Mode = BindingMode.OneWay; }
}
