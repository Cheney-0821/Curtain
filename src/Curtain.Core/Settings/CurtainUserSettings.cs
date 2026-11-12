using System.Text.Json;

namespace Curtain.Core.Settings;

using Curtain.Core.Protection;

public static class HotkeyModifier
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Windows = 0x0008;
}

public sealed record GlobalHotkeySettings(bool Enabled = true, uint Modifiers = HotkeyModifier.Alt,
    uint VirtualKey = 0x53);

public enum AutoProtectionScope { AddedWindows = 0, AllDisplays = 1 }

public static class OverlayBlurPreset
{
    public const string Light = "Light";
    public const string Standard = "Standard";
    public const string Strong = "Strong";

    public static string Normalize(string? preset) => preset switch
    {
        Light => Light,
        Strong => Strong,
        _ => Standard
    };

    public static float Amount(string? preset) => Normalize(preset) switch
    {
        Light => 2.0f,
        Strong => 9.0f,
        _ => 5.0f
    };
}

public static class AppLanguage
{
    public const string System = "System";
    public const string English = "en-US";
    public const string Chinese = "zh-CN";

    public static string Normalize(string? language) => language switch
    {
        English => English,
        Chinese => Chinese,
        _ => System
    };
}

public sealed record AutoProtectionSettings(bool Enabled = false, int IdleMinutes = 3,
    AutoProtectionScope Scope = AutoProtectionScope.AddedWindows);

public sealed record CurtainUserSettings
{
    public int SchemaVersion { get; init; } = 1;
    public bool StartWithWindows { get; init; }
    public bool DiagnosticsEnabled { get; init; } = true;
    public bool IncludeRawEtw { get; init; } = true;
    // Missing from old settings.json => stays true via the property initializer.
    public bool AutoDownloadUpdates { get; init; } = true;
    public GlobalHotkeySettings Hotkey { get; init; } = new();
    public AutoProtectionSettings AutoProtection { get; init; } = new();
    public GlobalHotkeySettings PrivacyHotkey { get; init; } = new(true, HotkeyModifier.Control, 0x28);
    public PrivacyMouseButton PrivacyMouseButton { get; init; } = PrivacyMouseButton.Off;
    public string BlurPreset { get; init; } = OverlayBlurPreset.Standard;
    public string Language { get; init; } = AppLanguage.System;
    public WindowProtectionSettings WindowProtection { get; init; } = new();
}

public sealed record CurtainUserSettingsLoad(CurtainUserSettings Settings, bool RecoveredFromInvalidFile);

public sealed class CurtainUserSettingsStore
{
    private const long MaximumFileBytes = 1_048_576;
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public CurtainUserSettingsStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Curtain");
        _path = Path.Combine(directory, "settings.json");
    }

    public CurtainUserSettingsLoad Load() => Load(null);

    public CurtainUserSettingsLoad Load(Action<string, int>? diagnostic)
    {
        if (!File.Exists(_path))
        {
            diagnostic?.Invoke("load-file", 0);
            return new(new CurtainUserSettings(), false);
        }
        try
        {
            var info = new FileInfo(_path);
            if (info.Length is <= 0 or > MaximumFileBytes)
            {
                diagnostic?.Invoke("load-size-validation", unchecked((int)0x800700DF));
                return new(new CurtainUserSettings(), true);
            }
            string json = File.ReadAllText(_path);
            diagnostic?.Invoke("load-file", 0);
            var settings = JsonSerializer.Deserialize<CurtainUserSettings>(json, _json);
            diagnostic?.Invoke("load-deserialize", 0);
            if (settings is null || !IsValid(settings))
            {
                diagnostic?.Invoke("load-validate", unchecked((int)0x8007000D));
                return new(new CurtainUserSettings(), true);
            }
            diagnostic?.Invoke("load-validate", 0);
            return new(NormalizeNewSettings(settings), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            diagnostic?.Invoke("load-failed", ex.HResult);
            return new(new CurtainUserSettings(), true);
        }
    }

    public void Save(CurtainUserSettings settings) => Save(settings, null);

    public void Save(CurtainUserSettings settings, Action<string, int>? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = NormalizeNewSettings(settings);
        if (!IsValid(settings))
        {
            diagnostic?.Invoke("save-validate", unchecked((int)0x8007000D));
            throw new ArgumentException("Invalid Curtain settings.", nameof(settings));
        }
        diagnostic?.Invoke("save-validate", 0);
        string? stage = "save-directory";
        string temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string json = JsonSerializer.Serialize(settings, _json);
            stage = "save-temp-write";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 16 * 1024, leaveOpen: true))
            {
                writer.Write(json);
                writer.Flush();
                stage = "save-flush";
                stream.Flush(flushToDisk: true);
            }
            diagnostic?.Invoke("save-temp-flushed", 0);
            stage = "save-replace";
            if (File.Exists(_path)) File.Replace(temporary, _path, null, ignoreMetadataErrors: true);
            else File.Move(temporary, _path);
            diagnostic?.Invoke("save-replace", 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            diagnostic?.Invoke(stage, ex.HResult);
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static bool IsValid(CurtainUserSettings settings) =>
        settings.SchemaVersion == 1 && settings.Hotkey is not null && IsValidHotkey(settings.Hotkey) &&
        settings.PrivacyHotkey is not null && IsValidHotkey(settings.PrivacyHotkey) &&
        !IsSameShortcut(settings.Hotkey, settings.PrivacyHotkey) &&
        settings.AutoProtection is { } auto &&
        auto.IdleMinutes is 1 or 3 or 5 or 10 &&
        Enum.IsDefined(auto.Scope);

    private static CurtainUserSettings NormalizeNewSettings(CurtainUserSettings settings) => settings with
    {
        Hotkey = settings.Hotkey is null ? null! : settings.Hotkey with { Enabled = true },
        BlurPreset = OverlayBlurPreset.Normalize(settings.BlurPreset),
        Language = AppLanguage.Normalize(settings.Language),
        WindowProtection = WindowBlurScale.Normalize(settings.WindowProtection ?? new()),
        PrivacyMouseButton = Enum.IsDefined(settings.PrivacyMouseButton) ? settings.PrivacyMouseButton : PrivacyMouseButton.Off
    };

    public static bool IsSameShortcut(GlobalHotkeySettings first, GlobalHotkeySettings second) =>
        first.Modifiers == second.Modifiers && first.VirtualKey == second.VirtualKey;

    public static bool IsValidHotkey(GlobalHotkeySettings hotkey)
    {
        const uint allowed = HotkeyModifier.Alt | HotkeyModifier.Control | HotkeyModifier.Shift | HotkeyModifier.Windows;
        bool hasModifier = (hotkey.Modifiers & allowed) != 0;
        bool keyIsModifier = hotkey.VirtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C;
        return hasModifier && (hotkey.Modifiers & ~allowed) == 0 &&
            (hotkey.VirtualKey == 0x20 || hotkey.VirtualKey is (>= 0x25 and <= 0x28) or (>= 0x30 and <= 0x5A) or (>= 0x60 and <= 0x7B)) &&
            hotkey.VirtualKey != 0x7B && !keyIsModifier;
    }
}
