using ClassIsland.iOS.Services.Notifications;
using Xunit;

namespace ClassIsland.Platforms.Abstractions.Tests;

public sealed class IosLessonNotificationSchedulingTests
{
    [Fact]
    public void Select_BalancesDenseScheduleAcrossFullSevenDayHorizon()
    {
        var firstDay = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var requests = Enumerable.Range(0, 7)
            .SelectMany(day => Enumerable.Range(0, 23)
                .Select(index => CreateRequest(
                    $"day-{day}-request-{index}",
                    firstDay.AddDays(day).AddHours(7).AddMinutes(index * 25))))
            .ToArray();

        var result = IosLessonNotificationScheduleSelector.Select(requests, 60);

        Assert.Equal(60, result.Count);
        for (var day = 0; day < 7; day++)
        {
            var date = DateOnly.FromDateTime(firstDay.AddDays(day).LocalDateTime);
            var selectedForDay = result
                .Where(x => DateOnly.FromDateTime(x.FireAt.LocalDateTime) == date)
                .ToArray();
            Assert.NotEmpty(selectedForDay);
            Assert.Equal(requests.Where(x =>
                    DateOnly.FromDateTime(x.FireAt.LocalDateTime) == date).Min(x => x.FireAt),
                selectedForDay.Min(x => x.FireAt));
            Assert.Equal(requests.Where(x =>
                    DateOnly.FromDateTime(x.FireAt.LocalDateTime) == date).Max(x => x.FireAt),
                selectedForDay.Max(x => x.FireAt));
        }
    }

    [Fact]
    public void Select_KeepsSmallScheduleUnchangedAndOrdered()
    {
        var now = new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        var later = CreateRequest("later", now.AddHours(1));
        var earlier = CreateRequest("earlier", now);

        var result = IosLessonNotificationScheduleSelector.Select([later, earlier], 60);

        Assert.Equal([earlier, later], result);
    }

    [Fact]
    public void Select_NeverSplitsPreparationAndOnClassChain()
    {
        var firstDay = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var requests = Enumerable.Range(0, 7)
            .SelectMany(day => Enumerable.Range(0, 12)
                .SelectMany(index =>
                {
                    var chainId = $"day-{day}-lesson-{index}";
                    var start = firstDay.AddDays(day).AddHours(8).AddMinutes(index * 35);
                    return new[]
                    {
                        CreateRequest(
                            $"{chainId}.prepare",
                            start.AddMinutes(-5),
                            IosNotificationSchedulingPolicy.PrepareOnClassChannelId,
                            chainId),
                        CreateRequest(
                            $"{chainId}.on",
                            start,
                            IosNotificationSchedulingPolicy.OnClassChannelId,
                            chainId)
                    };
                }))
            .ToArray();

        var result = IosLessonNotificationScheduleSelector.Select(requests, 60);

        Assert.Equal(60, result.Count);
        foreach (var chain in requests.GroupBy(x => x.ChainId))
        {
            Assert.Contains(result.Count(x => x.ChainId == chain.Key), new[] { 0, 2 });
        }
        Assert.Equal(7, result
            .Select(x => DateOnly.FromDateTime(x.FireAt.LocalDateTime))
            .Distinct()
            .Count());
    }

    [Fact]
    public void Select_PrioritizesDayCoverageAcrossDenseSixtyDayWindow()
    {
        var firstDay = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var requests = Enumerable.Range(0, 40)
            .SelectMany(day =>
            {
                var chainId = $"day-{day}-lesson";
                var start = firstDay.AddDays(day).AddHours(8);
                return new[]
                {
                    CreateRequest(
                        $"{chainId}.prepare",
                        start.AddMinutes(-5),
                        IosNotificationSchedulingPolicy.PrepareOnClassChannelId,
                        chainId),
                    CreateRequest(
                        $"{chainId}.on",
                        start,
                        IosNotificationSchedulingPolicy.OnClassChannelId,
                        chainId),
                    CreateRequest(
                        $"day-{day}.break",
                        start.AddHours(1),
                        IosNotificationSchedulingPolicy.OnBreakingChannelId)
                };
            })
            .ToArray();

        var result = IosLessonNotificationScheduleSelector.Select(requests, 60);

        Assert.Equal(60, result.Count);
        Assert.Equal(30, result
            .Select(x => DateOnly.FromDateTime(x.FireAt.LocalDateTime))
            .Distinct()
            .Count());
        Assert.DoesNotContain(
            result,
            x => x.ChannelId == IosNotificationSchedulingPolicy.OnBreakingChannelId);
        Assert.All(
            result.GroupBy(x => x.ChainId),
            chain =>
            {
                Assert.NotNull(chain.Key);
                Assert.Equal(2, chain.Count());
                Assert.Contains(
                    chain,
                    x => x.ChannelId ==
                         IosNotificationSchedulingPolicy.PrepareOnClassChannelId);
                Assert.Contains(
                    chain,
                    x => x.ChannelId ==
                         IosNotificationSchedulingPolicy.OnClassChannelId);
            });
        foreach (var chain in requests.Where(x => x.ChainId != null).GroupBy(x => x.ChainId))
        {
            Assert.Contains(result.Count(x => x.ChainId == chain.Key), new[] { 0, 2 });
        }
    }

    [Fact]
    public void Select_WhenDayCountExceedsMaximum_SelectsEarliestOnClassDays()
    {
        var firstDay = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var requests = Enumerable.Range(0, 5)
            .SelectMany(day =>
            {
                var start = firstDay.AddDays(day).AddHours(8);
                return new[]
                {
                    CreateRequest(
                        $"day-{day}.on",
                        start,
                        IosNotificationSchedulingPolicy.OnClassChannelId,
                        $"day-{day}.lesson"),
                    CreateRequest(
                        $"day-{day}.break",
                        start.AddHours(1),
                        IosNotificationSchedulingPolicy.OnBreakingChannelId)
                };
            })
            .ToArray();

        var result = IosLessonNotificationScheduleSelector.Select(requests, 3);

        Assert.Equal(3, result.Count);
        Assert.Equal(
            Enumerable.Range(0, 3)
                .Select(day => DateOnly.FromDateTime(firstDay.AddDays(day).LocalDateTime)),
            result.Select(x => DateOnly.FromDateTime(x.FireAt.LocalDateTime)));
        Assert.All(
            result,
            x => Assert.Equal(
                IosNotificationSchedulingPolicy.OnClassChannelId,
                x.ChannelId));
        Assert.Equal(
            ["day-0.lesson", "day-1.lesson", "day-2.lesson"],
            result.Select(x => x.ChainId));
    }

    [Fact]
    public void Select_WhenInitialTwoTicketChainsExceedBudget_KeepsEarliestChainsAtomic()
    {
        var firstDay = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var requests = Enumerable.Range(0, 4)
            .SelectMany(day => CreateLessonChain(firstDay.AddDays(day).AddHours(8), $"day-{day}.lesson"))
            .ToArray();

        var result = IosLessonNotificationScheduleSelector.Select(requests, 5);

        Assert.Equal(4, result.Count);
        Assert.Equal(
            ["day-0.lesson", "day-1.lesson"],
            result.Select(x => x.ChainId).Distinct());
        Assert.All(
            result.GroupBy(x => x.ChainId),
            chain => Assert.Equal(2, chain.Count()));
        Assert.All(
            result.GroupBy(x => x.ChainId),
            chain => Assert.Equal(
                [
                    IosNotificationSchedulingPolicy.PrepareOnClassChannelId,
                    IosNotificationSchedulingPolicy.OnClassChannelId
                ],
                chain.Select(x => x.ChannelId)));
    }

    [Fact]
    public void Select_WhenDailyChainCostsDiffer_MaximizesCoveredDays()
    {
        var firstDay = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var requests = Enumerable.Range(0, 60)
            .SelectMany(day =>
            {
                var start = firstDay.AddDays(day).AddHours(8);
                return day < 30
                    ? CreateLessonChain(start, $"day-{day}.lesson")
                    :
                    [
                        CreateRequest(
                            $"day-{day}.on",
                            start,
                            IosNotificationSchedulingPolicy.OnClassChannelId,
                            $"day-{day}.lesson")
                    ];
            })
            .ToArray();

        var result = IosLessonNotificationScheduleSelector.Select(requests, 60);

        Assert.Equal(60, result.Count);
        Assert.Equal(45, result
            .Select(x => DateOnly.FromDateTime(x.FireAt.LocalDateTime))
            .Distinct()
            .Count());
        var selectedDates = result
            .Select(x => DateOnly.FromDateTime(x.FireAt.LocalDateTime))
            .ToHashSet();
        Assert.All(
            Enumerable.Range(30, 30),
            day => Assert.Contains(
                DateOnly.FromDateTime(firstDay.AddDays(day).LocalDateTime),
                selectedDates));
        Assert.Equal(
            Enumerable.Range(0, 15)
                .Select(day => DateOnly.FromDateTime(firstDay.AddDays(day).LocalDateTime)),
            selectedDates.Where(date => date < DateOnly.FromDateTime(
                    firstDay.AddDays(30).LocalDateTime))
                .Order());
        foreach (var chain in requests.GroupBy(x => x.ChainId))
        {
            Assert.Contains(result.Count(x => x.ChainId == chain.Key), new[] { 0, chain.Count() });
        }
    }

    [Fact]
    public void Select_WithOneSlotRemaining_DoesNotSplitAnotherTwoTicketChain()
    {
        var firstDay = new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        var requests = CreateLessonChain(firstDay, "first.lesson")
            .Concat(CreateLessonChain(firstDay.AddHours(2), "second.lesson"))
            .ToArray();

        var result = IosLessonNotificationScheduleSelector.Select(requests, 3);

        Assert.Equal(2, result.Count);
        Assert.All(result, x => Assert.Equal("first.lesson", x.ChainId));
        Assert.Equal(
            [
                IosNotificationSchedulingPolicy.PrepareOnClassChannelId,
                IosNotificationSchedulingPolicy.OnClassChannelId
            ],
            result.Select(x => x.ChannelId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Select_WithNonPositiveMaximum_Throws(int maximumCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IosLessonNotificationScheduleSelector.Select([], maximumCount));
    }

    [Fact]
    public void Select_UsesBreakOnlyWhenDayHasNoClassStartRequest()
    {
        var firstDay = new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        var requests = new[]
        {
            CreateRequest(
                "first-day.break",
                firstDay,
                IosNotificationSchedulingPolicy.OnBreakingChannelId),
            CreateRequest(
                "second-day.break",
                firstDay.AddDays(1),
                IosNotificationSchedulingPolicy.OnBreakingChannelId),
            CreateRequest(
                "second-day.on",
                firstDay.AddDays(1).AddHours(1),
                IosNotificationSchedulingPolicy.OnClassChannelId,
                "second-day.lesson")
        };

        var result = IosLessonNotificationScheduleSelector.Select(requests, 2);

        Assert.Equal(["first-day.break", "second-day.on"], result.Select(x => x.Identifier));
        Assert.Equal(
            [
                IosNotificationSchedulingPolicy.OnBreakingChannelId,
                IosNotificationSchedulingPolicy.OnClassChannelId
            ],
            result.Select(x => x.ChannelId));
    }

    [Fact]
    public void Select_FillsAdditionalLessonChainsBeforeBreaks()
    {
        var firstStart = new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        var requests = CreateLessonChain(firstStart, "first.lesson")
            .Concat(CreateLessonChain(firstStart.AddHours(2), "second.lesson"))
            .Append(CreateRequest(
                "midday.break",
                firstStart.AddHours(1),
                IosNotificationSchedulingPolicy.OnBreakingChannelId))
            .ToArray();

        var result = IosLessonNotificationScheduleSelector.Select(requests, 4);

        Assert.Equal(4, result.Count);
        Assert.DoesNotContain(
            result,
            x => x.ChannelId == IosNotificationSchedulingPolicy.OnBreakingChannelId);
        Assert.Equal(
            ["first.lesson", "second.lesson"],
            result.Select(x => x.ChainId).Distinct());
        Assert.All(
            result.GroupBy(x => x.ChainId),
            chain => Assert.Equal(2, chain.Count()));
    }

    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void ShouldRequestAuthorization_RequiresAppProviderAndChannel(
        bool appEnabled,
        bool providerEnabled,
        bool channelEnabled,
        bool expected)
    {
        var result = IosNotificationSchedulingPolicy.ShouldRequestAuthorization(
            appEnabled,
            providerEnabled,
            [channelEnabled]);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CanCompleteQueueTicket_RequiresSupportedProviderChannelAndMatchingSchedule()
    {
        var fireAt = new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        var scheduled = new[]
        {
            CreateRequest(
                "scheduled",
                fireAt,
                IosNotificationSchedulingPolicy.OnClassChannelId)
        };

        Assert.True(IosNotificationSchedulingPolicy.CanCompleteQueueTicket(
            IosNotificationSchedulingPolicy.ClassNotificationProviderId,
            IosNotificationSchedulingPolicy.OnClassChannelId,
            fireAt.LocalDateTime.AddSeconds(30),
            scheduled,
            TimeSpan.FromMinutes(1)));
        Assert.False(IosNotificationSchedulingPolicy.CanCompleteQueueTicket(
            Guid.NewGuid(),
            IosNotificationSchedulingPolicy.OnClassChannelId,
            fireAt.LocalDateTime,
            scheduled,
            TimeSpan.FromMinutes(1)));
        Assert.False(IosNotificationSchedulingPolicy.CanCompleteQueueTicket(
            IosNotificationSchedulingPolicy.ClassNotificationProviderId,
            IosNotificationSchedulingPolicy.OnBreakingChannelId,
            fireAt.LocalDateTime,
            scheduled,
            TimeSpan.FromMinutes(1)));
        Assert.False(IosNotificationSchedulingPolicy.CanCompleteQueueTicket(
            IosNotificationSchedulingPolicy.ClassNotificationProviderId,
            IosNotificationSchedulingPolicy.OnClassChannelId,
            fireAt.LocalDateTime.AddMinutes(2),
            scheduled,
            TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData(2, 8)]
    [InlineData(-2, 12)]
    public void GetExpectedQueueTicketLocalFireTime_MapsLogicalOffsetsToSystemTime(
        int logicalOffsetMinutes,
        int expectedSystemMinute)
    {
        var systemNow = new DateTimeOffset(
            2026,
            7,
            13,
            8,
            0,
            0,
            TimeSpan.Zero);
        var logicalNow = systemNow.DateTime.AddMinutes(logicalOffsetMinutes);
        var logicalClassStart = new DateTime(2026, 7, 13, 8, 10, 0);

        var expectedFireTime = IosNotificationSchedulingPolicy
            .GetExpectedQueueTicketLocalFireTime(
                IosNotificationSchedulingPolicy.OnClassChannelId,
                true,
                logicalClassStart,
                logicalNow,
                systemNow);
        var scheduled = new[]
        {
            CreateRequest(
                "mapped",
                new DateTimeOffset(expectedFireTime),
                IosNotificationSchedulingPolicy.OnClassChannelId)
        };

        Assert.Equal(
            new DateTime(2026, 7, 13, 8, expectedSystemMinute, 0),
            expectedFireTime);
        Assert.True(IosNotificationSchedulingPolicy.CanCompleteQueueTicket(
            IosNotificationSchedulingPolicy.ClassNotificationProviderId,
            IosNotificationSchedulingPolicy.OnClassChannelId,
            expectedFireTime,
            scheduled,
            TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void GetExpectedQueueTicketLocalFireTime_UsesSystemNowForChainHead()
    {
        var systemNow = new DateTimeOffset(
            2026,
            7,
            13,
            8,
            0,
            0,
            TimeSpan.Zero);

        var result = IosNotificationSchedulingPolicy.GetExpectedQueueTicketLocalFireTime(
            IosNotificationSchedulingPolicy.PrepareOnClassChannelId,
            false,
            new DateTime(2026, 7, 13, 8, 10, 0),
            new DateTime(2026, 7, 13, 8, 2, 0),
            systemNow);

        Assert.Equal(systemNow.LocalDateTime, result);
    }

    private static IosLessonNotificationRequest CreateRequest(
        string identifier,
        DateTimeOffset fireAt,
        Guid? channelId = null,
        string? chainId = null) =>
        new(
            identifier,
            fireAt,
            "title",
            "body",
            channelId ?? IosNotificationSchedulingPolicy.PrepareOnClassChannelId,
            true,
            ChainId: chainId);

    private static IosLessonNotificationRequest[] CreateLessonChain(
        DateTimeOffset start,
        string chainId) =>
    [
        CreateRequest(
            $"{chainId}.prepare",
            start.AddMinutes(-5),
            IosNotificationSchedulingPolicy.PrepareOnClassChannelId,
            chainId),
        CreateRequest(
            $"{chainId}.on",
            start,
            IosNotificationSchedulingPolicy.OnClassChannelId,
            chainId)
    ];
}
