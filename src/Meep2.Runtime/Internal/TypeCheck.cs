using System.Collections.Concurrent;

namespace Meep2.Runtime.Internal;

/// <summary>
/// Remembers a yes-or-no answer for each message type, so a check that's slow the first time (such as compiling an
/// expression) is fast afterwards. Most streams carry one type, so the last answer is kept closest to hand.
/// </summary>
internal sealed class TypeCheck(Func<Type, bool> check)
{
    private readonly ConcurrentDictionary<Type, bool> _answers = new();
    private volatile Answer? _last;

    public bool Check(Type type)
    {
        var last = _last;
        if (last is not null && last.Type == type)
            return last.Result;

        var result = _answers.GetOrAdd(type, check);
        _last = new Answer(type, result);
        return result;
    }

    private sealed record Answer(Type Type, bool Result);
}
