namespace Meep2.Tests.Support;

public static class Wait
{
    /// <summary>Long enough for anything a test waits on, short enough that a hang fails the test.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>Waits until a condition holds, failing after <see cref="Limit"/>.</summary>
    public static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition never came true.");
            await Task.Delay(5);
        }
    }

    /// <summary>Waits until an asynchronous condition holds, failing after <see cref="Limit"/>.</summary>
    public static async Task Until(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition never came true.");
            await Task.Delay(20);
        }
    }

    /// <summary>Waits for a task, failing after <see cref="Limit"/>.</summary>
    public static Task For(Task task) => task.WaitAsync(Limit);

    /// <summary>Waits for a task's result, failing after <see cref="Limit"/>.</summary>
    public static Task<T> For<T>(Task<T> task) => task.WaitAsync(Limit);

    /// <summary>Joins values with commas, so order can be checked with one readable assertion.</summary>
    public static string Join<T>(IEnumerable<T> values) => string.Join(",", values);
}
