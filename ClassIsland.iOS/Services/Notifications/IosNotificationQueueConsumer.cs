using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Enums.Notification;
using ClassIsland.Core.Models.Notification;

namespace ClassIsland.iOS.Services.Notifications;

/// <summary>
/// iOS 使用系统本地通知显示课程提醒。只有与最近一次成功同步的系统通知
/// 对应的课程票据才会完成；其余票据退回提醒主机，交给其它消费者处理。
/// </summary>
internal sealed class IosNotificationQueueConsumer : INotificationConsumer
{
    private static readonly TimeSpan ScheduleMatchTolerance = TimeSpan.FromMinutes(1);
    private IosLessonNotificationRequest[] _scheduledRequests = [];
    private int _isHandingOff;

    public int QueuedNotificationCount => Volatile.Read(ref _isHandingOff);

    public bool AcceptsNotificationRequests =>
        Volatile.Read(ref _isHandingOff) == 0 &&
        Volatile.Read(ref _scheduledRequests).Length > 0;

    internal void SetScheduledRequests(
        IReadOnlyCollection<IosLessonNotificationRequest> requests) =>
        Volatile.Write(ref _scheduledRequests, requests.ToArray());

    internal void ClearScheduledRequests() =>
        Volatile.Write(ref _scheduledRequests, []);

    public void ReceiveNotifications(
        IReadOnlyList<NotificationPlayingTicket> notificationRequests)
    {
        var scheduledRequests = Volatile.Read(ref _scheduledRequests);
        var now = DateTime.Now;
        var ticketsToComplete = new List<NotificationPlayingTicket>();
        var ticketsToHandOff = new List<NotificationPlayingTicket>();
        foreach (var chain in notificationRequests.GroupBy(
                     x => x.Request.ChainedHeadRequest ?? x.Request,
                     ReferenceEqualityComparer.Instance))
        {
            var chainTickets = chain.ToArray();
            if (chainTickets.All(ticket => IsCoveredBySystemSchedule(
                    ticket.Request,
                    scheduledRequests,
                    now)))
            {
                ticketsToComplete.AddRange(chainTickets);
            }
            else
            {
                // 取消一张票据即可让主机按 ChainedHeadRequest 重建整条提醒链；
                // 逐张取消会在每次同步重新入队时制造重复票据。
                ticketsToHandOff.Add(
                    chainTickets.FirstOrDefault(x => ReferenceEquals(x.Request, chain.Key)) ??
                    chainTickets[0]);
            }
        }

        if (ticketsToHandOff.Count > 0)
        {
            // ticket.Cancel() 会同步触发主机重新入队。先暂停接收，确保退回的
            // 天气、放学、自动化、集控及未被系统排程的课程提醒不会再次自旋到这里。
            Interlocked.Exchange(ref _isHandingOff, 1);
        }

        try
        {
            foreach (var ticket in ticketsToComplete)
            {
                CompleteTicket(ticket);
            }

            foreach (var ticket in ticketsToHandOff)
            {
                try
                {
                    ticket.Cancel();
                }
                catch (AggregateException exception)
                {
                    // CancellationToken 回调由提醒提供方和主机拥有；继续退回其余票据。
                    Console.Error.WriteLine($"退回 iOS 未处理提醒票据时回调失败：{exception}");
                }
            }
        }
        finally
        {
            if (ticketsToHandOff.Count > 0)
            {
                Interlocked.Exchange(ref _isHandingOff, 0);
            }
        }
    }

    private static bool IsCoveredBySystemSchedule(
        NotificationRequest request,
        IReadOnlyCollection<IosLessonNotificationRequest> scheduledRequests,
        DateTime now)
    {
        var expectedFireTime = GetExpectedLocalFireTime(request, now);
        return IosNotificationSchedulingPolicy.CanCompleteQueueTicket(
            request.NotificationSourceGuid,
            request.ChannelId,
            expectedFireTime,
            scheduledRequests,
            ScheduleMatchTolerance);
    }

    private static DateTime GetExpectedLocalFireTime(
        NotificationRequest request,
        DateTime now)
    {
        if (request.ChannelId == IosNotificationSchedulingPolicy.OnClassChannelId &&
            request.ChainedHeadRequest is { } chainedHead &&
            !ReferenceEquals(chainedHead, request) &&
            chainedHead.OverlayContent?.EndTime is { } chainedEndTime)
        {
            return chainedEndTime;
        }

        return now;
    }

    private static void CompleteTicket(NotificationPlayingTicket ticket)
    {
        ticket.Request.State = NotificationState.Completed;
        try
        {
            ticket.Request.CompletedTokenSource.Cancel();
        }
        catch (AggregateException exception)
        {
            // CancellationToken 回调由提醒提供方拥有；单个回调异常不能阻止
            // 其余已由 iOS 系统接管的票据完成。
            Console.Error.WriteLine($"完成 iOS 课程提醒票据时回调失败：{exception}");
        }
    }
}
