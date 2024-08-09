using System.Data;
using System.Data.Common;

namespace Medallion.Threading.Internal.Data;

/// <summary>
/// Implements keepalive for a <see cref="DatabaseConnection"/> which is important for certain providers
/// such as SQL Azure.
/// 
/// Also supports more active monitoring for the purposes of implementing <see cref="IDistributedSynchronizationHandle.HandleLostToken"/>
/// </summary>
internal sealed class ConnectionMonitor : IAsyncDisposable
{
    /// <summary>
    /// Weak reference to the underlying <see cref="DatabaseConnection"/>. We use a weak reference to
    /// avoid the case where our background worker <see cref="_monitoringWorkerTask"/> keeps the connection from being GC'd
    /// and therefore keeps an abandoned handle from being released
    /// </summary>
    private readonly WeakReference<DatabaseConnection> _weakConnection;
    /// <summary>
    /// Caches a handler for <see cref="OnConnectionStateChanged(object, StateChangeEventArgs)"/> so we
    /// unregister it in <see cref="DisposeAsync"/>
    /// </summary>
    private readonly StateChangeEventHandler? _stateChangedHandler;

    /// <summary>
    /// Allows us to avoid running multiple concurrent queries on <see cref="_weakConnection"/>
    /// </summary>
    private readonly AsyncLock _connectionLock = AsyncLock.Create();

    /// <summary>
    /// Tracks whether the connection is externally-owned. For externally owned connections we cannot
    /// run any background queries because this might violate threadsafety with whatever the connection
    /// owner is doing
    /// </summary>
    private readonly bool _isExternallyOwnedConnection;

    private TimeoutValue _keepaliveCadence = Timeout.InfiniteTimeSpan;
    private State _state;
    private Dictionary<MonitoringHandle, CancellationTokenSource>? _monitoringHandleRegistrations;
    private CancellationTokenSource? _monitorStateChangedTokenSource;
    private Task _monitoringWorkerTask = Task.CompletedTask;

    public ConnectionMonitor(DatabaseConnection connection)
    {
        this._weakConnection = new WeakReference<DatabaseConnection>(connection);
        this._isExternallyOwnedConnection = connection.IsExernallyOwned;
        // stopped not autostopped here so that the statechange handler will not cause a start
        this._state = connection.CanExecuteQueries ? State.Idle : State.Stopped;
        Invariant.Require(this._state == State.Stopped || this._isExternallyOwnedConnection);

        if (connection.InnerConnection is DbConnection dbConnection)
        {
            dbConnection.StateChange += this._stateChangedHandler = this.OnConnectionStateChanged;
        }
    }

    /// <summary>
    /// Protects access to all mutable state
    /// </summary>
    private object Lock => this._weakConnection;

    private bool HasRegisteredMonitoringHandlesNoLock => (this._monitoringHandleRegistrations?.Count).GetValueOrDefault() != 0;

    public async ValueTask<IDisposable> AcquireConnectionLockAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            ValueTask<IDisposable?> connectionLockTask;
            lock (this.Lock)
            {
                // If we're monitoring, then the connection will almost constantly be in use. 
                // Fire state changed to cancel that query and clear it up
                if (this._state == State.Active && this.HasRegisteredMonitoringHandlesNoLock)
                {
                    this.FireStateChangedNoLock();
                }

                // By starting the acquisition inside Lock, we should be guaranteed to get in before the worker look can
                // take the lock again. This relies on AsyncLock.AcquireAsync being FIFO, which is currently true because of
                // how SemaphoreSlim works. However, to be extra robust we do a try-wait here and retry on failure
                connectionLockTask = this._connectionLock.TryAcquireAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }

            var handle = await connectionLockTask.ConfigureAwait(false);
            if (handle != null) { return handle; }
        }
    }

    public void SetKeepaliveCadence(TimeoutValue keepaliveCadence)
    {
        Invariant.Require(!this._isExternallyOwnedConnection);

        lock (this.Lock)
        {
            Invariant.Require(this._state != State.Disposed);

            var originalKeepaliveCadence = this._keepaliveCadence;
            this._keepaliveCadence = keepaliveCadence;

            if (!this.StartMonitorWorkerIfNeededNoLock()
                && this._state == State.Active
                && !this.HasRegisteredMonitoringHandlesNoLock
                && keepaliveCadence.CompareTo(originalKeepaliveCadence) < 0)
            {
                // If we get here, then we already have an active worker performing
                // keepalive on a longer cadence. Since that worker is likely asleep,
                // we fire state changed to wake it up
                this.FireStateChangedNoLock();
            }
        }
    }

    public IDatabaseConnectionMonitoringHandle GetMonitoringHandle()
    {
        lock (this.Lock)
        {
            // Since this will be called via non-thread-safe paths, we do a true
            // dispose check. This error should never reach callers unless they are
            // using non-thread-safe stuff concurrently
            if (this._state == State.Disposed) { throw new ObjectDisposedException(this.GetType().ToString()); }

            // If the connection is already closed, we'll never see a state change
            // event for the close so just return an already canceled handle
            if (this._state == State.AutoStopped || this._state == State.Stopped)
            {
                return new AlreadyCanceledHandle();
            }

            // If the connection does not support state monitoring, we can't produce
            // a monitoring handle
            if (this._stateChangedHandler == null)
            {
                return NullHandle.Instance;
            }

            var hadRegisteredMonitoringHandles = this.HasRegisteredMonitoringHandlesNoLock;

            var connectionLostTokenSource = new CancellationTokenSource();
            var handle = new MonitoringHandle(this, connectionLostTokenSource.Token);
            (this._monitoringHandleRegistrations ??= [])
                .Add(handle, connectionLostTokenSource);

            if (!this.StartMonitorWorkerIfNeededNoLock() 
                && !hadRegisteredMonitoringHandles
                && this._state == State.Active)
            {
                // If we get here, it means we already had an active worker which was not monitoring (doing
                // keepalive). That worker is likely asleep, so we fire state changed to wake it up and have it
                // switch over to monitoring
                this.FireStateChangedNoLock();
            }

            return handle;
        }
    }

    private void ReleaseMonitoringHandle(MonitoringHandle handle)
    {
        lock (this.Lock)
        {
            if (this._monitoringHandleRegistrations!.TryGetValue(handle, out var cancellationTokenSource))
            {
                this._monitoringHandleRegistrations.Remove(handle);
                cancellationTokenSource.Dispose();

                // If we've removed the last reason to be monitoring, fire state changed to stop the monitoring process.
                // Without this, the next query that attempts to acquire the connection lock will not think we are monitoring
                // and therefore will not fire state change. Then, it will get stuck waiting for the monitoring query to complete
                if (this._monitoringHandleRegistrations.Count == 0 && this._state == State.Active)
                {
                    this.FireStateChangedNoLock();
                }
            }
        }
    }

    private void OnConnectionStateChanged(object sender, StateChangeEventArgs args)
    {
        if (args.OriginalState == ConnectionState.Open && args.CurrentState != ConnectionState.Open)
        {
            lock (this.Lock)
            {
                if (this._state == State.Idle || this._state == State.Active)
                {
                    this._state = State.AutoStopped;
                    this.CloseOrCancelMonitoringHandleRegistrationsNoLock(isCancel: true);
                }

                Invariant.Require(!this.HasRegisteredMonitoringHandlesNoLock);
            }
        }
        else if (args.OriginalState != ConnectionState.Open && args.CurrentState == ConnectionState.Open)
        {
            lock (this.Lock)
            {
                if (this._state == State.AutoStopped)
                {
                    this.StartNoLock();
                }
            }
        }
    }

    public void Start()
    {
        Invariant.Require(!this._isExternallyOwnedConnection);

        lock (this.Lock)
        {
            Invariant.Require(this._state == State.Stopped);
            this.StartNoLock();
        }
    }

    private void StartNoLock()
    {
        this._state = State.Idle;
        this.StartMonitorWorkerIfNeededNoLock();
    }

    public ValueTask StopAsync() => this.StopOrDisposeAsync(isDispose: false);
    public ValueTask DisposeAsync() => this.StopOrDisposeAsync(isDispose: true);

    private async ValueTask StopOrDisposeAsync(bool isDispose)
    {
        Task? task;
        lock (this.Lock)
        {
            if (isDispose)
            {
                this._state = State.Disposed;
            }
            else
            {
                Invariant.Require(!this._isExternallyOwnedConnection);
                Invariant.Require(this._state != State.Disposed);
                this._state = State.Stopped;
            }

            // If we have any registered monitoring handles, clear them out.
            // We don't cancel them since if the helper was stopped that indicates
            // proper disposal rather than loss of the connection
            this.CloseOrCancelMonitoringHandleRegistrationsNoLock(isCancel: false);

            task = this._monitoringWorkerTask;

            // Note: synchronous cancel here should be safe because we've already set
            // the state to disposed above which the monitoring loop will check if it
            // takes over the Cancel() thread.
            this._monitorStateChangedTokenSource?.Cancel();

            // If disposing, unsubscribe from state change tracking.
            if (isDispose
                && this._stateChangedHandler != null
                && this._weakConnection.TryGetTarget(out var connection))
            {
                ((DbConnection)connection.InnerConnection).StateChange -= this._stateChangedHandler;
            }
        }

        if (task != null)
        {
            await task.AwaitSyncOverAsync().ConfigureAwait(false);
        }
    }

    private void CloseOrCancelMonitoringHandleRegistrationsNoLock(bool isCancel)
    {
        Invariant.Require(this._state == State.AutoStopped || this._state == State.Stopped || this._state == State.Disposed);

        if (this._monitoringHandleRegistrations == null) { return; }

        foreach (var kvp in this._monitoringHandleRegistrations)
        {
            var cancellationTokenSource = kvp.Value;
            if (isCancel)
            {
                // cancel in a background thread in case we have hangs or errors
                Task.Run(() =>
                {
                    try { cancellationTokenSource.Cancel(); }
                    finally { cancellationTokenSource.Dispose(); }
                });
            }
            else
            {
                cancellationTokenSource.Dispose();
            }
        }
        this._monitoringHandleRegistrations.Clear();
    }

    private bool StartMonitorWorkerIfNeededNoLock()
    {
        Invariant.Require(this._state != State.Disposed);

        // never monitor external connections
        if (this._isExternallyOwnedConnection) { return false; }

        // If we're in the active state, we already have a worker. If we're not in the idle
        // state, we're not supposed to be running
        if (this._state != State.Idle) { return false; }

        // skip if there's nothing to do
        if (this._keepaliveCadence.IsInfinite && !this.HasRegisteredMonitoringHandlesNoLock) { return false; }
        
        this._monitorStateChangedTokenSource = new CancellationTokenSource();
        // Set up the task as a continuation on the previous task to avoid concurrency in the case where the previous
        // one is spinning down. If we change states in rapid succession we could end up with multiple tasks queued up
        // but this shouldn't matter since when the active one ultimately stops all the others will follow in rapid succession
        this._monitoringWorkerTask = this._monitoringWorkerTask
            .ContinueWith((_, state) => ((ConnectionMonitor)state!).MonitorWorkerLoop(), state: this)
            .Unwrap();
        this._state = State.Active;
        return true;
    }

    private void FireStateChangedNoLock()
    {
        var monitorStateChangedTokenSource = this._monitorStateChangedTokenSource!;
        this._monitorStateChangedTokenSource = new CancellationTokenSource();
        // Canceling asynchronously is important because the Cancel() thread can end up
        // running continuations inside the monitoring loop (e. g. see
        // https://github.com/madelson/DistributedLock/issues/85). Now that we set the new
        // token source before canceling the old one we should avoid that particular issue, but
        // it is still safer and easier to reason about not to have that happen. This also ensures
        // that FireStateChangedNoLock() always returns quickly, even if the monitoring loop
        // were to do some synchronous work on the continuation thread.
        Task.Run(() =>
        {
            try { monitorStateChangedTokenSource.Cancel(); }
            finally { monitorStateChangedTokenSource.Dispose(); }
        });
    }

    private async Task MonitorWorkerLoop()
    {
        while (await this.TryKeepaliveOrMonitorAsync().ConfigureAwait(false))
        {
            // just keep going
        }
    }

    private async Task<bool> TryKeepaliveOrMonitorAsync()
    {
        // get state
        TimeoutValue keepaliveCadence;
        bool isMonitoring;
        CancellationToken stateChangedToken;
        lock (this.Lock)
        {
            if (this._state != State.Active) { return false; }

            keepaliveCadence = this._keepaliveCadence;
            isMonitoring = this.HasRegisteredMonitoringHandlesNoLock;
            stateChangedToken = this._monitorStateChangedTokenSource!.Token;
        }

        return await (isMonitoring ? this.DoMonitoringAsync(stateChangedToken) : this.DoKeepaliveAsync(keepaliveCadence, stateChangedToken)).ConfigureAwait(false);
    }

    private async Task<bool> DoMonitoringAsync(CancellationToken cancellationToken)
    {
        if (!this._weakConnection.TryGetTarget(out var connection)) { return false; }

        // don't pass token here: this should finish quickly and we don't want to throw
        using var _ = await this._connectionLock.AcquireAsync(CancellationToken.None).ConfigureAwait(false);

        // 1-min increments is kind of an arbitrary choice. We want to avoid this being too short since each time
        // we "come up to breathe" that's a waste of resources. We also want to avoid this being too long since
        // in case people have some kind of monitoring set up for hanging queries
        await connection.SleepAsync(
                sleepTime: TimeSpan.FromMinutes(1),
                cancellationToken: cancellationToken,
                executor: (command, token) => command.ExecuteNonQueryAsync(token, disallowAsyncCancellation: false, isConnectionMonitoringQuery: true)
            ).TryAwait();

        return true;
    }

    private async Task<bool> DoKeepaliveAsync(TimeoutValue keepaliveCadence, CancellationToken stateChangedToken)
    {
        await Task.Delay(keepaliveCadence.InMilliseconds, stateChangedToken).TryAwait();
        if (stateChangedToken.IsCancellationRequested) { return true; }

        // retrieve only after the delay to avoid this reference longer than needed
        if (!this._weakConnection.TryGetTarget(out var connection)) { return false; }

        // We do a zero-wait try-lock here because if the connection is in-use then someone is querying with it. In that case,
        // There's no need for us to run a keepalive query. Since we are using zero timeout, we don't bother to pass the cancellationToken;
        // this saves us from having to handle cancellation exceptions
        using var connectionLockHandle = await this._connectionLock.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
        if (connectionLockHandle != null)
        {
            using var command = connection.CreateCommand();
            command.SetCommandText("SELECT 0 /* DistributedLock connection keepalive */");
            // Since this query is very fast and non-blocking, we don't bother trying to cancel it. This avoids having 
            // to deal with the overhead of throwing exceptions within ExecuteNonQueryAsync()
            await command.ExecuteNonQueryAsync(CancellationToken.None, disallowAsyncCancellation: false, isConnectionMonitoringQuery: true).AsTask().TryAwait();
        }

        return true;
    }

    private sealed class MonitoringHandle(ConnectionMonitor keepaliveHelper, CancellationToken cancellationToken) : IDatabaseConnectionMonitoringHandle
    {
        private ConnectionMonitor? _monitor = keepaliveHelper;
        private readonly CancellationToken _connectionLostToken = cancellationToken;

        public CancellationToken ConnectionLostToken => Volatile.Read(ref this._monitor) != null ? this._connectionLostToken : throw new ObjectDisposedException("handle");

        public void Dispose() => Interlocked.Exchange(ref this._monitor, null)?.ReleaseMonitoringHandle(this);
    }

    private sealed class AlreadyCanceledHandle : IDatabaseConnectionMonitoringHandle
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        public AlreadyCanceledHandle()
        {
            this._cancellationTokenSource.Cancel();
        }

        public CancellationToken ConnectionLostToken => this._cancellationTokenSource.Token;

        public void Dispose() => this._cancellationTokenSource.Dispose();
    }

    private sealed class NullHandle : IDatabaseConnectionMonitoringHandle
    {
        public static readonly NullHandle Instance = new();

        private NullHandle() { }

        public CancellationToken ConnectionLostToken => CancellationToken.None;

        public void Dispose() { }
    }

    private enum State : byte
    {
        Idle,
        Active,
        AutoStopped,
        Stopped,
        Disposed,
    }
}
