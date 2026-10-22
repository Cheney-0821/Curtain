using System.Text.Json;

namespace Curtain.Core.Settings;

public static class HotkeyModifier
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Windows = 0x0008;
}

public sealed record GlobalHotkeySettings(bool Enabled = true, uint Modifiers = HotkeyModifier.Control | HotkeyModifier.Shift,
    uint VirtualKey = 0x78);

public enum AutoProtectionScope { AddedWindows = 0, AllDisplays = 1 }

public sealed record AutoProtectionSettings(bool Enabled = false, int IdleMinutes = 3,
    AutoProtectionScope Scope = AutoProtectionScope.AddedWindows);

public sealed record CurtainUserSettings
{
    public int SchemaVersion { get; init; } = 1;
    public bool StartWithWindows { get; init; }
    // Missing from old settings.json => stays true via the property initializer.
    public bool AutoDownloadUpdates { get; init; } = true;
    public GlobalHotkeySettings Hotkey { get; init; } = new();
    public AutoProtectionSettings AutoProtection { get; init; } = new();
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

    public CurtainUserSettingsLoad Load()
    {
        if (!File.Exists(_path)) return new(new CurtainUserSettings(), false);
        try
        {
            var info = new FileInfo(_path);
            if (info.Length is <= 0 or > MaximumFileBytes) return new(new CurtainUserSettings(), true);
            var settings = JsonSerializer.Deserialize<CurtainUserSettings>(File.ReadAllText(_path), _json);
            if (settings is null || !IsValid(settings)) return new(new CurtainUserSettings(), true);
            return new(settings, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(new CurtainUserSettings(), true);
        }
    }

    public void Save(CurtainUserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsValid(settings)) throw new ArgumentException("Invalid Curtain settings.", nameof(settings));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, _json));
        File.Move(temporary, _path, true);
    }

    private static bool IsValid(CurtainUserSettings settings) =>
        settings.SchemaVersion == 1 && settings.Hotkey is not null && IsValidHotkey(settings.Hotkey) &&
        settings.AutoProtection is { } auto &&
        auto.IdleMinutes is 1 or 3 or 5 or 10 &&
        Enum.IsDefined(auto.Scope);

    public static bool IsValidHotkey(GlobalHotkeySettings hotkey)
    {
        const uint allowed = HotkeyModifier.Alt | HotkeyModifier.Control | HotkeyModifier.Shift;
        bool hasModifier = (hotkey.Modifiers & allowed) != 0;
        bool keyIsModifier = hotkey.VirtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C;
        return hasModifier && (hotkey.Modifiers & ~allowed) == 0 &&
            (hotkey.VirtualKey is (>= 0x30 and <= 0x5A) or (>= 0x60 and <= 0x7B)) &&
            hotkey.VirtualKey != 0x7B && !keyIsModifier;
    }
}
