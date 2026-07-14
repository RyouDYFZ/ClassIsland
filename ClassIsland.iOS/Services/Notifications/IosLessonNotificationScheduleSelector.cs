namespace ClassIsland.iOS.Services.Notifications;

/// <summary>
/// 在 iOS 待处理通知上限内，按上课日均衡保留课程提醒。
/// </summary>
internal static class IosLessonNotificationScheduleSelector
{
    public static IReadOnlyList<IosLessonNotificationRequest> Select(
        IEnumerable<IosLessonNotificationRequest> requests,
        int maximumCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);

        var orderedRequests = requests
            .OrderBy(x => x.FireAt)
            .ThenBy(x => x.Identifier, StringComparer.Ordinal)
            .ToArray();
        if (orderedRequests.Length <= maximumCount)
        {
            return orderedRequests;
        }

        var requestsByDay = orderedRequests
            .GroupBy(x => DateOnly.FromDateTime(x.FireAt.LocalDateTime))
            .Select(x => x.ToArray())
            .ToArray();

        // 当前工厂最多扫描 60 天，因此在预留 4 个系统通知槽位的情况下，
        // 正常不会有超过上限的上课日。仍保留防御性分支，优先覆盖最近日期。
        if (requestsByDay.Length > maximumCount)
        {
            return requestsByDay
                .Take(maximumCount)
                .Select(x => x[0])
                .ToArray();
        }

        var quotas = Enumerable.Repeat(1, requestsByDay.Length).ToArray();
        var remaining = maximumCount - requestsByDay.Length;
        while (remaining > 0)
        {
            var allocated = false;
            for (var index = 0; index < requestsByDay.Length && remaining > 0; index++)
            {
                if (quotas[index] >= requestsByDay[index].Length)
                {
                    continue;
                }

                quotas[index]++;
                remaining--;
                allocated = true;
            }

            if (!allocated)
            {
                break;
            }
        }

        var selected = new List<IosLessonNotificationRequest>(maximumCount);
        for (var index = 0; index < requestsByDay.Length; index++)
        {
            selected.AddRange(SelectEvenly(requestsByDay[index], quotas[index]));
        }

        return selected
            .OrderBy(x => x.FireAt)
            .ThenBy(x => x.Identifier, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<IosLessonNotificationRequest> SelectEvenly(
        IReadOnlyList<IosLessonNotificationRequest> requests,
        int count)
    {
        if (count >= requests.Count)
        {
            return requests;
        }

        if (count == 1)
        {
            return [requests[0]];
        }

        var selected = new IosLessonNotificationRequest[count];
        for (var index = 0; index < count; index++)
        {
            var sourceIndex = index * (requests.Count - 1) / (count - 1);
            selected[index] = requests[sourceIndex];
        }

        return selected;
    }
}
