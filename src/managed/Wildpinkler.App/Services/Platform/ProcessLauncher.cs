using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Wildpinkler.App.Services;

public interface ILaunchedProcess : IDisposable
{
    Task<int> WaitForExitAsync();
}

public interface IProcessLauncher
{
    ILaunchedProcess Start(ProcessStartInfo startInfo);
}

public sealed class ProcessLauncher : IProcessLauncher
{
    public ILaunchedProcess Start(ProcessStartInfo startInfo) =>
        new LaunchedProcess(Process.Start(startInfo) ?? throw new InvalidOperationException("The loader process did not start."));

    private sealed class LaunchedProcess : ILaunchedProcess
    {
        private readonly Process _process;

        public LaunchedProcess(Process process) => _process = process;

        public async Task<int> WaitForExitAsync()
        {
            await _process.WaitForExitAsync();
            return _process.ExitCode;
        }

        public void Dispose() => _process.Dispose();
    }
}