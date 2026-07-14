namespace ClassIsland.iOS.Services.Notifications;

/// <summary>
/// 在 iOS 待处理通知上限内，按上课日均衡且按链原子保留课程提醒。
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

        var groups = orderedRequests
            .GroupBy(x => x.ChainId ?? x.Identifier, StringComparer.Ordinal)
            .Select(x => x.OrderBy(request => request.FireAt)
                .ThenBy(request => request.Identifier, StringComparer.Ordinal)
                .ToArray())
            .OrderBy(x => x[0].FireAt)
            .ThenBy(x => x[0].Identifier, StringComparer.Ordinal)
            .ToArray();
        var groupsByDay = groups
            .GroupBy(x => DateOnly.FromDateTime(x[^1].FireAt.LocalDateTime))
            .Select(x => x.ToArray())
            .ToArray();

        var initialGroupIndexes = groupsByDay
            .Select(FindPreferredGroupIndex)
            .ToArray();

        // 同一课程的准备/上课请求必须作为一个原子组提交，不能拆开提醒链。
        var selectedIndexes = groupsByDay
            .Select((_, index) => new HashSet<int> { initialGroupIndexes[index] })
            .ToArray();
        var selectedRequestCount = groupsByDay
            .Select((groupsForDay, index) =>
                groupsForDay[initialGroupIndexes[index]].Length)
            .Sum();
        if (selectedRequestCount > maximumCount)
        {
            return SelectGroupsWithinBudget(
                    groupsByDay.Select((groupsForDay, index) =>
                        groupsForDay[initialGroupIndexes[index]]),
                    maximumCount)
                .SelectMany(x => x)
                .OrderBy(x => x.FireAt)
                .ThenBy(x => x.Identifier, StringComparer.Ordinal)
                .ToArray();
        }

        var remaining = maximumCount - selectedRequestCount;
        while (remaining > 0)
        {
            var allocated = false;
            for (var index = 0; index < groupsByDay.Length && remaining > 0; index++)
            {
                var candidateIndex = FindMostSeparatedGroupIndex(
                    groupsByDay[index],
                    selectedIndexes[index],
                    remaining);
                if (candidateIndex < 0)
                {
                    continue;
                }

                var group = groupsByDay[index][candidateIndex];
                selectedIndexes[index].Add(candidateIndex);
                remaining -= group.Length;
                allocated = true;
            }

            if (!allocated)
            {
                break;
            }
        }

        return groupsByDay
            .SelectMany((dayGroups, dayIndex) => selectedIndexes[dayIndex]
                .OrderBy(x => x)
                .Select(groupIndex => dayGroups[groupIndex]))
            .SelectMany(group => group)
            .OrderBy(x => x.FireAt)
            .ThenBy(x => x.Identifier, StringComparer.Ordinal)
            .ToArray();
    }

    private static int FindMostSeparatedGroupIndex(
        IReadOnlyList<IosLessonNotificationRequest[]> groups,
        IReadOnlySet<int> selectedIndexes,
        int remainingRequestCount)
    {
        var bestIndex = -1;
        var bestDistance = -1;
        var bestPriority = int.MaxValue;
        for (var index = 0; index < groups.Count; index++)
        {
            if (selectedIndexes.Contains(index) ||
                groups[index].Length > remainingRequestCount)
            {
                continue;
            }

            var priority = GetGroupPriority(groups[index]);
            var distance = selectedIndexes.Min(selected => Math.Abs(selected - index));
            if (priority < bestPriority ||
                priority == bestPriority &&
                (distance > bestDistance ||
                 distance == bestDistance && index > bestIndex))
            {
                bestIndex = index;
                bestDistance = distance;
                bestPriority = priority;
            }
        }

        return bestIndex;
    }

    private static int FindPreferredGroupIndex(
        IReadOnlyList<IosLessonNotificationRequest[]> groups)
    {
        var bestIndex = 0;
        var bestPriority = GetGroupPriority(groups[0]);
        var bestRequestCount = groups[0].Length;
        for (var index = 1; index < groups.Count; index++)
        {
            var priority = GetGroupPriority(groups[index]);
            var requestCount = groups[index].Length;
            if (priority < bestPriority ||
                priority == bestPriority && requestCount < bestRequestCount)
            {
                bestIndex = index;
                bestPriority = priority;
                bestRequestCount = requestCount;
            }
        }

        // 先选择当天最高优先级、占用额度最少的完整链；同成本时保留最早课程。
        return bestIndex;
    }

    private static int GetGroupPriority(
        IReadOnlyCollection<IosLessonNotificationRequest> group)
    {
        if (group.Any(x =>
                x.ChannelId == IosNotificationSchedulingPolicy.OnClassChannelId))
        {
            return 0;
        }

        return group.Any(x =>
            x.ChannelId == IosNotificationSchedulingPolicy.PrepareOnClassChannelId)
            ? 1
            : 2;
    }

    private static IEnumerable<IosLessonNotificationRequest[]> SelectGroupsWithinBudget(
        IEnumerable<IosLessonNotificationRequest[]> groups,
        int maximumCount)
    {
        var remaining = maximumCount;
        foreach (var candidate in groups
                     .Select((group, index) => (Group: group, DayIndex: index))
                     .OrderBy(x => x.Group.Length)
                     .ThenBy(x => x.DayIndex))
        {
            var group = candidate.Group;
            if (group.Length > remaining)
            {
                continue;
            }

            yield return group;
            remaining -= group.Length;
            if (remaining == 0)
            {
                yield break;
            }
        }
    }
}
