namespace RoboMouse.Core;

/// <summary>
/// Runs work items on the thread pool one at a time, in the order they were queued, with a cap on how
/// many may wait. Used to serve a peer's file requests: each one reads from disk or from another peer,
/// which must not happen on the connection's receive thread (it also answers pings), and a peer must
/// not be able to start an unbounded number of them at once. Thread-safe.
/// </summary>
public sealed class SerialWorkQueue
{
    private readonly object _lock = new();
    private readonly Queue<Action> _queue = new();
    private readonly int _maxPending;
    private bool _running;

    public SerialWorkQueue(int maxPending)
    {
        _maxPending = maxPending;
    }

    /// <summary>Queues <paramref name="work"/>. Returns false (and drops it) when the queue is full.</summary>
    public bool TryEnqueue(Action work)
    {
        lock (_lock)
        {
            if (_queue.Count >= _maxPending)
                return false;
            _queue.Enqueue(work);
            if (_running)
                return true;
            _running = true;
        }
        ThreadPool.UnsafeQueueUserWorkItem(_ => Drain(), null);
        return true;
    }

    private void Drain()
    {
        while (true)
        {
            Action work;
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    _running = false;
                    return;
                }
                work = _queue.Dequeue();
            }

            try
            {
                work();
            }
            catch (Exception ex)
            {
                Logging.SimpleLogger.Log("Queue", $"Queued work failed: {ex.Message}");
            }
        }
    }
}
