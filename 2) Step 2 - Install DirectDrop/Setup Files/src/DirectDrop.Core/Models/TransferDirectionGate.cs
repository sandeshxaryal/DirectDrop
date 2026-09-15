namespace DirectDrop.Core.Models;

/// <summary>
/// LAN-bandwidth arbiter for DirectDrop's single hotspot link.
/// Transfers in the same direction may run concurrently; opposite directions
/// are not allowed to actively move bytes at the same time. This avoids the
/// common Wi-Fi Mobile Hotspot case where an uplink and downlink compete for
/// the same radio/host queues and produce a lower total throughput.
/// </summary>
public sealed class TransferDirectionGate : IDisposable
{
    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private TransferDirection? _activeDirection;
    private int _activeCount;
    private bool _disposed;

    public Task<Lease> AcquireAsync(TransferDirection direction, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            if (_activeCount == 0 || _activeDirection == direction)
            {
                _activeDirection = direction;
                _activeCount++;
                return Task.FromResult(new Lease(this));
            }

            var waiter = new Waiter(direction);
            waiter.Node = _waiters.AddLast(waiter);
            waiter.CancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var tuple = ((TransferDirectionGate Gate, Waiter Waiter))state!;
                    tuple.Gate.CancelWaiter(tuple.Waiter);
                }, (this, waiter));
            return WaitForGrantAsync(waiter, cancellationToken);
        }
    }

    private async Task<Lease> WaitForGrantAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        try
        {
            await waiter.Granted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            waiter.CancellationRegistration.Dispose();
            return new Lease(this);
        }
        catch (OperationCanceledException)
        {
            // If grant and cancellation raced, the gate has already counted us
            // as active. Return that slot before propagating cancellation.
            if (Volatile.Read(ref waiter.GrantedFlag) != 0)
            {
                Release();
            }
            throw;
        }
    }

    private void CancelWaiter(Waiter waiter)
    {
        lock (_sync)
        {
            if (Volatile.Read(ref waiter.GrantedFlag) != 0)
                return;

            var node = waiter.Node;
            if (node is null)
                return;

            _waiters.Remove(node);
            waiter.Node = null;
            waiter.CancellationRegistration.Dispose();
        }
    }

    private void Release()
    {
        lock (_sync)
        {
            if (_activeCount <= 0)
                return;

            _activeCount--;
            if (_activeCount > 0)
                return;

            _activeDirection = null;
            GrantNextDirectionGroup();
        }
    }

    private void GrantNextDirectionGroup()
    {
        if (_waiters.First is null)
            return;

        TransferDirection direction = _waiters.First.Value.Direction;
        _activeDirection = direction;

        var node = _waiters.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.Direction == direction)
            {
                Waiter waiter = node.Value;
                _waiters.Remove(node);
                waiter.Node = null;
                Volatile.Write(ref waiter.GrantedFlag, 1);
                _activeCount++;
                waiter.CancellationRegistration.Dispose();
                waiter.Granted.TrySetResult(true);
            }
            node = next;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TransferDirectionGate));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (Waiter waiter in _waiters)
            {
                waiter.Node = null;
                waiter.CancellationRegistration.Dispose();
                waiter.Granted.TrySetCanceled();
            }
            _waiters.Clear();
        }
    }

    private sealed class Waiter
    {
        public Waiter(TransferDirection direction) => Direction = direction;
        public TransferDirection Direction { get; }
        public TaskCompletionSource<bool> Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
        public CancellationTokenRegistration CancellationRegistration { get; set; }
        public int GrantedFlag;
    }

    public sealed class Lease : IDisposable
    {
        private TransferDirectionGate? _gate;
        internal Lease(TransferDirectionGate gate) => _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
