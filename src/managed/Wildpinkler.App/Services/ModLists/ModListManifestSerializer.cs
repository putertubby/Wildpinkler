using System.IO;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public sealed class ModListManifestSerializer
{
    public const string FileExtension = ".wpmodlist.json";
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<ModListManifest> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException("The mod-list file does not exist.", path);
        if (file.Length > MaxFileBytes)
            throw new InvalidDataException("The mod-list file is too large.");

        await using var stream = file.OpenRead();
        var manifest = await JsonSerializer.DeserializeAsync<ModListManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new JsonException("The mod-list file is empty.");
        ModListManifestValidator.EnsureValid(manifest);
        return manifest;
    }

    public async Task SaveAsync(ModListManifest manifest, string path, CancellationToken cancellationToken = default)
    {
        ModListManifestValidator.EnsureValid(manifest);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";

        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public string Serialize(ModListManifest manifest)
    {
        ModListManifestValidator.EnsureValid(manifest);
        return JsonSerializer.Serialize(manifest, JsonOptions);
    }
}
