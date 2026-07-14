namespace ClassIsland.iOS.Services.Notifications;

internal static class IosNotificationCapacityPolicy
{
    internal const int MaximumPendingNotificationCount = 64;
    internal const int ReservedFallbackNotificationCount = 1;

    public static int GetMaximumManagedNotificationCount(
        int configuredMaximumManagedCount,
        int nonManagedPendingCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(configuredMaximumManagedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nonManagedPendingCount);

        return Math.Min(
            configuredMaximumManagedCount,
            Math.Max(
                0,
                MaximumPendingNotificationCount -
                nonManagedPendingCount -
                ReservedFallbackNotificationCount));
    }

    public static IosFallbackNotificationCapacityDecision GetFallbackSubmissionDecision(
        string identifier,
        IEnumerable<string> pendingIdentifiers,
        TimeSpan capacityWaitElapsed,
        TimeSpan maximumCapacityWait)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        ArgumentNullException.ThrowIfNull(pendingIdentifiers);
        if (capacityWaitElapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityWaitElapsed));
        }
        if (maximumCapacityWait <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCapacityWait));
        }

        var pending = pendingIdentifiers.ToHashSet(StringComparer.Ordinal);
        if (pending.Contains(identifier) ||
            pending.Count < MaximumPendingNotificationCount)
        {
            return IosFallbackNotificationCapacityDecision.Submit;
        }

        return capacityWaitElapsed < maximumCapacityWait
            ? IosFallbackNotificationCapacityDecision.Retry
            : IosFallbackNotificationCapacityDecision.CapacityExhausted;
    }
}

internal enum IosFallbackNotificationCapacityDecision
{
    Submit,
    Retry,
    CapacityExhausted
}
