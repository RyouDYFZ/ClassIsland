using ClassIsland.iOS.Services.Notifications;
using Xunit;

namespace ClassIsland.Platforms.Abstractions.Tests;

public sealed class IosLessonNotificationSchedulingTests
{
    [Fact]
    public void Select_BalancesDenseScheduleAcrossFullSevenDayHorizon()
    {
        var firstDay = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.FromHours(8));
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
        var now = new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.FromHours(8));
        var later = CreateRequest("later", now.AddHours(1));
        var earlier = CreateRequest("earlier", now);

        var result = IosLessonNotificationScheduleSelector.Select([later, earlier], 60);

        Assert.Equal([earlier, later], result);
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
        var fireAt = new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.FromHours(8));
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

    private static IosLessonNotificationRequest CreateRequest(
        string identifier,
        DateTimeOffset fireAt,
        Guid? channelId = null) =>
        new(
            identifier,
            fireAt,
            "title",
            "body",
            channelId ?? IosNotificationSchedulingPolicy.PrepareOnClassChannelId,
            true);
}
