using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wildpinkler.App.Services;

public enum AppThemePreference
{
    System,
    Light,
    Dark,
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool IsNavigationPaneOpen { get; set; }
    public bool IsWindowMaximized { get; set; }
    public AppThemePreference Theme { get; set; } = AppThemePreference.System;

    // Restore bounds only; the maximized size is never stored here.
    public int? WindowLeft { get; set; }
    public int? WindowTop { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }

    // User-dragged details-pane width per page; only meaningful in the wide (side-by-side) layout.
    public double? GamesDetailsWidth { get; set; }
    public double? ToolsDetailsWidth { get; set; }
    public double? ModsDetailsWidth { get; set; }
    public double? DownloadsDetailsWidth { get; set; }
    public bool ConfirmRemoteDownloads { get; set; } = true;
    public double? ProfilesListWidth { get; set; }
}

// Deliberately synchronous, unlike the other stores: both call sites are window lifecycle points
// (constructor and AppWindow.Closing) where awaiting is not available.
public sealed class AppSettingsStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler", "app-settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new AppSettings();

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions);
            if (settings is null || settings.SchemaVersion > CurrentSchemaVersion)
                return new AppSettings();

            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        settings.SchemaVersion = CurrentSchemaVersion;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, _path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
