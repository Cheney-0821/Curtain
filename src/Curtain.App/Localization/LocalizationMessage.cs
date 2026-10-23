namespace Curtain.App.Localization;

internal sealed record LocalizationMessage(string Key, object?[] Arguments)
{
    internal static LocalizationMessage Create(string key, params object?[] arguments) => new(key, arguments);
}
