using System.Globalization;
using Foundation;
using UserNotifications;

namespace ClassIsland.iOS.Services.Notifications;

/// <summary>
/// 仅管理 ClassIsland 课程提醒前缀下的 iOS 本地通知。
/// </summary>
internal sealed class IosLessonNotificationScheduler
{
    private const string IdentifierPrefix = "classisland.lessons.";
    internal const string CategoryIdentifier = "classisland.lessons";
    private const string CatchUpHistoryKey = "classisland.lessons.catch-up-history";

    private readonly IosNotificationMutationGate _mutationGate;
    private readonly HashSet<string> _catchUpHistory = LoadCatchUpHistory();

    public IosLessonNotificationScheduler(IosNotificationMutationGate mutationGate)
    {
        _mutationGate = mutationGate;
    }

    public Task<IReadOnlyList<IosLessonNotificationRequest>> SynchronizeAsync(
        IReadOnlyCollection<IosLessonNotificationRequest> requests,
        Action<IReadOnlyList<IosLessonNotificationRequest>> publishConfirmedRequests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publishConfirmedRequests);
        return _mutationGate.ExecuteAsync(
            async () =>
            {
                var synchronizedRequests = await SynchronizeCoreAsync(
                    requests,
                    cancellationToken);
                publishConfirmedRequests(synchronizedRequests);
                return synchronizedRequests;
            },
            cancellationToken);
    }

    private async Task<IReadOnlyList<IosLessonNotificationRequest>> SynchronizeCoreAsync(
        IReadOnlyCollection<IosLessonNotificationRequest> requests,
        CancellationToken cancellationToken)
    {
        var notificationCenter = UNUserNotificationCenter.Current;
        var pending = await notificationCenter.GetPendingNotificationRequestsAsync() ?? [];
        cancellationToken.ThrowIfCancellationRequested();

        var distinctCandidates = requests
            .GroupBy(x => x.Identifier, StringComparer.Ordinal)
            .Select(x => x.First())
            .ToArray();
        var pendingByIdentifier = pending
            .GroupBy(x => x.Identifier, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var pendingIdentifiers = pendingByIdentifier.Keys
            .ToHashSet(StringComparer.Ordinal);
        var nonManagedPendingCount = pendingIdentifiers.Count(x => !x.StartsWith(
            IdentifierPrefix,
            StringComparison.Ordinal));
        var maximumManagedCount = IosNotificationCapacityPolicy
            .GetMaximumManagedNotificationCount(
                IosLessonNotificationScheduleFactory.MaximumPendingNotifications,
                nonManagedPendingCount);
        var distinctRequests = maximumManagedCount > 0
            ? IosLessonNotificationScheduleSelector.Select(
                    distinctCandidates,
                    maximumManagedCount)
                .ToArray()
            : [];
        var deliveredIdentifiers = distinctRequests.Any(x => x.IsCatchUp)
            ? (await notificationCenter.GetDeliveredNotificationsAsync() ?? [])
                .Select(x => x.Request.Identifier)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();

        var logicallySatisfiedCatchUpIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        var requestsToSubmit = new List<IosLessonNotificationRequest>(distinctRequests.Length);
        foreach (var request in distinctRequests)
        {
            if (request.IsCatchUp &&
                (_catchUpHistory.Contains(request.Identifier) ||
                 pendingIdentifiers.Contains(request.Identifier) ||
                 deliveredIdentifiers.Contains(request.Identifier)))
            {
                logicallySatisfiedCatchUpIdentifiers.Add(request.Identifier);
                continue;
            }

            if (request.FireAt > DateTimeOffset.Now.AddSeconds(1))
            {
                requestsToSubmit.Add(request);
            }
        }

        var desiredNativeIdentifiers = requestsToSubmit
            .Select(x => x.Identifier)
            .Concat(logicallySatisfiedCatchUpIdentifiers.Where(pendingIdentifiers.Contains))
            .ToArray();
        var plan = IosNotificationSynchronizationPolicy.CreatePlan(
            desiredNativeIdentifiers,
            requestsToSubmit.Select(x => x.Identifier),
            pendingIdentifiers,
            IdentifierPrefix,
            IosNotificationCapacityPolicy.MaximumPendingNotificationCount);
        var requestsToSubmitByIdentifier = requestsToSubmit.ToDictionary(
            x => x.Identifier,
            StringComparer.Ordinal);
        var modifiedIdentifiers = new List<string>();
        var removedObsoleteIdentifiers = new List<string>();
        IReadOnlyList<IosLessonNotificationRequest> synchronizedRequests;
        try
        {
            var identifiersRequiredDuringSwap = plan.RequestedIdentifiers
                .Where(pendingIdentifiers.Contains)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var step in plan.UpsertSteps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (step.ObsoleteIdentifierToRemoveBeforeUpsert is { } obsoleteIdentifier)
                {
                    removedObsoleteIdentifiers.Add(obsoleteIdentifier);
                    notificationCenter.RemovePendingNotificationRequests(
                        [obsoleteIdentifier]);
                }

                var request = requestsToSubmitByIdentifier[step.Identifier];
                // Journal before crossing the native boundary: the API may accept
                // a request and still surface an error to managed code.
                modifiedIdentifiers.Add(step.Identifier);
                await SubmitRequestAsync(notificationCenter, request);
                identifiersRequiredDuringSwap.Add(step.Identifier);

                if (step.ObsoleteIdentifierToRemoveBeforeUpsert != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var confirmedAfterStep = await GetPendingIdentifiersAsync(
                        notificationCenter);
                    EnsurePendingIdentifiers(
                        identifiersRequiredDuringSwap,
                        confirmedAfterStep,
                        $"提交课程通知 {step.Identifier} 后");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var confirmedPendingIdentifiers = await GetPendingIdentifiersAsync(
                notificationCenter);
            EnsurePendingIdentifiers(
                plan.RequestedIdentifiers,
                confirmedPendingIdentifiers,
                "提交新的课程通知后");

            if (plan.ObsoleteIdentifiersToRemoveAfterUpsert.Count > 0)
            {
                removedObsoleteIdentifiers.AddRange(
                    plan.ObsoleteIdentifiersToRemoveAfterUpsert);
                notificationCenter.RemovePendingNotificationRequests(
                    plan.ObsoleteIdentifiersToRemoveAfterUpsert.ToArray());
            }

            cancellationToken.ThrowIfCancellationRequested();
            confirmedPendingIdentifiers = await GetPendingIdentifiersAsync(
                notificationCenter);
            EnsurePendingIdentifiers(
                plan.RequestedIdentifiers,
                confirmedPendingIdentifiers,
                "清理旧课程通知后");
            synchronizedRequests = distinctRequests
                .Where(x => logicallySatisfiedCatchUpIdentifiers.Contains(x.Identifier) ||
                            confirmedPendingIdentifiers.Contains(x.Identifier))
                .ToArray();
        }
        catch (Exception synchronizationException)
        {
            var rollbackExceptions = await RollbackAsync(
                notificationCenter,
                modifiedIdentifiers,
                removedObsoleteIdentifiers,
                pendingByIdentifier);
            if (rollbackExceptions.Count > 0)
            {
                throw new IosNotificationSynchronizationRollbackException(
                    synchronizationException,
                    rollbackExceptions);
            }

            throw;
        }

        var catchUpHistoryChanged = false;
        foreach (var identifier in synchronizedRequests
                     .Where(x => x.IsCatchUp)
                     .Select(x => x.Identifier))
        {
            catchUpHistoryChanged |= _catchUpHistory.Add(identifier);
        }
        if (catchUpHistoryChanged)
        {
            try
            {
                SaveCatchUpHistory();
            }
            catch (Exception exception)
            {
                // 原生排程已经完整提交；历史持久化失败不应把一个有效排程
                // 降级成未知状态。当前进程仍保留内存去重集合。
                Console.Error.WriteLine($"保存 iOS 补发通知历史失败：{exception}");
            }
        }

        return synchronizedRequests;
    }

    private static async Task SubmitRequestAsync(
        UNUserNotificationCenter notificationCenter,
        IosLessonNotificationRequest request)
    {
        var localFireAt = request.FireAt.LocalDateTime;
        using var dateComponents = new NSDateComponents
        {
            Year = localFireAt.Year,
            Month = localFireAt.Month,
            Day = localFireAt.Day,
            Hour = localFireAt.Hour,
            Minute = localFireAt.Minute,
            Second = localFireAt.Second,
            TimeZone = NSTimeZone.LocalTimeZone
        };
        using var trigger = UNCalendarNotificationTrigger.CreateTrigger(
            dateComponents,
            false);
        using var content = new UNMutableNotificationContent
        {
            Title = request.Title,
            Body = request.Body,
            CategoryIdentifier = CategoryIdentifier,
            ThreadIdentifier = CategoryIdentifier
        };
        if (request.PlaySound)
        {
            content.Sound = UNNotificationSound.Default;
        }

        using var nativeRequest = UNNotificationRequest.FromIdentifier(
            request.Identifier,
            content,
            trigger);
        await notificationCenter.AddNotificationRequestAsync(nativeRequest);
    }

    private static async Task<IReadOnlyList<Exception>> RollbackAsync(
        UNUserNotificationCenter notificationCenter,
        IReadOnlyCollection<string> modifiedIdentifiers,
        IReadOnlyCollection<string> removedObsoleteIdentifiers,
        IReadOnlyDictionary<string, UNNotificationRequest> pendingByIdentifier)
    {
        var rollbackPlan = IosNotificationSynchronizationPolicy.CreateRollbackPlan(
            modifiedIdentifiers,
            pendingByIdentifier.Keys,
            removedObsoleteIdentifiers);
        if (rollbackPlan.AddedIdentifiersToRemove.Count > 0)
        {
            notificationCenter.RemovePendingNotificationRequests(
                rollbackPlan.AddedIdentifiersToRemove.ToArray());
        }

        var exceptions = new List<Exception>();
        // Restore the complete original managed snapshot. Besides explicitly
        // replaced/removed items, iOS may silently evict another request when
        // its native limit is reached.
        foreach (var identifier in pendingByIdentifier.Keys
                     .Where(x => x.StartsWith(IdentifierPrefix, StringComparison.Ordinal)))
        {
            try
            {
                await notificationCenter.AddNotificationRequestAsync(
                    pendingByIdentifier[identifier]);
            }
            catch (Exception exception)
            {
                exceptions.Add(exception);
            }
        }

        try
        {
            var restoredIdentifiers = await GetPendingIdentifiersAsync(notificationCenter);
            var missingOriginalManagedIdentifiers = pendingByIdentifier.Keys
                .Where(x => x.StartsWith(IdentifierPrefix, StringComparison.Ordinal) &&
                            !restoredIdentifiers.Contains(x))
                .ToArray();
            if (missingOriginalManagedIdentifiers.Length > 0)
            {
                exceptions.Add(new InvalidOperationException(
                    "The previous iOS notification schedule was not fully restored: " +
                    string.Join(", ", missingOriginalManagedIdentifiers)));
            }
        }
        catch (Exception exception)
        {
            exceptions.Add(exception);
        }

        return exceptions;
    }

    private static async Task<HashSet<string>> GetPendingIdentifiersAsync(
        UNUserNotificationCenter notificationCenter) =>
        (await notificationCenter.GetPendingNotificationRequestsAsync() ?? [])
        .Select(x => x.Identifier)
        .ToHashSet(StringComparer.Ordinal);

    private static void EnsurePendingIdentifiers(
        IEnumerable<string> expectedIdentifiers,
        IReadOnlySet<string> pendingIdentifiers,
        string operation)
    {
        var missingIdentifiers = IosNotificationSynchronizationPolicy
            .GetMissingIdentifiers(expectedIdentifiers, pendingIdentifiers);
        if (missingIdentifiers.Count > 0)
        {
            throw new InvalidOperationException(
                $"{operation}，iOS 未保留以下课程通知：" +
                string.Join(", ", missingIdentifiers));
        }
    }

    private static HashSet<string> LoadCatchUpHistory()
    {
        var raw = NSUserDefaults.StandardUserDefaults.StringForKey(CatchUpHistoryKey);
        return string.IsNullOrWhiteSpace(raw)
            ? new HashSet<string>(StringComparer.Ordinal)
            : raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsRecentCatchUpIdentifier)
                .ToHashSet(StringComparer.Ordinal);
    }

    private void SaveCatchUpHistory()
    {
        _catchUpHistory.RemoveWhere(x => !IsRecentCatchUpIdentifier(x));
        var value = string.Join('\n', _catchUpHistory.OrderBy(x => x, StringComparer.Ordinal));
        NSUserDefaults.StandardUserDefaults.SetString(value, CatchUpHistoryKey);
    }

    private static bool IsRecentCatchUpIdentifier(string identifier)
    {
        var parts = identifier.Split('.');
        return parts.Length >= 3 &&
               DateTime.TryParseExact(
                   parts[2],
                   "yyyyMMdd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out var date) &&
               date >= DateTime.Today.AddDays(-1);
    }
}

internal sealed class IosNotificationSynchronizationRollbackException(
    Exception synchronizationException,
    IReadOnlyCollection<Exception> rollbackExceptions)
    : Exception(
        "回滚 iOS/iPadOS 课程通知排程失败，当前原生排程状态无法确认。",
        new AggregateException(
            new[] { synchronizationException }.Concat(rollbackExceptions)))
{
}
