using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wildpinkler.App.Services;

public sealed class CredentialStore
{
    private static readonly byte[] Entropy = "Wildpinkler credentials"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler", "credentials.json");
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<string?> GetAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            var values = await LoadAsync();
            if (!values.TryGetValue(key, out var protectedValue))
                return null;
            try
            {
                return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetAsync(string key, string? value)
    {
        await _lock.WaitAsync();
        try
        {
            var values = await LoadAsync();
            if (string.IsNullOrWhiteSpace(value))
                values.Remove(key);
            else
                values[key] = Convert.ToBase64String(ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = _path + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(values, JsonOptions));
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, string>> LoadAsync()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, JsonOptions)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}