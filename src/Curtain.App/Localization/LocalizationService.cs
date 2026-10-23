using System.Diagnostics;
using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Curtain.App.Localization;

internal static class LocalizationService
{
    private const string SystemLanguage = "System";
    private const string English = "en-US";
    private const string Chinese = "zh-CN";
    private static readonly Lazy<ResourceManager> Manager = new(() =>
        new ResourceManager(Path.Combine(AppContext.BaseDirectory, "Curtain.App.pri")));
    private static readonly Dictionary<string, ResourceContext> Contexts = new(StringComparer.OrdinalIgnoreCase);
    private static string _language = SystemLanguage;

    internal static event Action? LanguageChanged;
    internal static string Language => _language;
    internal static string EffectiveLanguage => _language == SystemLanguage
        ? (CultureInfo.CurrentUICulture.Name.StartsWith("zh-", StringComparison.OrdinalIgnoreCase) ? Chinese : English)
        : _language;

    internal static string NormalizeLanguage(string? language) => language switch
    {
        English => English,
        Chinese => Chinese,
        _ => SystemLanguage
    };

    internal static void SetLanguage(string? language)
    {
        string normalized = NormalizeLanguage(language);
        if (_language == normalized) return;
        _language = normalized;
        LanguageChanged?.Invoke();
    }

    internal static string Get(string key, params object?[] arguments)
    {
        string? value = TryGet(key, EffectiveLanguage) ?? TryGet(key, English);
        if (value is null)
        {
            Debug.WriteLine($"Missing localization resource: {key}");
            return string.Empty;
        }
        return arguments.Length == 0 ? value : string.Format(CultureInfo.GetCultureInfo(EffectiveLanguage), value, arguments);
    }

    private static string? TryGet(string key, string language)
    {
        try
        {
            ResourceContext context;
            lock (Contexts)
            {
                if (!Contexts.TryGetValue(language, out context!))
                {
                    context = Manager.Value.CreateResourceContext();
                    context.QualifierValues["Language"] = language;
                    Contexts.Add(language, context);
                }
            }
            return Manager.Value.MainResourceMap.GetSubtree("Resources").GetValue(key, context).ValueAsString;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to load localization resource {key} ({language}): {ex.GetType().Name}");
            return null;
        }
    }
}
