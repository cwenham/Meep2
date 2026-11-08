namespace Meep2.Runtime.Internal;

/// <summary>
/// The most recent messages from a pipeline's sources, and which of them ended in an error nothing handled, for
/// the error policy's <c>stop-pipeline at N%</c>.
/// </summary>
internal sealed class SourceWindow(int size)
{
    private readonly Guid[] _ids = new Guid[size];
    private readonly bool[] _failed = new bool[size];
    private readonly Lock _lock = new();
    private int _next;
    private int _count;
    private int _failedCount;

    /// <summary>Records a message a source sent, forgetting the oldest once the window is full.</summary>
    public void Add(Guid id)
    {
        lock (_lock)
        {
            if (_count == size)
            {
                if (_failed[_next])
                    _failedCount--;
            }
            else
            {
                _count++;
            }
            _ids[_next] = id;
            _failed[_next] = false;
            _next = (_next + 1) % size;
        }
    }

    /// <summary>Marks a message as having ended in an unhandled error, if it's still in the window.</summary>
    /// <returns>How many messages in the window failed, and how many there are.</returns>
    public (int Failed, int Count) MarkFailed(Guid id)
    {
        lock (_lock)
        {
            for (var i = 0; i < _count; i++)
            {
                if (_ids[i] == id)
                {
                    if (!_failed[i])
                    {
                        _failed[i] = true;
                        _failedCount++;
                    }
                    break;
                }
            }
            return (_failedCount, _count);
        }
    }
}
