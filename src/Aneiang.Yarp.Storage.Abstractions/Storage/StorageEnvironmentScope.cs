namespace Aneiang.Yarp.Storage;

/// <summary>
/// Temporarily points storage at an environment other than the one being edited, without
/// disturbing the editing view.
/// </summary>
/// <remarks>
/// <para>
/// Every repository opens connections through a factory that asks
/// <see cref="IStorageEnvironmentResolver.CurrentEnvironment"/> which store to use. That is the
/// right default — the control plane should read and write what the operator is looking at — but it
/// makes two legitimate operations impossible: diffing another environment, and writing into one
/// during a promotion.
/// </para>
/// <para>
/// The obvious workaround is to switch the view, do the work, switch back. That is not safe here:
/// switching reloads the working set and, when the target is the runtime environment, publishes to
/// live traffic. A read used to render a diff would briefly deploy the very configuration it was
/// describing. So instead this scope redirects <b>connections</b> for the current async flow only.
/// </para>
/// <para>
/// The override is an <see cref="AsyncLocal{T}"/>, so it cannot leak into another request. It does
/// flow into tasks started inside the scope, which is the intent; do not start long-lived background
/// work inside one.
/// </para>
/// </remarks>
public static class StorageEnvironmentScope
{
    private static readonly AsyncLocal<string?> AmbientEnvironment = new();

    /// <summary>Environment connections are currently redirected to, or <c>null</c> when not overridden.</summary>
    public static string? Ambient => AmbientEnvironment.Value;

    /// <summary>
    /// Redirects storage to <paramref name="environment"/> until the returned handle is disposed.
    /// Scopes nest; disposing restores the previous value.
    /// </summary>
    public static IDisposable Enter(string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);

        var previous = AmbientEnvironment.Value;
        AmbientEnvironment.Value = environment;
        return new Handle(previous);
    }

    private sealed class Handle(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            AmbientEnvironment.Value = previous;
        }
    }
}
