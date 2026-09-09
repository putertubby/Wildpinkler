using System;
using System.Collections.Generic;
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
    public double? ModListsDetailsWidth { get; set; }
    public bool ConfirmRemoteDownloads { get; set; } = true;
    public double? ProfilesListWidth { get; set; }
    public string? ActivePageTag { get; set; }
    public Microsoft.Extensions.Logging.LogLevel LogLevel { get; set; } = Microsoft.Extensions.Logging.LogLevel.Information;

    // Assistant provider selection. The API key never lives here; it is held by CredentialStore.
    public string AssistantProviderId { get; set; } = Agent.AiProviderPresets.DefaultProviderId;

    // Null means "use the preset value", so changing a preset default reaches users who never overrode it.
    public string? AssistantEndpoint { get; set; }
    public string? AssistantModelId { get; set; }
    public bool AssistantPersistTranscript { get; set; } = true;
    public Agent.AssistantMode AssistantMode { get; set; } = Agent.AssistantMode.Agent;
    public bool AssistantOffersAllTools { get; set; }
    public bool AssistantShowUsage { get; set; }

    // Hosts the user has agreed may receive conversation content. Loopback is never listed.
    public List<string> AssistantAcceptedRemoteHosts { get; set; } = [];
    public bool IsAssistantPaneOpen { get; set; }
    public double? AssistantPaneWidth { get; set; }
}

// Deliberately synchronous, unlike the other stores: both call sites are window lifecycle points
// (constructor and AppWindow.Closing) where awaiting is not available.
public sealed class AppSettingsStore
{
    private const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string _path;

    public AppSettingsStore(string? root = null)
    {
        var directory = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler");
        _path = Path.Combine(directory, "app-settings.json");
    }

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
