using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using iTask.Utilities;

namespace iTask.Configuration;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads settings, writing a defaults file on first run. Falls back to defaults on any error.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
                Migrate(settings);
                // Re-save so options added in newer versions show up in the file with their defaults.
                var upgraded = JsonSerializer.Serialize(settings, Options);
                if (upgraded != json)
                    Save(settings);
                return settings;
            }

            var defaults = new AppSettings { SettingsVersion = AppSettings.CurrentVersion };
            Save(defaults);
            return defaults;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load settings; using defaults", ex);
            return new AppSettings();
        }
    }

    /// <summary>
    /// Brings older files forward. Values saved by an older version were that version's *defaults*
    /// (the file is written on first run), so design changes must overwrite them explicitly.
    /// </summary>
    private static void Migrate(AppSettings s)
    {
        if (s.SettingsVersion < 2)
        {
            // v2: macOS-style dock (64 px icons, magnification, 75% body over blur).
            var d = new DockSettings();
            s.Dock.IconSize = d.IconSize;
            s.Dock.Magnification = d.Magnification;
            s.Dock.MagnificationRange = d.MagnificationRange;
            s.Appearance.DockOpacity = new AppearanceSettings().DockOpacity;
        }
        s.SettingsVersion = AppSettings.CurrentVersion;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDirectory);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings", ex);
        }
    }
}
