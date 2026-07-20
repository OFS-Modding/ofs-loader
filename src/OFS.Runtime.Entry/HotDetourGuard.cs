using System.Linq.Expressions;

namespace OFS.Runtime.Entry;

internal static class HotDetourGuard
{
    internal static TDelegate Wrap<TDelegate>(
        string ownerId,
        string hookId,
        TDelegate replacement)
        where TDelegate : Delegate
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hookId);
        ArgumentNullException.ThrowIfNull(replacement);

        var invoke = typeof(TDelegate).GetMethod("Invoke")
            ?? throw new ArgumentException("Hook delegate has no Invoke method.", nameof(replacement));
        var parameters = invoke.GetParameters()
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();
        var lease = Expression.Variable(typeof(HotRuntimeCallbackLease), "hotLease");
        var enter = Expression.Assign(
            lease,
            Expression.Call(
                typeof(ModSafetyStore),
                nameof(ModSafetyStore.EnterHotRuntimeCallback),
                Type.EmptyTypes,
                Expression.Constant(ownerId),
                Expression.Constant($"detour:{hookId}")));
        var invokeReplacement = Expression.Invoke(
            Expression.Constant(replacement, typeof(TDelegate)),
            parameters);
        var dispose = Expression.Call(
            lease,
            typeof(HotRuntimeCallbackLease).GetMethod(nameof(IDisposable.Dispose))!);
        var body = Expression.Block(
            [lease],
            enter,
            Expression.TryFinally(invokeReplacement, dispose));
        return Expression.Lambda<TDelegate>(body, parameters).Compile();
    }
}
