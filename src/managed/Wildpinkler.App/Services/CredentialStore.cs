using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wildpinkler.App.Services;

/// <summary>
/// Stores site credentials encrypted with DPAPI for the current Windows user. Additional entropy is
/// generated once per installation, so ciphertext is not portable between machines and a stealer
/// cannot decrypt it from a hard-coded constant alone.
/// </summary>
public sealed class CredentialStore : IDisposable
{
    private const int EntropyByteCount = 32;
    private static readonly byte[] LegacyEntropy = "Wildpinkler credentials"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly string _path;
    private readonly string _entropyPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private byte[]? _entropy;

    public CredentialStore(string? root = null)
    {
        var directory = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler");
        _path = Path.Combine(directory, "credentials.json");
        _entropyPath = Path.Combine(directory, "credentials.entropy");
    }

    public async Task<string?> GetAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            var values = await LoadAsync();
            if (!values.TryGetValue(key, out var protectedValue))
                return null;

            if (TryUnprotect(protectedValue, GetOrCreateEntropy(), out var value))
                return value;

            // Written by a build that used the shared constant; re-protect it under this install's entropy.
            if (!TryUnprotect(protectedValue, LegacyEntropy, out var legacyValue))
                return null;

            values[key] = Protect(legacyValue);
            await SaveAsync(values);
            return legacyValue;
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
                values[key] = Protect(value);

            await SaveAsync(values);
        }
        finally
        {
            _lock.Release();
        }
    }

    private string Protect(string value) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(value), GetOrCreateEntropy(), DataProtectionScope.CurrentUser));

    private static bool TryUnprotect(string protectedValue, byte[] entropy, out string value)
    {
        try
        {
            value = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), entropy, DataProtectionScope.CurrentUser));
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            value = string.Empty;
            return false;
        }
    }

    private byte[] GetOrCreateEntropy()
    {
        if (_entropy is not null)
            return _entropy;

        Directory.CreateDirectory(Path.GetDirectoryName(_entropyPath)!);
        if (File.Exists(_entropyPath))
        {
            var existing = File.ReadAllBytes(_entropyPath);
            if (existing.Length == EntropyByteCount)
                return _entropy = existing;
        }

        var generated = RandomNumberGenerator.GetBytes(EntropyByteCount);
        File.WriteAllBytes(_entropyPath, generated);
        return _entropy = generated;
    }

    private async Task SaveAsync(Dictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(values, JsonOptions));
        File.Move(temporaryPath, _path, true);
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
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Dispose() => _lock.Dispose();
}
