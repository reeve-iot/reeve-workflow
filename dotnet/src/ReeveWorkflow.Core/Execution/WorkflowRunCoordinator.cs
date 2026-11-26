using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>Per-workflow concurrency arbiter: every mode funnels through <see cref="AcquireAsync"/>, returning a
/// lease the caller MUST dispose or a skip/drop/error decision. Cluster gating extends the budget fleet-wide.</summary>
public sealed class WorkflowRunCoordinator : IDisposable
{
    private readonly Guid _workflowId;
    private readonly IWorkflowMetrics _metrics;
    private readonly ILogger? _logger;
    private readonly object _stateLock = new();

    private readonly IWorkflowRunLockProvider? _lockProvider;

    private readonly IWorkflowRunSlotGate? _slotGate;

    private readonly bool _isMultiReplica;

    private readonly IWorkflowClusterSignal? _clusterSignal;
    private readonly string _restartChannel;
    private IDisposable? _restartCancelSubscription;

    private readonly IWorkflowRestartGenerations _restartGenerations;

    private readonly TimeSpan _restartWatchInterval;

    private readonly CancellationTokenSource _lifetime = new();

    private string ClusterRunBudgetLockKey => $"WorkflowRunBudget_{_workflowId:N}";

    private static readonly TimeSpan ClusterRunBudgetRetryBudget = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan ClusterRunBudgetRetryInterval = TimeSpan.FromMilliseconds(25);

    internal static readonly TimeSpan RestartWaiterLiveFor = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan RestartSayAgainEvery = TimeSpan.FromSeconds(1);

    private const int RestartTouchEverySayings = 2;

    private static readonly TimeSpan RestartPollFirst = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan RestartPollMost = TimeSpan.FromMilliseconds(250);

    private const int RestartReadEveryPolls = 4;

    private static readonly TimeSpan DefaultRestartWatchInterval = TimeSpan.FromSeconds(10);

    private readonly Dictionary<Guid, CancellationTokenSource> _activeRuns = new();

    private readonly Dictionary<Guid, long> _runGenerations = new();

    private readonly SortedSet<long> _waitingHere = [];

    private long _newestStartedHere;

    private readonly SemaphoreSlim _semaphore;

    private int _permitDebt;

    private int _queuedWaiters;
    private int _admissionPins;
    private bool _retired;
    private WorkflowMode _mode;
    private int _maxConcurrentRuns;
    private int _maxQueuedRuns;
    private WorkflowOverflowBehavior _overflowBehavior;
    private volatile bool _disposed;

    private bool Forgotten => _disposed || _lifetime.IsCancellationRequested;

    /// <summary>A coordinator whose Restart triggers are ordered in this process alone.</summary>
    /// <remarks>Right for one replica. A host with peers passes the generations every replica reads, through the
    /// overload that takes them: with this one each replica orders only its own triggers.</remarks>
    public WorkflowRunCoordinator(
        Guid workflowId,
        WorkflowMode mode,
        int maxConcurrentRuns,
        int maxQueuedRuns,
        WorkflowOverflowBehavior overflowBehavior,
        IWorkflowMetrics metrics,
        ILogger? logger = null,
        IWorkflowRunLockProvider? lockProvider = null,
        IWorkflowRunSlotGate? slotGate = null,
        bool isMultiReplica = false,
        IWorkflowClusterSignal? clusterSignal = null,
        string? restartChannel = null)
        : this(workflowId, mode, maxConcurrentRuns, maxQueuedRuns, overflowBehavior, metrics, logger, lockProvider,
            slotGate, isMultiReplica, clusterSignal, restartChannel, new InProcessRestartGenerations())
    {
    }

    /// <summary>A coordinator whose Restart triggers are ordered by <paramref name="restartGenerations"/>.</summary>
    /// <param name="workflowId">The workflow whose runs this coordinator admits.</param>
    /// <param name="mode">How a trigger that finds the workflow running is admitted.</param>
    /// <param name="maxConcurrentRuns">How many runs may go at once, at least one.</param>
    /// <param name="maxQueuedRuns">How many triggers may wait under Queued.</param>
    /// <param name="overflowBehavior">What a trigger that finds no room does.</param>
    /// <param name="metrics">Where admission outcomes are recorded.</param>
    /// <param name="logger">Where what could not be done is logged; null logs nothing.</param>
    /// <param name="lockProvider">The cluster run-budget lock for the modes with one place; null for none.</param>
    /// <param name="slotGate">The cluster slot gate for Parallel and Queued; null for none.</param>
    /// <param name="isMultiReplica">Whether the workflow runs on peers as well.</param>
    /// <param name="clusterSignal">Where a Restart cancel is said to the peers; null says it to nobody.</param>
    /// <param name="restartChannel">The channel the Restart cancel is said on.</param>
    /// <param name="restartGenerations">Where every replica reads which Restart trigger is newest.</param>
    /// <param name="restartWatchInterval">How often a running Restart run on a replica with peers looks for a newer
    /// trigger waiting for it, which is what stops it when the cancel on the signal was lost. Null is ten seconds.</param>
    public WorkflowRunCoordinator(
        Guid workflowId,
        WorkflowMode mode,
        int maxConcurrentRuns,
        int maxQueuedRuns,
        WorkflowOverflowBehavior overflowBehavior,
        IWorkflowMetrics metrics,
        ILogger? logger,
        IWorkflowRunLockProvider? lockProvider,
        IWorkflowRunSlotGate? slotGate,
        bool isMultiReplica,
        IWorkflowClusterSignal? clusterSignal,
        string? restartChannel,
        IWorkflowRestartGenerations restartGenerations,
        TimeSpan? restartWatchInterval = null)
        : this(workflowId, mode, maxConcurrentRuns, maxQueuedRuns, overflowBehavior, metrics, logger,
            lockProvider, slotGate, isMultiReplica, clusterSignal, restartChannel, restartGenerations,
            restartWatchInterval, subscribeToClusterSignal: true)
    {
    }

    internal WorkflowRunCoordinator(
        Guid workflowId, WorkflowMode mode, int maxConcurrentRuns, int maxQueuedRuns,
        WorkflowOverflowBehavior overflowBehavior, IWorkflowMetrics metrics, ILogger? logger,
        IWorkflowRunLockProvider? lockProvider, IWorkflowRunSlotGate? slotGate, bool isMultiReplica,
        IWorkflowClusterSignal? clusterSignal, string? restartChannel,
        IWorkflowRestartGenerations restartGenerations, TimeSpan? restartWatchInterval,
        bool subscribeToClusterSignal)
    {
        if (maxConcurrentRuns < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentRuns));
        if (maxQueuedRuns < 0)
            throw new ArgumentOutOfRangeException(nameof(maxQueuedRuns));
        ArgumentNullException.ThrowIfNull(restartGenerations);
        if (restartWatchInterval is { } watch && watch <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(restartWatchInterval));

        _workflowId = workflowId;
        _metrics = metrics;
        _logger = logger;
        _lockProvider = lockProvider;
        _slotGate = slotGate;
        _isMultiReplica = isMultiReplica;
        _mode = mode;
        _maxConcurrentRuns = maxConcurrentRuns;
        _maxQueuedRuns = maxQueuedRuns;
        _overflowBehavior = overflowBehavior;
        _clusterSignal = clusterSignal;
        _restartChannel = restartChannel ?? new WorkflowClusterSignalChannels().RestartCancel;
        _restartGenerations = restartGenerations;
        _restartWatchInterval = restartWatchInterval ?? DefaultRestartWatchInterval;

        _restartCancelSubscription = subscribeToClusterSignal
            ? clusterSignal?.Subscribe(
                _restartChannel, (id, payload, _) => { ApplyRestartCancel(id, payload); return Task.CompletedTask; })
            : null;

        var permits = mode is WorkflowMode.Single or WorkflowMode.Restart ? 1 : maxConcurrentRuns;
        _semaphore = new SemaphoreSlim(permits, int.MaxValue);
    }

    /// <summary>Number of active runs (semaphore permits currently held). Test-visible accessor.</summary>
    public int ActiveRunCount
    {
        get
        {
            lock (_stateLock)
            {
                return _activeRuns.Count;
            }
        }
    }

    /// <summary>Snapshot of active run ids, so the workflow-deleted handler can write a final cancelled-by-delete
    /// audit entry per in-flight run before disposal. Empty when no runs are in flight.</summary>
    public IReadOnlyList<Guid> SnapshotActiveRunIds()
    {
        lock (_stateLock)
        {
            if (_activeRuns.Count == 0)
                return Array.Empty<Guid>();
            return _activeRuns.Keys.ToArray();
        }
    }

    /// <summary>How many Queued triggers are waiting for a permit. Waiters that queued before a shrink or a mode
    /// change keep their place and are counted until they start or their caller gives up.</summary>
    public int QueueDepth
    {
        get { lock (_stateLock) return _queuedWaiters; }
    }

    internal bool TryPinAdmission(out AdmissionPin pin)
    {
        lock (_stateLock)
        {
            if (_retired || _disposed)
            {
                pin = default;
                return false;
            }
            _admissionPins++;
            pin = new AdmissionPin(this);
            return true;
        }
    }

    internal bool TryRetireIfIdle()
    {
        lock (_stateLock)
        {
            if (_retired || _disposed || _activeRuns.Count != 0 || _queuedWaiters != 0
                || _waitingHere.Count != 0 || _admissionPins != 0)
                return false;
            _retired = true;
            return true;
        }
    }

    internal readonly struct AdmissionPin(WorkflowRunCoordinator owner) : IDisposable
    {
        private readonly WorkflowRunCoordinator? _owner = owner;
        public void Dispose()
        {
            if (_owner is null) return;
            lock (_owner._stateLock) _owner._admissionPins--;
        }
    }

    public WorkflowMode Mode
    {
        get { lock (_stateLock) return _mode; }
    }

    /// <summary>Tries to acquire a run slot per the coordinator's mode. A non-null <see cref="WorkflowRunDecision"/>
    /// lease MUST be disposed in a finally; otherwise the outcome describes the rejection.</summary>
    public Task<WorkflowRunDecision> AcquireAsync(CancellationToken cancellationToken) =>
        AcquireAsync(parkedSleeps: null, onQueued: null, cancellationToken);

    /// <summary>The same acquire, for a host whose runs can park on a sleep. Single then also skips while one of
    /// this workflow's runs is parked on one, and Restart reports <c>restarted</c> when it supersedes one.</summary>
    /// <remarks><c>onQueued</c> is called once a Queued run's place is fixed, before it waits for its
    /// slot and outside every lock. It must be fast; an exception from it is logged and ignored.</remarks>
    public async Task<WorkflowRunDecision> AcquireAsync(
        IWorkflowParkedSleepStore? parkedSleeps, Action? onQueued, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        WorkflowMode mode;
        WorkflowOverflowBehavior overflow;
        lock (_stateLock)
        {
            mode = _mode;
            overflow = _overflowBehavior;
        }

        return mode switch
        {
            WorkflowMode.Single => await AcquireSingleAsync(parkedSleeps, cancellationToken).ConfigureAwait(false),
            WorkflowMode.Restart => await AcquireRestartAsync(parkedSleeps, cancellationToken).ConfigureAwait(false),
            WorkflowMode.Queued => await AcquireQueuedAsync(overflow, onQueued, cancellationToken).ConfigureAwait(false),
            WorkflowMode.Parallel => await AcquireParallelAsync(overflow, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unknown workflow mode {mode}.")
        };
    }

    /// <summary>A place for a parked run coming back, or null when the workflow has none free now. Never waits,
    /// never queues and never supersedes: the run is older than whatever holds the place, and the wait it came
    /// back on is put back and asked for again on a later pass.</summary>
    /// <remarks>A resumed run holds the place like any run does, so a Restart trigger cancels
    /// it and a Single trigger is skipped while it runs. No admission outcome is recorded: a resume is not a
    /// trigger, and one that finds no room is asked again rather than dropped. Under Restart a resumed run is
    /// generation zero, older than every trigger, so it is not started while any trigger is waiting, and the watch
    /// stops it for a trigger that starts or waits after it — not for the one that started it before it parked.</remarks>
    public async Task<WorkflowRunLease?> TryAcquireForResumeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        WorkflowMode mode;
        int maxConcurrentRuns;
        lock (_stateLock)
        {
            mode = _mode;
            maxConcurrentRuns = _maxConcurrentRuns;
            if (mode is WorkflowMode.Restart && _waitingHere.Count > 0)
                return null;
        }

        if (mode is WorkflowMode.Restart
            && await ReadRestartGenerationsQuietlyAsync(cancellationToken).ConfigureAwait(false) is { NewestWaiting: not null })
            return null;

        if (!_semaphore.Wait(0, cancellationToken))
            return null;

        (bool Contended, IWorkflowRunLock? Lock) clusterLock = (false, null);
        string? slotToken = null;
        try
        {
            if (mode is WorkflowMode.Single or WorkflowMode.Restart)
            {
                clusterLock = await TryAcquireClusterRunBudgetAsync(cancellationToken).ConfigureAwait(false);
                if (clusterLock.Contended)
                {
                    ReturnPermit();
                    return null;
                }
            }
            else
            {
                (var admitted, slotToken) = await TryAcquireClusterSlotAsync(maxConcurrentRuns, cancellationToken)
                    .ConfigureAwait(false);
                if (!admitted)
                {
                    ReturnPermit();
                    return null;
                }
            }
        }
        catch
        {
            ReturnPermit();
            throw;
        }

        if (mode is WorkflowMode.Single)
            return CreateLeaseLocked(cancellationToken, mode, clusterLock.Lock);
        if (mode is not WorkflowMode.Restart)
            return CreateLeaseLocked(cancellationToken, mode, slotToken: slotToken);

        WorkflowRestartGenerationState? placed;
        try
        {
            placed = await ReadRestartGenerationsQuietlyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (clusterLock.Lock is not null)
                await DisposeLockQuietlyAsync(clusterLock.Lock).ConfigureAwait(false);
            ReturnPermit();
            throw;
        }

        if (placed is { NewestWaiting: not null })
        {
            if (clusterLock.Lock is not null)
                await DisposeLockQuietlyAsync(clusterLock.Lock).ConfigureAwait(false);
            ReturnPermit();
            return null;
        }

        var startedBeforeResume = placed?.Started;
        var lease = CreateLeaseLocked(cancellationToken, mode, clusterLock.Lock, restartGeneration: 0);
        WatchRestartRun(lease.RunId, 0, lease.RunCancellationSource, startedBeforeResume);
        return lease;
    }

    private async Task<WorkflowRunDecision> AcquireSingleAsync(
        IWorkflowParkedSleepStore? parkedSleeps, CancellationToken cancellationToken)
    {
        if (!_semaphore.Wait(0, cancellationToken))
        {
            RecordOutcome(WorkflowMode.Single, WorkflowAdmissionOutcomes.Skipped);
            return new WorkflowRunDecision(
                WorkflowMode.Single,
                WorkflowAdmissionOutcomes.Skipped,
                Lease: null,
                Reason: "single_mode_already_running");
        }

        (bool Contended, IWorkflowRunLock? Lock) clusterLock;
        try
        {
            clusterLock = await TryAcquireClusterRunBudgetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReturnPermit();
            throw;
        }

        if (clusterLock.Contended)
        {
            ReturnPermit();
            RecordOutcome(WorkflowMode.Single, WorkflowAdmissionOutcomes.Skipped);
            return new WorkflowRunDecision(
                WorkflowMode.Single,
                WorkflowAdmissionOutcomes.Skipped,
                Lease: null,
                Reason: "single_mode_already_running_on_another_replica");
        }

        bool parked;
        try
        {
            parked = await IsParkedOnASleepAsync(parkedSleeps, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (clusterLock.Lock is not null)
                await DisposeLockQuietlyAsync(clusterLock.Lock).ConfigureAwait(false);
            ReturnPermit();
            throw;
        }

        if (parked)
        {
            if (clusterLock.Lock is not null)
                await DisposeLockQuietlyAsync(clusterLock.Lock).ConfigureAwait(false);
            ReturnPermit();
            RecordOutcome(WorkflowMode.Single, WorkflowAdmissionOutcomes.Skipped);
            return new WorkflowRunDecision(
                WorkflowMode.Single,
                WorkflowAdmissionOutcomes.Skipped,
                Lease: null,
                Reason: "single_mode_parked_sleep");
        }

        var lease = CreateLeaseLocked(cancellationToken, WorkflowMode.Single, clusterLock.Lock);
        return await RecordStartedAsync(new WorkflowRunDecision(
            WorkflowMode.Single,
            WorkflowAdmissionOutcomes.Started,
            lease,
            Reason: null)).ConfigureAwait(false);
    }

    private async Task<WorkflowRunDecision> AcquireRestartAsync(
        IWorkflowParkedSleepStore? parkedSleeps, CancellationToken cancellationToken)
    {
        long generation;
        var issuing = StartIssue();
        try
        {
            generation = await WaitForIssueAsync(issuing, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or ObjectDisposedException)
                                   || !(cancellationToken.IsCancellationRequested || Forgotten))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Forgotten, this);
            var issuedAfterFailure = await ReadIssuedAfterFailedIssueAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Forgotten, this);
            _logger?.LogWarning(ex,
                "Workflow {WorkflowId}: no Restart generation could be issued; this trigger stops every run and asks "
                + "for the cluster lock for a bounded while.",
                _workflowId);
            return await AcquireRestartWithoutGenerationAsync(issuedAfterFailure, parkedSleeps, cancellationToken).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested || Forgotten)
        {
            await RetireRestartGenerationQuietlyAsync(generation).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new ObjectDisposedException(nameof(WorkflowRunCoordinator));
        }

        bool hadExisting;
        lock (_stateLock)
        {
            _waitingHere.Add(generation);
            hadExisting = _activeRuns.Count > 0;
        }

        hadExisting = hadExisting || await IsParkedOnASleepAsync(parkedSleeps, cancellationToken).ConfigureAwait(false);

        return await WaitAndStartRestartAsync(
            generation, ordered: true, live: true, promoted: false, hasPermit: false, hadExisting, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkflowRunDecision> WaitAndStartRestartAsync(
        long generation, bool ordered, bool live, bool promoted, bool hasPermit, bool hadExisting,
        CancellationToken cancellationToken, long? waitingAbove = null)
    {
        var storeWaitingAbove = waitingAbove ?? generation;
        using var keepalive = live ? CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token) : null;
        var keeping = Task.CompletedTask;
        var started = false;
        try
        {
            if (live)
            {
                CancelRunsOlderThan(generation);
                PublishRestartCancel(generation);
                keeping = KeepWaitingAsync(generation, keepalive!.Token);
                if (hadExisting)
                    RecordOutcome(WorkflowMode.Restart, WorkflowAdmissionOutcomes.Restarted);
            }

            while (true)
            {
                if (!hasPermit && !await WaitForRestartPermitAsync(generation, cancellationToken).ConfigureAwait(false))
                    return StoodAside();
                hasPermit = false;

                (RestartLockAnswer Answer, IWorkflowRunLock? Lock) held;
                try
                {
                    held = await WaitForRestartLockAsync(generation, ordered, live, storeWaitingAbove, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    ReturnPermit();
                    throw;
                }

                if (held.Answer is RestartLockAnswer.Acquired)
                {
                    WorkflowRestartGenerationState? state;
                    try
                    {
                        state = await ReadRestartGenerationsQuietlyAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        if (held.Lock is not null)
                            await DisposeLockQuietlyAsync(held.Lock).ConfigureAwait(false);
                        ReturnPermit();
                        throw;
                    }

                    if (state is { } read && ordered && StartedPast(read.Started, generation, live))
                        held.Answer = RestartLockAnswer.NewerStarted;
                    else if (state is { NewestWaiting: { } newest } && newest > storeWaitingAbove)
                        held.Answer = RestartLockAnswer.NewerWaiting;
                }

                if (held.Answer is RestartLockAnswer.Acquired)
                {
                    var runId = Guid.NewGuid();
                    var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var disposed = false;
                    lock (_stateLock)
                    {
                        if (_disposed)
                            disposed = true;
                        else if (ordered && StartedPast(_newestStartedHere, generation, live))
                            held.Answer = RestartLockAnswer.NewerStarted;
                        else if (_waitingHere.Count > 0 && _waitingHere.Max > generation)
                            held.Answer = RestartLockAnswer.NewerWaiting;
                        else
                        {
                            _activeRuns[runId] = runCts;
                            _runGenerations[runId] = generation;
                        }
                    }

                    if (disposed)
                    {
                        runCts.Dispose();
                        if (held.Lock is not null)
                            await DisposeLockQuietlyAsync(held.Lock).ConfigureAwait(false);
                        ReturnPermit();
                        throw new ObjectDisposedException(nameof(WorkflowRunCoordinator));
                    }

                    if (held.Answer is RestartLockAnswer.Acquired)
                    {
                        var lease = new WorkflowRunLease(this, runId, runCts, WorkflowMode.Restart, held.Lock, slotToken: null);
                        try
                        {
                            if (!promoted)
                                RecordOutcome(WorkflowMode.Restart, WorkflowAdmissionOutcomes.Started);
                        }
                        catch
                        {
                            await lease.DisposeAsync().ConfigureAwait(false);
                            throw;
                        }

                        lock (_stateLock)
                        {
                            if (generation > _newestStartedHere) _newestStartedHere = generation;
                        }

                        started = true;
                        if (ordered && generation > 0)
                        {
                            await MarkRestartStartedQuietlyAsync(generation).ConfigureAwait(false);
                            if (Forgotten)
                            {
                                await lease.DisposeAsync().ConfigureAwait(false);
                                throw new ObjectDisposedException(nameof(WorkflowRunCoordinator));
                            }
                        }
                        WatchRestartRun(runId, generation, runCts, startedMark: ordered ? generation : null, waitingAbove: waitingAbove);

                        return promoted
                            ? new WorkflowRunDecision(WorkflowMode.Queued, WorkflowAdmissionOutcomes.Queued, lease, Reason: null)
                            : new WorkflowRunDecision(
                                WorkflowMode.Restart,
                                hadExisting ? WorkflowAdmissionOutcomes.Restarted : WorkflowAdmissionOutcomes.Started,
                                lease,
                                Reason: null);
                    }

                    runCts.Dispose();
                }

                if (held.Lock is not null)
                    await DisposeLockQuietlyAsync(held.Lock).ConfigureAwait(false);
                ReturnPermit();

                if (held.Answer is RestartLockAnswer.NewerStarted || !live)
                    return StoodAside();
                if (await GiveWayAsync(generation, cancellationToken).ConfigureAwait(false))
                    return StoodAside();
            }
        }
        finally
        {
            if (keepalive is not null)
            {
                await keepalive.CancelAsync().ConfigureAwait(false);
                try { await keeping.ConfigureAwait(false); }
                catch (Exception ex) { _logger?.LogDebug(ex, "Workflow {WorkflowId}: a Restart keepalive ended with an error.", _workflowId); }
            }

            if (live)
            {
                lock (_stateLock) _waitingHere.Remove(generation);
                if (!started)
                    await RetireRestartGenerationQuietlyAsync(generation).ConfigureAwait(false);
            }
        }
    }

    private enum RestartLockAnswer
    {
        Acquired,
        NewerStarted,
        NewerWaiting
    }

    private WorkflowRunDecision StoodAside()
    {
        RecordOutcome(WorkflowMode.Restart, WorkflowAdmissionOutcomes.Skipped);
        return new WorkflowRunDecision(
            WorkflowMode.Restart,
            WorkflowAdmissionOutcomes.Skipped,
            Lease: null,
            Reason: "restart_mode_superseded_by_a_newer_trigger");
    }

    private Task<long> StartIssue()
    {
        try
        {
            return _restartGenerations.IssueAsync(_workflowId, RestartWaiterLiveFor, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return Task.FromException<long>(ex);
        }
    }

    private async Task<long> WaitForIssueAsync(Task<long> issuing, CancellationToken cancellationToken)
    {
        using var waitFor = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            return await issuing.WaitAsync(waitFor.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waitFor.IsCancellationRequested)
        {
            _ = issuing.ContinueWith(
                landed =>
                {
                    if (landed.IsCompletedSuccessfully)
                        return RetireRestartGenerationQuietlyAsync(landed.Result);
                    var failure = landed.Exception;
                    _logger?.LogDebug(failure, "Workflow {WorkflowId}: a Restart generation nobody waited for was not issued.", _workflowId);
                    return Task.CompletedTask;
                },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            throw new ObjectDisposedException(nameof(WorkflowRunCoordinator));
        }
    }

    private async Task WaitForPermitWhileAliveAsync(CancellationToken cancellationToken, Action? onQueued = null)
    {
        using var waitFor = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            var turn = _semaphore.WaitAsync(waitFor.Token);
            HearQueued(onQueued);
            await turn.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _lifetime.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(WorkflowRunCoordinator));
        }
    }

    private async Task<bool> IsParkedOnASleepAsync(
        IWorkflowParkedSleepStore? parkedSleeps, CancellationToken cancellationToken)
    {
        if (parkedSleeps is null)
            return false;

        try
        {
            return await parkedSleeps.HasParkedSleepAsync(_workflowId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex,
                "Workflow {WorkflowId}: could not ask whether a run is parked on a sleep; admitting the run.",
                _workflowId);
            return false;
        }
    }

    private async Task<bool> WaitForRestartPermitAsync(long generation, CancellationToken cancellationToken)
    {
        var polls = 0;
        while (true)
        {
            ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
            if (await _semaphore.WaitAsync(RestartPollMost, cancellationToken).ConfigureAwait(false))
                return true;

            if (NewerStartedHere(generation, live: true))
                return false;
            if (++polls % RestartReadEveryPolls == 0
                && await ReadRestartGenerationsQuietlyAsync(cancellationToken).ConfigureAwait(false) is { } state
                && StartedPast(state.Started, generation, live: true))
                return false;
        }
    }

    private async Task<(RestartLockAnswer Answer, IWorkflowRunLock? Lock)> WaitForRestartLockAsync(
        long generation, bool ordered, bool live, long storeWaitingAbove, CancellationToken cancellationToken)
    {
        var pause = RestartPollFirst;
        var polls = 0;
        while (true)
        {
            ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
            var (contended, held) = await TryAcquireClusterRunBudgetAsync(cancellationToken).ConfigureAwait(false);
            if (!contended)
                return (RestartLockAnswer.Acquired, held);

            if (ordered && NewerStartedHere(generation, live))
                return (RestartLockAnswer.NewerStarted, null);
            if (++polls % RestartReadEveryPolls == 0
                && await ReadRestartGenerationsQuietlyAsync(cancellationToken).ConfigureAwait(false) is { } state)
            {
                if (ordered && StartedPast(state.Started, generation, live))
                    return (RestartLockAnswer.NewerStarted, null);
                if (state.NewestWaiting is { } newest && newest > storeWaitingAbove)
                    return (RestartLockAnswer.NewerWaiting, null);
            }

            await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
            pause = pause * 2 < RestartPollMost ? pause * 2 : RestartPollMost;
        }
    }

    private async Task<bool> GiveWayAsync(long generation, CancellationToken cancellationToken)
    {
        var pause = RestartPollFirst;
        while (true)
        {
            await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
            pause = pause * 2 < RestartPollMost ? pause * 2 : RestartPollMost;

            ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
            if (NewerStartedHere(generation, live: true))
                return true;
            if (await ReadRestartGenerationsQuietlyAsync(cancellationToken).ConfigureAwait(false) is not { } state)
                return false;
            if (StartedPast(state.Started, generation, live: true))
                return true;
            if (state.NewestWaiting is not { } newest || newest <= generation)
                return false;
        }
    }

    private bool NewerStartedHere(long generation, bool live)
    {
        lock (_stateLock) return StartedPast(_newestStartedHere, generation, live);
    }

    private static bool StartedPast(long started, long generation, bool live) =>
        live ? started >= generation : started > generation;

    private async Task KeepWaitingAsync(long generation, CancellationToken stop)
    {
        var sayings = 0;
        var touching = Task.CompletedTask;
        try
        {
            while (true)
            {
                try
                {
                    await Task.Delay(RestartSayAgainEvery, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (Forgotten)
                    return;

                CancelRunsOlderThan(generation);
                PublishRestartCancel(generation);
                if (++sayings % RestartTouchEverySayings == 0 && touching.IsCompleted)
                    touching = TouchQuietlyAsync(generation, stop);
            }
        }
        finally
        {
            await touching.ConfigureAwait(false);
        }
    }

    private async Task TouchQuietlyAsync(long generation, CancellationToken stop)
    {
        try
        {
            await _restartGenerations.TouchAsync(_workflowId, generation, RestartWaiterLiveFor, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Workflow {WorkflowId}: Restart generation {Generation} could not be kept live; the next touch tries again.",
                _workflowId, generation);
        }
    }

    private async Task<WorkflowRestartGenerationState?> ReadRestartGenerationsQuietlyAsync(CancellationToken cancellationToken)
    {
        using var readFor = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            return await _restartGenerations.ReadAsync(_workflowId, readFor.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (readFor.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ObjectDisposedException(nameof(WorkflowRunCoordinator));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Workflow {WorkflowId}: the Restart generations could not be read.", _workflowId);
            return null;
        }
    }

    private async Task MarkRestartStartedQuietlyAsync(long generation)
    {
        try
        {
            await _restartGenerations.StartAsync(_workflowId, generation, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Workflow {WorkflowId}: Restart generation {Generation} started but could not be marked so.",
                _workflowId, generation);
        }
    }

    private async Task RetireRestartGenerationQuietlyAsync(long generation)
    {
        try
        {
            await _restartGenerations.RetireAsync(_workflowId, generation, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Workflow {WorkflowId}: Restart generation {Generation} could not be retired; it lapses on its own.",
                _workflowId, generation);
        }
    }

    private void WatchRestartRun(
        Guid runId, long generation, CancellationTokenSource runCts, long? startedMark, long? waitingAbove = null)
    {
        var newerThan = waitingAbove ?? generation;
        if (!_isMultiReplica)
            return;

        _ = WatchAsync();

        async Task WatchAsync()
        {
            while (true)
            {
                try
                {
                    await Task.Delay(_restartWatchInterval, _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                lock (_stateLock)
                {
                    if (!_activeRuns.TryGetValue(runId, out var current) || current != runCts)
                        return;
                }

                WorkflowRestartGenerationState state;
                try
                {
                    state = await _restartGenerations.ReadAsync(_workflowId, _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Workflow {WorkflowId}: the Restart watch could not read the generations.", _workflowId);
                    continue;
                }

                startedMark ??= state.Started;
                if (state.Started <= startedMark && (state.NewestWaiting is not { } newest || newest <= newerThan))
                    continue;

                lock (_stateLock)
                {
                    if (!_activeRuns.ContainsKey(runId))
                        return;
                }

                try
                {
                    await runCts.CancelAsync().ConfigureAwait(false);
                    _logger?.LogInformation(
                        "Workflow {WorkflowId}: stopped a Restart run of generation {Generation}; a newer trigger is waiting or started.",
                        _workflowId, generation);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "Workflow {WorkflowId}: stopped a Restart run of generation {Generation}; one of its cancellation callbacks threw.",
                        _workflowId, generation);
                }

                return;
            }
        }
    }

    private async Task<WorkflowRunDecision> AcquireRestartWithoutGenerationAsync(
        long issuedAfterFailure, IWorkflowParkedSleepStore? parkedSleeps, CancellationToken cancellationToken)
    {
        bool hadExisting;
        lock (_stateLock) hadExisting = _activeRuns.Count > 0;
        hadExisting = hadExisting || await IsParkedOnASleepAsync(parkedSleeps, cancellationToken).ConfigureAwait(false);
        CancelRunsOlderThan(generation: null);
        if (_clusterSignal is not null)
            PublishQuietly(_restartChannel, payload: null);
        if (hadExisting)
            RecordOutcome(WorkflowMode.Restart, WorkflowAdmissionOutcomes.Restarted);

        await WaitForPermitWhileAliveAsync(cancellationToken).ConfigureAwait(false);

        (bool Contended, IWorkflowRunLock? Lock) clusterLock;
        try
        {
            clusterLock = await TryAcquireClusterRunBudgetWithRetryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReturnPermit();
            throw;
        }

        if (clusterLock.Contended)
        {
            ReturnPermit();
            RecordOutcome(WorkflowMode.Restart, WorkflowAdmissionOutcomes.Skipped);
            return new WorkflowRunDecision(
                WorkflowMode.Restart,
                WorkflowAdmissionOutcomes.Skipped,
                Lease: null,
                Reason: "restart_mode_already_running_on_another_replica");
        }

        var lease = CreateLeaseLocked(cancellationToken, WorkflowMode.Restart, clusterLock.Lock, restartGeneration: 0);
        WatchRestartRun(lease.RunId, 0, lease.RunCancellationSource, startedMark: null, waitingAbove: issuedAfterFailure);
        return await RecordStartedAsync(new WorkflowRunDecision(
            WorkflowMode.Restart,
            hadExisting ? WorkflowAdmissionOutcomes.Restarted : WorkflowAdmissionOutcomes.Started,
            lease,
            Reason: null)).ConfigureAwait(false);
    }

    private async Task<long> ReadIssuedAfterFailedIssueAsync(CancellationToken cancellationToken) =>
        (await ReadRestartGenerationsQuietlyAsync(cancellationToken).ConfigureAwait(false))?.Issued ?? 0;

    private async Task DisposeLockQuietlyAsync(IWorkflowRunLock clusterLock)
    {
        try
        {
            await clusterLock.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Workflow {WorkflowId}: the cluster run-budget lock could not be given back; it lapses on its own.",
                _workflowId);
        }
    }

    private async Task<(bool Contended, IWorkflowRunLock? Lock)> TryAcquireClusterRunBudgetAsync(
        CancellationToken cancellationToken)
    {
        if (_lockProvider == null)
            return (false, null);

        try
        {
            var acquired = await _lockProvider.TryAcquireAsync(ClusterRunBudgetLockKey, cancellationToken)
                .ConfigureAwait(false);
            return (acquired == null, acquired);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Workflow {WorkflowId}: cluster run-budget lock acquisition failed; proceeding with the " +
                "in-process permit only (degrade-open).",
                _workflowId);
            return (false, null);
        }
    }

    private async Task<(bool Contended, IWorkflowRunLock? Lock)> TryAcquireClusterRunBudgetWithRetryAsync(
        CancellationToken cancellationToken)
    {
        var result = await TryAcquireClusterRunBudgetAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Contended || _lockProvider == null)
            return result;

        var deadline = DateTimeOffset.UtcNow + ClusterRunBudgetRetryBudget;
        while (result.Contended && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(ClusterRunBudgetRetryInterval, cancellationToken).ConfigureAwait(false);
            result = await TryAcquireClusterRunBudgetAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private void PublishRestartCancel(long generation)
    {
        if (_clusterSignal is null)
            return;

        PublishQuietly(_restartChannel, generation.ToString(CultureInfo.InvariantCulture));
    }

    private void PublishQuietly(string channel, string? payload)
    {
        try
        {
            _ = _clusterSignal!.PublishAsync(channel, _workflowId, payload);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Workflow {WorkflowId}: a Restart signal on {Channel} could not be published.",
                _workflowId, channel);
        }
    }

    private int CancelRunsOlderThan(long? generation)
    {
        List<CancellationTokenSource> toCancel;
        lock (_stateLock)
        {
            toCancel = _activeRuns
                .Where(run => generation is not { } newer
                    || !_runGenerations.TryGetValue(run.Key, out var runGeneration)
                    || runGeneration < newer)
                .Select(run => run.Value)
                .ToList();
        }

        var cancelled = 0;
        foreach (var cts in toCancel)
        {
            try
            {
                if (cts.IsCancellationRequested)
                    continue;
                cts.Cancel();
                cancelled++;
            }
            catch (ObjectDisposedException) { }
            catch (AggregateException ex)
            {
                cancelled++;
                _logger?.LogWarning(ex, "Workflow {WorkflowId}: a run's cancellation callback threw.", _workflowId);
            }
        }

        return cancelled;
    }

    /// <summary>Cancels this replica's local in-flight runs so a superseding Restart elsewhere can take over.
    /// Suppressing this replica's own broadcast is the signal's job: cancelling twice tears down the run that won.</summary>
    public int ApplyRestartCancel(Guid workflowId) => ApplyRestartCancel(workflowId, payload: null);

    /// <summary>Applies a peer's Restart cancel that names its trigger's generation: the runs here older than it are
    /// cancelled. A payload that names no generation cancels every run.</summary>
    public int ApplyRestartCancel(Guid workflowId, string? payload)
    {
        if (workflowId != _workflowId)
            return 0;

        var cancelled = CancelRunsOlderThan(
            long.TryParse(payload, NumberStyles.None, CultureInfo.InvariantCulture, out var generation) ? generation : null);

        if (cancelled > 0)
        {
            _logger?.LogInformation(
                "Workflow {WorkflowId}: cancelled {Count} local run(s) on a peer's Restart-supersession signal.",
                _workflowId, cancelled);
        }

        return cancelled;
    }

    private async Task<(bool Admitted, string? SlotToken)> TryAcquireClusterSlotAsync(
        int maxConcurrentRuns,
        CancellationToken cancellationToken)
    {
        if (!_isMultiReplica || _slotGate == null)
            return (true, null);

        var slotToken = Guid.NewGuid().ToString("N");
        var admitted = await _slotGate.TryAcquireAsync(_workflowId, slotToken, maxConcurrentRuns, cancellationToken)
            .ConfigureAwait(false);
        return admitted ? (true, slotToken) : (false, null);
    }

    internal Task<IWorkflowRunLock?> ReacquireClusterRunBudgetAsync(CancellationToken cancellationToken) =>
        _lockProvider is null
            ? Task.FromResult<IWorkflowRunLock?>(null)
            : _lockProvider.TryAcquireAsync(ClusterRunBudgetLockKey, cancellationToken);

    internal async Task<bool> RenewClusterSlotAsync(string slotToken, CancellationToken cancellationToken)
    {
        if (_slotGate is null)
            return true;
        if (await _slotGate.RenewAsync(_workflowId, slotToken, cancellationToken).ConfigureAwait(false))
            return true;

        int budget;
        lock (_stateLock) budget = _maxConcurrentRuns;
        return await _slotGate.TryAcquireAsync(_workflowId, slotToken, budget, cancellationToken).ConfigureAwait(false);
    }

    internal void ReleaseClusterSlot(string? slotToken)
    {
        if (slotToken == null || _slotGate == null)
            return;

        _ = _slotGate.ReleaseAsync(_workflowId, slotToken);
    }


    private async Task<WorkflowRunDecision> AcquireQueuedAsync(
        WorkflowOverflowBehavior overflow,
        Action? onQueued,
        CancellationToken cancellationToken)
    {
        if (_semaphore.Wait(0, cancellationToken))
        {
            string? slotToken;
            try
            {
                var (admitted, token) = await TryAcquireClusterSlotAsync(_maxConcurrentRuns, cancellationToken)
                    .ConfigureAwait(false);
                if (!admitted)
                {
                    ReturnPermit();
                    return ApplyOverflow(overflow, WorkflowMode.Queued, "concurrency_limit_reached_cluster_wide");
                }
                slotToken = token;
            }
            catch
            {
                ReturnPermit();
                throw;
            }

            var lease = CreateLeaseLocked(cancellationToken, WorkflowMode.Queued, slotToken: slotToken);
            return await RecordStartedAsync(new WorkflowRunDecision(
                WorkflowMode.Queued,
                WorkflowAdmissionOutcomes.Started,
                lease,
                Reason: null)).ConfigureAwait(false);
        }

        var restartIssuedAtQueue = (await ReadRestartGenerationsQuietlyAsync(cancellationToken).ConfigureAwait(false))?.Issued;

        string? refused = null;
        lock (_stateLock)
        {
            if (_maxQueuedRuns == 0)
                refused = "queue_disabled";
            else if (_queuedWaiters >= _maxQueuedRuns)
                refused = "queue_full";
            else
                _queuedWaiters++;
        }

        if (refused is not null)
            return ApplyOverflow(overflow, WorkflowMode.Queued, refused);

        try
        {
            RecordOutcome(WorkflowMode.Queued, WorkflowAdmissionOutcomes.Queued);
            await WaitForPermitWhileAliveAsync(cancellationToken, onQueued).ConfigureAwait(false);
        }
        finally
        {
            lock (_stateLock) _queuedWaiters--;
        }

        WorkflowMode promotedMode;
        lock (_stateLock) promotedMode = _mode;
        if (promotedMode is WorkflowMode.Restart)
            return await PromoteUnderRestartAsync(restartIssuedAtQueue, cancellationToken).ConfigureAwait(false);

        if (promotedMode is WorkflowMode.Single)
        {
            (bool Contended, IWorkflowRunLock? Lock) clusterLock;
            try
            {
                clusterLock = await TryAcquireClusterRunBudgetAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ReturnPermit();
                throw;
            }

            if (clusterLock.Contended)
            {
                ReturnPermit();
                RecordOutcome(promotedMode, WorkflowAdmissionOutcomes.Skipped);
                return new WorkflowRunDecision(
                    promotedMode,
                    WorkflowAdmissionOutcomes.Skipped,
                    Lease: null,
                    Reason: "single_mode_already_running_on_another_replica");
            }

            return new WorkflowRunDecision(
                WorkflowMode.Queued,
                WorkflowAdmissionOutcomes.Queued,
                CreateLeaseLocked(cancellationToken, promotedMode, clusterLock.Lock),
                Reason: null);
        }

        string? promotedSlotToken;
        try
        {
            var (admitted, token) = await TryAcquireClusterSlotAsync(_maxConcurrentRuns, cancellationToken)
                .ConfigureAwait(false);
            if (!admitted)
            {
                ReturnPermit();
                return ApplyOverflow(overflow, WorkflowMode.Queued, "concurrency_limit_reached_cluster_wide");
            }
            promotedSlotToken = token;
        }
        catch
        {
            ReturnPermit();
            throw;
        }

        var lease2 = CreateLeaseLocked(cancellationToken, WorkflowMode.Queued, slotToken: promotedSlotToken);
        return new WorkflowRunDecision(
            WorkflowMode.Queued,
            WorkflowAdmissionOutcomes.Queued,
            lease2,
            Reason: null);
    }


    private async Task<WorkflowRunDecision> PromoteUnderRestartAsync(long? issuedAtQueue, CancellationToken cancellationToken)
    {
        if (issuedAtQueue is { } generation)
        {
            return await WaitAndStartRestartAsync(
                generation, ordered: true, live: false, promoted: true, hasPermit: true, hadExisting: false, cancellationToken)
                .ConfigureAwait(false);
        }

        long issued;
        var issuing = StartIssue();
        try
        {
            issued = await WaitForIssueAsync(issuing, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested || Forgotten)
        {
            ReturnPermit();
            cancellationToken.ThrowIfCancellationRequested();
            throw new ObjectDisposedException(nameof(WorkflowRunCoordinator));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Workflow {WorkflowId}: a promoted waiter could not be given a Restart generation.", _workflowId);
            long issuedAfterFailure;
            try
            {
                issuedAfterFailure = await ReadIssuedAfterFailedIssueAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ReturnPermit();
                throw;
            }

            return await WaitAndStartRestartAsync(
                0, ordered: false, live: false, promoted: true, hasPermit: true, hadExisting: false, cancellationToken,
                waitingAbove: issuedAfterFailure).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested || Forgotten)
        {
            ReturnPermit();
            await RetireRestartGenerationQuietlyAsync(issued).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new ObjectDisposedException(nameof(WorkflowRunCoordinator));
        }

        lock (_stateLock) _waitingHere.Add(issued);
        return await WaitAndStartRestartAsync(
            issued, ordered: true, live: true, promoted: true, hasPermit: true, hadExisting: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<WorkflowRunDecision> AcquireParallelAsync(
        WorkflowOverflowBehavior overflow,
        CancellationToken cancellationToken)
    {
        if (_semaphore.Wait(0, cancellationToken))
        {
            string? slotToken;
            try
            {
                var (admitted, token) = await TryAcquireClusterSlotAsync(_maxConcurrentRuns, cancellationToken)
                    .ConfigureAwait(false);
                if (!admitted)
                {
                    ReturnPermit();
                    return ApplyOverflow(overflow, WorkflowMode.Parallel, "concurrency_limit_reached_cluster_wide");
                }
                slotToken = token;
            }
            catch
            {
                ReturnPermit();
                throw;
            }

            var lease = CreateLeaseLocked(cancellationToken, WorkflowMode.Parallel, slotToken: slotToken);
            return await RecordStartedAsync(new WorkflowRunDecision(
                WorkflowMode.Parallel,
                WorkflowAdmissionOutcomes.Started,
                lease,
                Reason: null)).ConfigureAwait(false);
        }

        return ApplyOverflow(overflow, WorkflowMode.Parallel, "concurrency_limit_reached");
    }

    private void HearQueued(Action? onQueued)
    {
        try
        {
            onQueued?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Workflow {WorkflowId}: the queued-run callback threw; the run stays queued.", _workflowId);
        }
    }

    private WorkflowRunDecision ApplyOverflow(WorkflowOverflowBehavior overflow, WorkflowMode mode, string reason)
    {
        switch (overflow)
        {
            case WorkflowOverflowBehavior.Drop:
                RecordOutcome(mode, WorkflowAdmissionOutcomes.Dropped);
                return new WorkflowRunDecision(mode,
                    WorkflowAdmissionOutcomes.Dropped, Lease: null, Reason: reason);

            case WorkflowOverflowBehavior.Log:
                RecordOutcome(mode, WorkflowAdmissionOutcomes.Dropped);
                _logger?.LogWarning(
                    "Workflow {WorkflowId} dropped a trigger in {Mode} mode (overflow=Log, reason={Reason}).",
                    _workflowId, mode, reason);
                return new WorkflowRunDecision(mode,
                    WorkflowAdmissionOutcomes.Dropped, Lease: null, Reason: reason);

            case WorkflowOverflowBehavior.Error:
                RecordOutcome(mode, WorkflowAdmissionOutcomes.Errored);
                throw new WorkflowConcurrencyException(_workflowId, mode.ToString(),
                    WorkflowAdmissionOutcomes.Errored,
                    $"Workflow '{_workflowId}' rejected trigger in {mode} mode: {reason}. " +
                    "Increase MaxConcurrentRuns/MaxQueuedRuns or change OverflowBehavior to Drop or Log.");

            default:
                throw new InvalidOperationException($"Unknown overflow behavior {overflow}.");
        }
    }


    private WorkflowRunLease CreateLeaseLocked(
        CancellationToken externalToken,
        WorkflowMode mode,
        IWorkflowRunLock? clusterLock = null,
        string? slotToken = null,
        long? restartGeneration = null)
    {
        var runId = Guid.NewGuid();
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

        bool disposed;
        lock (_stateLock)
        {
            disposed = _disposed || _retired;
            if (!disposed)
            {
                _activeRuns[runId] = runCts;
                if (restartGeneration is { } generation)
                    _runGenerations[runId] = generation;
            }
        }

        var lease = new WorkflowRunLease(this, runId, runCts, mode, clusterLock, slotToken);
        if (!disposed)
            return lease;

        lease.Dispose();
        throw new ObjectDisposedException(nameof(WorkflowRunCoordinator));
    }

    internal void ReleaseLease(Guid runId, CancellationTokenSource runCts)
    {
        lock (_stateLock)
        {
            _activeRuns.Remove(runId);
            _runGenerations.Remove(runId);
        }

        ReturnPermit();

        try { runCts.Dispose(); } catch { }
    }

    private void ReturnPermit()
    {
        lock (_stateLock)
        {
            if (_permitDebt > 0)
            {
                _permitDebt--;
                return;
            }
        }

        try
        {
            _semaphore.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RecordOutcome(WorkflowMode mode, string outcome)
        => _metrics.RecordSupersession(_workflowId, mode.ToString(), outcome);

    private async Task<WorkflowRunDecision> RecordStartedAsync(WorkflowRunDecision decision)
    {
        try
        {
            RecordOutcome(decision.Mode, WorkflowAdmissionOutcomes.Started);
        }
        catch
        {
            await decision.Lease!.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return decision;
    }


    /// <summary>Replaces the mode/limits in place, so editing a workflow's concurrency settings does not evict the
    /// coordinator and lose in-flight runs' restart CTSes. In-flight runs and queued waiters keep their place.</summary>
    public void Reconfigure(
        WorkflowMode mode,
        int maxConcurrentRuns,
        int maxQueuedRuns,
        WorkflowOverflowBehavior overflowBehavior)
    {
        if (maxConcurrentRuns < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentRuns));
        if (maxQueuedRuns < 0)
            throw new ArgumentOutOfRangeException(nameof(maxQueuedRuns));

        lock (_stateLock)
        {
            var oldPermits = _mode is WorkflowMode.Single or WorkflowMode.Restart ? 1 : _maxConcurrentRuns;
            var newPermits = mode is WorkflowMode.Single or WorkflowMode.Restart ? 1 : maxConcurrentRuns;

            var delta = newPermits - oldPermits;
            try
            {
                if (delta > 0)
                {
                    var repaid = Math.Min(_permitDebt, delta);
                    _permitDebt -= repaid;
                    if (delta - repaid > 0)
                        _semaphore.Release(delta - repaid);
                }
                else if (delta < 0)
                {
                    var shrink = -delta;
                    while (shrink > 0 && _semaphore.Wait(0))
                        shrink--;
                    _permitDebt += shrink;
                }
            }
            catch (ObjectDisposedException)
            {
            }

            _mode = mode;
            _maxConcurrentRuns = maxConcurrentRuns;
            _maxQueuedRuns = maxQueuedRuns;
            _overflowBehavior = overflowBehavior;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        List<CancellationTokenSource> toCancel;
        lock (_stateLock)
        {
            toCancel = _activeRuns.Values.ToList();
            _activeRuns.Clear();
            _runGenerations.Clear();
        }

        foreach (var cts in toCancel)
        {
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
        }

        try { _restartCancelSubscription?.Dispose(); } catch { }
        try { _lifetime.Cancel(); } catch { }
    }
}

/// <summary>One waiting run in a Queued workflow's channel. Public because the queue's element type is part
/// of what this coordinator is, not a detail a host should have to guess at.</summary>

/// <summary>Decision returned by <see cref="WorkflowRunCoordinator.AcquireAsync"/>. A non-null <see cref="Lease"/>
/// must be disposed after the run; a null one means the caller MUST NOT execute.</summary>
public sealed record WorkflowRunDecision(
    WorkflowMode Mode,
    string Outcome,
    WorkflowRunLease? Lease,
    string? Reason);

/// <summary>Per-run lease carrying the linked token the engine passes into block execution. Disposal releases the
/// cluster lock FIRST (awaited), THEN the local permit, so a re-acquire never sees an unlock still in flight.</summary>
public sealed class WorkflowRunLease : IDisposable, IAsyncDisposable
{
    private readonly WorkflowRunCoordinator _coordinator;
    private readonly CancellationTokenSource _runCts;
    private IWorkflowRunLock? _clusterLock;
    private readonly string? _slotToken;
    private readonly SemaphoreSlim _renewal = new(1, 1);
    private int _disposed;

    public Guid RunId { get; }
    public WorkflowMode Mode { get; }
    public CancellationToken Token => _runCts.Token;

    /// <summary>Exposes the per-run CTS so the engine can join it with its own timeout CTS. The coordinator owns
    /// the lifetime; engine code must not dispose it.</summary>
    public CancellationTokenSource RunCancellationSource => _runCts;

    internal WorkflowRunLease(
        WorkflowRunCoordinator coordinator,
        Guid runId,
        CancellationTokenSource runCts,
        WorkflowMode mode,
        IWorkflowRunLock? clusterLock = null,
        string? slotToken = null)
    {
        _coordinator = coordinator;
        _runCts = runCts;
        RunId = runId;
        Mode = mode;
        _clusterLock = clusterLock;
        _slotToken = slotToken;
    }

    /// <summary>Whether this lease holds anything cluster-wide that can lapse under a long run.</summary>
    public bool HoldsClusterBudget => _clusterLock is not null || _slotToken is not null;

    /// <summary>Re-asserts this run's hold on its workflow's cluster budget: a lapsed lock is taken again, and a held
    /// slot is renewed or taken again. False when what lapsed is held elsewhere now, and the run is over its budget.</summary>
    /// <remarks>What the engine's heartbeat calls on every beat. A lease already released answers true: there is
    /// nothing left to lose. A provider or gate that throws lets the exception out, and the run keeps what it had.</remarks>
    public async Task<bool> RenewAsync(CancellationToken cancellationToken)
    {
        await _renewal.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return true;

            if (_clusterLock is { } held && !await held.IsValidAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await _coordinator.ReacquireClusterRunBudgetAsync(cancellationToken).ConfigureAwait(false) is not { } again)
                    return false;
                _clusterLock = again;
                try { await held.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }

            return _slotToken is null
                || await _coordinator.RenewClusterSlotAsync(_slotToken, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _renewal.Release();
        }
    }

    /// <summary>The preferred disposal path: releases the cluster-wide run-budget lock deterministically (awaited)
    /// BEFORE the local permit, so a same-replica re-acquire can never see a still-held advisory lock.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _renewal.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_clusterLock != null)
            {
                try { await _clusterLock.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }
        }
        finally
        {
            _renewal.Release();
        }

        _coordinator.ReleaseClusterSlot(_slotToken);

        _coordinator.ReleaseLease(RunId, _runCts);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _renewal.Wait();
        try
        {
            if (_clusterLock != null)
            {
                try { _clusterLock.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { }
            }
        }
        finally
        {
            _renewal.Release();
        }

        _coordinator.ReleaseClusterSlot(_slotToken);

        _coordinator.ReleaseLease(RunId, _runCts);
    }
}
