using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public enum ModInstallJobState
{
    Queued,
    Installing,
    Done,
    Failed,
    Cancelled
}

/// <summary>Outcome the page's interactive installer reports back to the queue.</summary>
public enum InstallOutcome
{
    Done,
    Cancelled,
    Failed
}

/// <summary>
/// One mod being added to a profile's load order through the background install queue.
/// State and display properties are mutated on the UI thread (or inline when no dispatcher is
/// attached, which is how tests drive the queue), so UI can bind straight to a job.
/// </summary>
public sealed class ModInstallJob : ObservableObject
{
    private ModInstallJobState _state = ModInstallJobState.Queued;

    public ModInstallJob(Profile profile, ModEntry mod)
    {
        Key = Guid.NewGuid().ToString("N");
        Mod = mod;
        ModId = mod.Id;
        ModName = mod.Name;
        Profile = profile;
        Cancellation = new CancellationTokenSource();
    }

    /// <summary>Stable identity so a bound list can be reconciled instead of rebuilt.</summary>
    public string Key { get; }

    public ModEntry Mod { get; }
    public string ModId { get; }
    public string ModName { get; }
    public Profile Profile { get; }

    /// <summary>
    /// The job's own cancellation source; the interactive installer observes it, so the queue's
    /// Cancel button tears down the running install exactly like a single interactive install.
    /// </summary>
    public CancellationTokenSource Cancellation { get; }

    public ModInstallJobState State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value))
                return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsActive));
        }
    }

    public string StateText => State switch
    {
        ModInstallJobState.Queued => "Queued",
        ModInstallJobState.Installing => "Installing",
        ModInstallJobState.Done => "Done",
        ModInstallJobState.Failed => "Failed",
        _ => "Cancelled"
    };

    public bool IsActive => State is ModInstallJobState.Queued or ModInstallJobState.Installing;
}

/// <summary>
/// Runs mod installs one at a time so a multi-selection from "Add mods…" no longer blocks the UI
/// on the first (potentially multi-gigabyte) archive. Jobs execute serially — disk safety, and at
/// most one interactive dialog (FOMOD wizard, manual destination, or role picker) is open at a
/// time — and a job that needs user input simply runs the page's interactive installer, which the
/// worker awaits, so the queue pauses while the dialog is open. When a dispatcher is attached,
/// job state changes, <see cref="JobChanged"/> and the installer itself run on the UI thread,
/// which is where the dialogs and the InfoBar live; without one (tests) everything runs inline on
/// the worker.
/// </summary>
public sealed class ModInstallQueue
{
    private readonly object _gate = new();
    private readonly Channel<ModInstallJob> _channel = Channel.CreateUnbounded<ModInstallJob>();
    private readonly Task _worker;

    private DispatcherQueue? _dispatcher;
    private Func<Profile, ModEntry, CancellationTokenSource, Task<InstallOutcome>>? _installer;
    private ModInstallJob? _activeJob;
    private int _pendingCount;

    public ModInstallQueue()
    {
        _worker = ProcessAsync();
    }

    /// <summary>Every job in the current batch, oldest first.</summary>
    public ObservableCollection<ModInstallJob> Jobs { get; } = new();

    /// <summary>Raised on the UI thread when a job's state changes, so the page can refresh the InfoBar and status chips.</summary>
    public event EventHandler<ModInstallJob>? JobChanged;

    /// <summary>True while a job is installing or still waiting — drives the InfoBar and Cancel button.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
                return _activeJob is not null || _pendingCount > 0;
        }
    }

    /// <summary>The InfoBar message: names the active mod and its position in the queue.</summary>
    public string StatusText
    {
        get
        {
            lock (_gate)
            {
                if (_activeJob is null)
                    return _pendingCount > 0 ? $"{_pendingCount} install(s) queued" : string.Empty;
                return $"{_activeJob.StateText} {Jobs.IndexOf(_activeJob) + 1} of {Jobs.Count}: {_activeJob.ModName}";
            }
        }
    }

    /// <summary>
    /// The page's interactive installer; it owns the dialogs and reports the outcome. It receives the
    /// job's own <see cref="CancellationTokenSource"/> so the page can wire it to the Cancel button and
    /// the progress dialog. Set before any job is enqueued.
    /// </summary>
    public void SetInstaller(Func<Profile, ModEntry, CancellationTokenSource, Task<InstallOutcome>> installer) => _installer = installer;

    /// <summary>
    /// Attaches the UI thread's dispatcher; the installer and state changes are marshalled through
    /// it. With no dispatcher attached (tests) everything runs inline on the worker.
    /// </summary>
    public void AttachDispatcher(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    /// <summary>Queues <paramref name="mods"/> for <paramref name="profile"/>, in order. Resets the batch view.</summary>
    public void Enqueue(Profile profile, IEnumerable<ModEntry> mods)
    {
        List<ModInstallJob> batch = new();
        lock (_gate)
        {
            if (_pendingCount == 0 && _activeJob is null)
                Jobs.Clear();
            foreach (var mod in mods)
            {
                var job = new ModInstallJob(profile, mod);
                Jobs.Add(job);
                _pendingCount++;
                batch.Add(job);
            }
        }
        foreach (var job in batch)
        {
            _channel.Writer.TryWrite(job);
            NotifyJobChanged(job);
        }
    }

    /// <summary>Teardown helper for tests: completes the channel and waits for the worker.</summary>
    public async Task DisposeAsync()
    {
        _channel.Writer.Complete();
        await _worker;
    }

    private async Task ProcessAsync()
    {
        await foreach (var job in _channel.Reader.ReadAllAsync())
        {
            await ExecuteJobAsync(job);
        }
    }

    private async Task ExecuteJobAsync(ModInstallJob job)
    {
        lock (_gate)
        {
            _activeJob = job;
            _pendingCount = Math.Max(0, _pendingCount - 1);
        }
        SetJobState(job, ModInstallJobState.Installing);

        var installer = _installer;
        var outcome = installer is null
            ? InstallOutcome.Failed
            : await InvokeInstallerAsync(job, installer, job.Cancellation);

        SetJobState(job, outcome switch
        {
            InstallOutcome.Done => ModInstallJobState.Done,
            InstallOutcome.Cancelled => ModInstallJobState.Cancelled,
            _ => ModInstallJobState.Failed
        });

        lock (_gate)
        {
            if (ReferenceEquals(_activeJob, job))
                _activeJob = null;
        }
        NotifyJobChanged(job);
    }

    private async Task<InstallOutcome> InvokeInstallerAsync(ModInstallJob job,
        Func<Profile, ModEntry, CancellationTokenSource, Task<InstallOutcome>> installer, CancellationTokenSource cts)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null)
            return await installer(job.Profile, job.Mod, cts);

        var done = new TaskCompletionSource<InstallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.TryEnqueue(async () =>
        {
            try
            {
                done.TrySetResult(await installer(job.Profile, job.Mod, cts));
            }
            catch (Exception exception)
            {
                done.TrySetException(exception);
            }
        });
        return await done.Task;
    }

    private void SetJobState(ModInstallJob job, ModInstallJobState state)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            _ = dispatcher.TryEnqueue(() => job.State = state);
            return;
        }
        job.State = state;
    }

    private void NotifyJobChanged(ModInstallJob job)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            _ = dispatcher.TryEnqueue(() => JobChanged?.Invoke(this, job));
            return;
        }
        JobChanged?.Invoke(this, job);
    }
}
