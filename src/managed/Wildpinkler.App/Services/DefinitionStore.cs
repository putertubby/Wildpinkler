using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public enum DefinitionImportStatus
{
    Imported,
    Replaced,
    Invalid,
    AlreadyExists,
    ConflictsWithBuiltIn
}

public sealed record DefinitionImportResult<TDefinition>(DefinitionImportStatus Status, TDefinition? Definition, string Message)
    where TDefinition : class, IDefinition;

public sealed record DefinitionCatalog<TDefinition>(IReadOnlyList<TDefinition> Definitions, IReadOnlyList<string> Warnings)
    where TDefinition : class, IDefinition;

/// <summary>
/// Two-layer definition catalog: read-only built-ins shipped with the app, overridden by id from a
/// writable user layer. A single invalid file is reported as a warning and never breaks the catalog.
/// </summary>
public abstract class DefinitionStore<TDefinition> where TDefinition : class, IDefinition
{
    public const string PickerFileExtension = ".json";

    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _builtInDirectory;
    private readonly string _userDirectory;

    protected DefinitionStore()
    {
        _builtInDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", BuiltInDirectoryName);
        _userDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler", UserDirectoryName);
    }

    /// <summary>The kind-specific suffix, for example ".wpgame"; the stored files add ".json".</summary>
    protected abstract string ShortExtension { get; }

    protected abstract string BuiltInDirectoryName { get; }

    protected abstract string UserDirectoryName { get; }

    protected abstract bool Validate(TDefinition definition, out string error);

    public string FileExtension => ShortExtension + PickerFileExtension;

    public string UserDirectory => _userDirectory;

    /// <summary>The name to suggest in a save picker; the picker appends ".json" itself.</summary>
    public string SuggestFileName(TDefinition definition) => definition.DefinitionId + ShortExtension;

    /// <summary>Built-in definitions first, then user definitions overriding them by id.</summary>
    public async Task<DefinitionCatalog<TDefinition>> LoadAsync()
    {
        var warnings = new List<string>();
        var definitions = new Dictionary<string, TDefinition>(StringComparer.OrdinalIgnoreCase);

        await LoadDirectoryAsync(_builtInDirectory, DefinitionSource.BuiltIn, definitions, warnings);
        await LoadDirectoryAsync(_userDirectory, DefinitionSource.User, definitions, warnings);

        var ordered = definitions.Values.OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new DefinitionCatalog<TDefinition>(ordered, warnings);
    }

    public async Task<DefinitionImportResult<TDefinition>> ImportAsync(string sourcePath, bool allowOverwrite)
    {
        TDefinition definition;
        try
        {
            definition = await ReadAsync(sourcePath, DefinitionSource.User);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
        {
            return new DefinitionImportResult<TDefinition>(DefinitionImportStatus.Invalid, null,
                $"{Path.GetFileName(sourcePath)}: {exception.Message}");
        }

        if (await BuiltInExistsAsync(definition.DefinitionId))
        {
            return new DefinitionImportResult<TDefinition>(DefinitionImportStatus.ConflictsWithBuiltIn, definition,
                $"'{definition.DefinitionId}' is a built-in definition and cannot be replaced.");
        }

        var targetPath = Path.Combine(_userDirectory, definition.DefinitionId + FileExtension);
        var exists = File.Exists(targetPath);
        if (exists && !allowOverwrite)
        {
            return new DefinitionImportResult<TDefinition>(DefinitionImportStatus.AlreadyExists, definition,
                $"'{definition.DefinitionId}' is already imported.");
        }

        await WriteAsync(targetPath, definition);
        definition.FilePath = targetPath;
        return new DefinitionImportResult<TDefinition>(
            exists ? DefinitionImportStatus.Replaced : DefinitionImportStatus.Imported,
            definition,
            $"{definition.Name} (v{definition.DefinitionVersion}) {(exists ? "replaced" : "imported")}.");
    }

    public Task ExportAsync(TDefinition definition, string targetPath) => WriteAsync(targetPath, definition);

    /// <summary>Writes to the user layer, which overrides a built-in definition with the same id.</summary>
    public async Task<TDefinition> SaveUserAsync(TDefinition definition)
    {
        if (!Validate(definition, out var error))
            throw new InvalidDataException(error);

        var targetPath = Path.Combine(_userDirectory, definition.DefinitionId + FileExtension);
        await WriteAsync(targetPath, definition);
        definition.Source = DefinitionSource.User;
        definition.FilePath = targetPath;
        return definition;
    }

    public Task DeleteUserAsync(string definitionId)
    {
        var path = Path.Combine(_userDirectory, definitionId + FileExtension);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private async Task<bool> BuiltInExistsAsync(string definitionId)
    {
        if (!Directory.Exists(_builtInDirectory))
            return false;

        var builtIn = new Dictionary<string, TDefinition>(StringComparer.OrdinalIgnoreCase);
        await LoadDirectoryAsync(_builtInDirectory, DefinitionSource.BuiltIn, builtIn, new List<string>());
        return builtIn.ContainsKey(definitionId);
    }

    private async Task LoadDirectoryAsync(
        string directory,
        DefinitionSource source,
        IDictionary<string, TDefinition> definitions,
        ICollection<string> warnings)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var path in Directory.EnumerateFiles(directory, "*" + FileExtension))
        {
            try
            {
                var definition = await ReadAsync(path, source);
                definitions[definition.DefinitionId] = definition;
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
            {
                // A single bad file must never take down the whole catalog.
                warnings.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }
    }

    private async Task<TDefinition> ReadAsync(string path, DefinitionSource source)
    {
        var info = new FileInfo(path);
        if (info.Length > DefinitionValidation.MaxFileBytes)
            throw new InvalidDataException($"The file is larger than {DefinitionValidation.MaxFileBytes / 1024} KB.");

        await using var stream = File.OpenRead(path);
        var definition = await JsonSerializer.DeserializeAsync<TDefinition>(stream, JsonOptions)
            ?? throw new InvalidDataException("The file does not contain a definition.");

        if (!Validate(definition, out var error))
            throw new InvalidDataException(error);

        definition.Source = source;
        definition.FilePath = path;
        return definition;
    }

    private static async Task WriteAsync(string path, TDefinition definition)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";

        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, definition, JsonOptions);
            await stream.FlushAsync();
        }

        File.Move(temporaryPath, path, true);
    }
}
