using System;
using System.IO;
using Microsoft.Win32;

namespace Wildpinkler.Remote.Nexus;

/// <summary>
/// Registers this application as the <c>nxm:</c> handler under HKCU, preserving whatever handler was
/// there before so uninstalling the association hands it back rather than deleting it.
/// </summary>
public sealed class NxmProtocolRegistrar : IRemoteProtocolRegistrar
{
    private const string ProtocolKey = @"Software\Classes\nxm";
    private const string CommandKey = ProtocolKey + @"\shell\open\command";

    private readonly string _backupPath;
    private readonly Func<string> _commandFactory;

    public NxmProtocolRegistrar(string? backupPath = null, Func<string>? commandFactory = null)
    {
        _backupPath = backupPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler", "nxm-handler.backup");
        _commandFactory = commandFactory ?? (() => $"\"{Environment.ProcessPath}\" \"%1\"");
    }

    public string Scheme => "nxm";

    public bool IsRegistered() =>
        string.Equals(GetCurrentOwnerCommand(), _commandFactory(), StringComparison.OrdinalIgnoreCase);

    public string? GetCurrentOwnerCommand()
    {
        using var command = Registry.CurrentUser.OpenSubKey(CommandKey);
        return command?.GetValue(null) as string;
    }

    public void Register()
    {
        var desired = _commandFactory();
        var previous = GetCurrentOwnerCommand();
        if (!string.IsNullOrWhiteSpace(previous) && !string.Equals(previous, desired, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
            File.WriteAllText(_backupPath, previous);
        }

        using (var protocol = Registry.CurrentUser.CreateSubKey(ProtocolKey))
        {
            protocol!.SetValue(null, "URL:nxm Protocol");
            protocol.SetValue("URL Protocol", string.Empty);
        }

        using (var command = Registry.CurrentUser.CreateSubKey(CommandKey))
            command!.SetValue(null, desired);

        if (!IsRegistered())
            throw new InvalidOperationException("The nxm: association could not be written. Another application may be enforcing it.");
    }

    public void Unregister()
    {
        if (!IsRegistered())
            return;

        var previous = File.Exists(_backupPath) ? File.ReadAllText(_backupPath) : null;
        if (string.IsNullOrWhiteSpace(previous))
        {
            Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, false);
            return;
        }

        using (var command = Registry.CurrentUser.CreateSubKey(CommandKey))
            command!.SetValue(null, previous);
        File.Delete(_backupPath);
    }
}
