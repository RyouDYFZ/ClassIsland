namespace ClassIsland.iOS.Services.Notifications;

/// <summary>
/// Serializes mutations of the app-wide iOS notification center.
/// </summary>
internal sealed class IosNotificationMutationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task ExecuteAsync(
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
