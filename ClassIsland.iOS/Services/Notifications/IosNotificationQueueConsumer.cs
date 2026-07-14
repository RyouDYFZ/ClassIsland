using System.Diagnostics;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Controls.NotificationTemplates;
using ClassIsland.Core.Enums.Notification;
using ClassIsland.Core.Models.Notification;
using ClassIsland.Core.Models.Notification.Templates;
using ClassIsland.iOS.Services.Platform;
using ClassIsland.Platforms.Abstraction.Services;
using ClassIsland.Services;
using UserNotifications;

namespace ClassIsland.iOS.Services.Notifications;

/// <summary>
/// 完成已由课程排程接管的票据，并将其余提醒链折叠为即时 iOS 本地通知。
/// </summary>
internal sealed class IosNotificationQueueConsumer : INotificationConsumer
{
    private const string FallbackIdentifierPrefix = "classisland.fallback.";
    private const string FallbackCategoryIdentifier = "classisland.fallback";
    private static readonly TimeSpan ScheduleMatchTolerance = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FallbackCapacityRetryInterval =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumFallbackCapacityWait =
        TimeSpan.FromSeconds(5);

    private readonly IosNotificationAuthorizationService _authorizationService;
    private readonly INotificationHostService _notificationHostService;
    private readonly SettingsService _settingsService;
    private readonly IExactTimeService _exactTimeService;
    private readonly IosNotificationMutationGate _notificationMutationGate;
    private readonly CancellationTokenSource _stopCancellation = new();
    private readonly object _processingLock = new();
    private readonly Queue<IReadOnlyList<NotificationPlayingTicket>> _pendingBatches = [];
    private TaskCompletionSource _scheduleSynchronizationCompletion =
        CreatePendingCompletionSource();
    private IosLessonNotificationRequest[] _scheduledRequests = [];
    private bool _isProcessing;
    private int _queuedNotificationCount;
    private int _isScheduleSynchronizationPending = 1;
    private int _isStopped;

    internal IosNotificationQueueConsumer(
        IosNotificationAuthorizationService authorizationService,
        INotificationHostService notificationHostService,
        SettingsService settingsService,
        IExactTimeService exactTimeService,
        IosNotificationMutationGate notificationMutationGate)
    {
        _authorizationService = authorizationService;
        _notificationHostService = notificationHostService;
        _settingsService = settingsService;
        _exactTimeService = exactTimeService;
        _notificationMutationGate = notificationMutationGate;
    }

    public int QueuedNotificationCount =>
        Volatile.Read(ref _queuedNotificationCount);

    public bool AcceptsNotificationRequests =>
        Volatile.Read(ref _isStopped) == 0 &&
        Volatile.Read(ref _isScheduleSynchronizationPending) == 0 &&
        Volatile.Read(ref _queuedNotificationCount) == 0;

    internal void SetScheduledRequests(
        IReadOnlyCollection<IosLessonNotificationRequest> requests) =>
        Volatile.Write(ref _scheduledRequests, requests.ToArray());

    internal void ClearScheduledRequests() =>
        Volatile.Write(ref _scheduledRequests, []);

    internal void BeginScheduleSynchronization()
    {
        lock (_processingLock)
        {
            if (Volatile.Read(ref _isStopped) != 0 ||
                Interlocked.Exchange(ref _isScheduleSynchronizationPending, 1) != 0)
            {
                return;
            }

            _scheduleSynchronizationCompletion = CreatePendingCompletionSource();
        }
    }

    internal void EndScheduleSynchronization()
    {
        TaskCompletionSource completion;
        lock (_processingLock)
        {
            if (Volatile.Read(ref _isStopped) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _isScheduleSynchronizationPending, 0);
            completion = _scheduleSynchronizationCompletion;
        }

        completion.TrySetResult();
        if (Volatile.Read(ref _queuedNotificationCount) == 0)
        {
            _ = PullNextBatchAsync();
        }
    }

    internal void Stop()
    {
        List<NotificationPlayingTicket> drainedTickets = [];
        TaskCompletionSource synchronizationCompletion;
        lock (_processingLock)
        {
            if (Volatile.Read(ref _isStopped) != 0)
            {
                return;
            }

            Volatile.Write(ref _isStopped, 1);
            Volatile.Write(ref _isScheduleSynchronizationPending, 1);
            ClearScheduledRequests();
            while (_pendingBatches.TryDequeue(out var batch))
            {
                drainedTickets.AddRange(batch);
            }

            if (drainedTickets.Count > 0)
            {
                Interlocked.Add(ref _queuedNotificationCount, -drainedTickets.Count);
            }

            synchronizationCompletion = _scheduleSynchronizationCompletion;
        }

        _stopCancellation.Cancel();
        synchronizationCompletion.TrySetResult();
        if (drainedTickets.Count > 0)
        {
            _ = CompleteTicketsOnUiThreadAsync(drainedTickets, "消费者停止");
        }
    }

    public void ReceiveNotifications(
        IReadOnlyList<NotificationPlayingTicket> notificationRequests)
    {
        if (notificationRequests.Count == 0)
        {
            return;
        }

        var batch = notificationRequests.ToArray();
        var shouldStartProcessing = false;
        lock (_processingLock)
        {
            if (Volatile.Read(ref _isStopped) == 0)
            {
                _pendingBatches.Enqueue(batch);
                Interlocked.Add(ref _queuedNotificationCount, batch.Length);
                if (!_isProcessing)
                {
                    _isProcessing = true;
                    shouldStartProcessing = true;
                }
            }
        }

        if (Volatile.Read(ref _isStopped) != 0 && !shouldStartProcessing)
        {
            _ = CompleteTicketsOnUiThreadAsync(batch, "消费者已停止");
            return;
        }

        if (shouldStartProcessing)
        {
            _ = ProcessQueueAsync();
        }
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            IReadOnlyList<NotificationPlayingTicket> tickets;
            lock (_processingLock)
            {
                if (!_pendingBatches.TryDequeue(out tickets!))
                {
                    _isProcessing = false;
                    return;
                }
            }

            try
            {
                await ProcessBatchAsync(tickets).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (_stopCancellation.IsCancellationRequested)
            {
                await CompleteTicketsOnUiThreadAsync(tickets, "消费者停止")
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"处理 iOS 提醒票据时发生未预期异常：{exception}");
                await CompleteTicketsOnUiThreadAsync(tickets, "异常终结")
                    .ConfigureAwait(false);
            }
            finally
            {
                var remaining = Interlocked.Add(
                    ref _queuedNotificationCount,
                    -tickets.Count);
                if (remaining == 0 && Volatile.Read(ref _isStopped) == 0)
                {
                    await PullNextBatchAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ProcessBatchAsync(
        IReadOnlyList<NotificationPlayingTicket> notificationRequests)
    {
        var chains = await GetChainsOnUiThreadAsync(notificationRequests)
            .ConfigureAwait(false);
        bool? authorized = null;

        foreach (var chainTickets in chains)
        {
            while (true)
            {
                var preparation = await PrepareChainAfterSynchronizationAsync(chainTickets)
                    .ConfigureAwait(false);
                if (preparation.IsTerminal)
                {
                    break;
                }

                if (authorized is null)
                {
                    authorized = await RequestAuthorizationAsync().ConfigureAwait(false);
                }

                preparation = await PrepareChainAfterSynchronizationAsync(chainTickets)
                    .ConfigureAwait(false);
                if (preparation.IsTerminal)
                {
                    break;
                }

                string? fallbackIdentifier = null;
                if (authorized == true)
                {
                    try
                    {
                        _stopCancellation.Token.ThrowIfCancellationRequested();
                        fallbackIdentifier = await SubmitFallbackNotificationAsync(
                                preparation.Payload!,
                                preparation.PlaySound,
                                _stopCancellation.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                        when (exception is not OperationCanceledException ||
                              !_stopCancellation.IsCancellationRequested)
                    {
                        Console.Error.WriteLine(
                            $"提交 iOS 即时本地通知失败，票据将被终结：{exception}");
                    }
                }

                var finalization = await FinalizeChainOnUiThreadAsync(chainTickets)
                    .ConfigureAwait(false);
                if (finalization.RemoveFallback && fallbackIdentifier != null)
                {
                    await RemoveFallbackNotificationAsync(fallbackIdentifier)
                        .ConfigureAwait(false);
                }

                if (!finalization.ShouldRetry)
                {
                    break;
                }
            }
        }
    }

    private async Task<ChainPreparation> PrepareChainAfterSynchronizationAsync(
        IReadOnlyList<NotificationPlayingTicket> tickets)
    {
        while (true)
        {
            await WaitForScheduleSynchronizationAsync().ConfigureAwait(false);
            var preparation = await PrepareChainOnUiThreadAsync(tickets)
                .ConfigureAwait(false);
            if (!preparation.ShouldRetry)
            {
                return preparation;
            }
        }
    }

    private async Task WaitForScheduleSynchronizationAsync()
    {
        while (Volatile.Read(ref _isScheduleSynchronizationPending) != 0)
        {
            Task synchronizationTask;
            lock (_processingLock)
            {
                if (Volatile.Read(ref _isScheduleSynchronizationPending) == 0)
                {
                    return;
                }

                synchronizationTask = _scheduleSynchronizationCompletion.Task;
            }

            await synchronizationTask
                .WaitAsync(_stopCancellation.Token)
                .ConfigureAwait(false);
        }

        _stopCancellation.Token.ThrowIfCancellationRequested();
    }

    private async Task<bool> RequestAuthorizationAsync()
    {
        try
        {
            var authorized = await _authorizationService
                .RequestAuthorizationIfNeededAsync()
                .WaitAsync(_stopCancellation.Token)
                .ConfigureAwait(false);
            if (!authorized)
            {
                Console.Error.WriteLine(
                    "iOS/iPadOS 通知权限未授予，当前提醒票据将被终结。");
            }

            return authorized;
        }
        catch (OperationCanceledException)
            when (_stopCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"请求 iOS/iPadOS 通知权限失败，当前提醒票据将被终结：{exception}");
            return false;
        }
    }

    private async Task<string> SubmitFallbackNotificationAsync(
        IosFallbackNotificationPayload payload,
        bool playSound,
        CancellationToken cancellationToken)
    {
        var identifier = $"{FallbackIdentifierPrefix}{Guid.NewGuid():N}";
        var capacityWaitStarted = Stopwatch.GetTimestamp();
        while (true)
        {
            var submitted = await _notificationMutationGate.ExecuteAsync(
                    () => TrySubmitFallbackNotificationAsync(
                        identifier,
                        payload,
                        playSound,
                        Stopwatch.GetElapsedTime(capacityWaitStarted),
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            if (submitted)
            {
                return identifier;
            }

            await Task.Delay(FallbackCapacityRetryInterval, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<bool> TrySubmitFallbackNotificationAsync(
        string identifier,
        IosFallbackNotificationPayload payload,
        bool playSound,
        TimeSpan capacityWaitElapsed,
        CancellationToken cancellationToken)
    {
        var notificationCenter = UNUserNotificationCenter.Current;
        var pending = await notificationCenter.GetPendingNotificationRequestsAsync() ?? [];
        cancellationToken.ThrowIfCancellationRequested();
        var capacityDecision = IosNotificationCapacityPolicy
            .GetFallbackSubmissionDecision(
                identifier,
                pending.Select(x => x.Identifier),
                capacityWaitElapsed,
                MaximumFallbackCapacityWait);
        if (capacityDecision == IosFallbackNotificationCapacityDecision.Retry)
        {
            return false;
        }
        if (capacityDecision ==
            IosFallbackNotificationCapacityDecision.CapacityExhausted)
        {
            throw new TimeoutException(
                $"iOS 的 {IosNotificationCapacityPolicy.MaximumPendingNotificationCount} " +
                $"个待处理本地通知槽在 {MaximumFallbackCapacityWait.TotalSeconds:0} 秒内均未释放。");
        }

        using var content = new UNMutableNotificationContent
        {
            Title = payload.Title,
            Body = payload.Body,
            CategoryIdentifier = FallbackCategoryIdentifier,
            ThreadIdentifier = FallbackCategoryIdentifier
        };
        if (playSound)
        {
            content.Sound = UNNotificationSound.Default;
        }

        using var trigger = UNTimeIntervalNotificationTrigger.CreateTrigger(1, false);
        using var request = UNNotificationRequest.FromIdentifier(
            identifier,
            content,
            trigger);
        await notificationCenter.AddNotificationRequestAsync(request)
            .ConfigureAwait(false);

        var confirmedPending = await notificationCenter
            .GetPendingNotificationRequestsAsync() ?? [];
        if (confirmedPending.Any(x => x.Identifier == identifier))
        {
            return true;
        }

        var delivered = await notificationCenter.GetDeliveredNotificationsAsync() ?? [];
        if (delivered.Any(x => x.Request.Identifier == identifier))
        {
            return true;
        }

        throw new InvalidOperationException(
            $"iOS 未保留或送达即时本地通知 {identifier}。");
    }

    private Task RemoveFallbackNotificationAsync(string identifier)
    {
        return _notificationMutationGate.ExecuteAsync(
            () =>
            {
                var notificationCenter = UNUserNotificationCenter.Current;
                notificationCenter.RemovePendingNotificationRequests([identifier]);
                notificationCenter.RemoveDeliveredNotifications([identifier]);
                return Task.CompletedTask;
            },
            CancellationToken.None);
    }

    private async Task<ChainPreparation> PrepareChainOnUiThreadAsync(
        IReadOnlyList<NotificationPlayingTicket> tickets)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(() => PrepareChain(tickets));
        }

        return PrepareChain(tickets);
    }

    private ChainPreparation PrepareChain(
        IReadOnlyList<NotificationPlayingTicket> tickets)
    {
        if (Volatile.Read(ref _isScheduleSynchronizationPending) != 0)
        {
            return ChainPreparation.Retry;
        }

        if (tickets.Any(x => !IsTicketActive(x)))
        {
            CompleteTickets(tickets, "提醒链已取消");
            return ChainPreparation.Terminal;
        }

        var scheduledRequests = Volatile.Read(ref _scheduledRequests);
        var logicalNow = _exactTimeService.GetCurrentLocalDateTime();
        var systemNow = DateTimeOffset.Now;
        var coveredBySystemSchedule = tickets.All(ticket =>
            IsCoveredBySystemSchedule(
                ticket.Request,
                scheduledRequests,
                logicalNow,
                systemNow));
        if (coveredBySystemSchedule)
        {
            CompleteTickets(tickets, "课程排程接管");
            return ChainPreparation.Terminal;
        }

        var payload = CreatePayload(tickets);
        var playSound = IosFallbackNotificationPayloadPolicy.ShouldPlaySound(
            _settingsService.Settings.AllowNotificationSound,
            tickets.Select(x => x.Settings.IsNotificationSoundEnabled));
        return new ChainPreparation(false, false, payload, playSound);
    }

    private static async Task<NotificationPlayingTicket[][]> GetChainsOnUiThreadAsync(
        IReadOnlyList<NotificationPlayingTicket> tickets)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(() => GetChains(tickets));
        }

        return GetChains(tickets);
    }

    private static NotificationPlayingTicket[][] GetChains(
        IReadOnlyList<NotificationPlayingTicket> tickets) =>
        tickets.GroupBy(
                x => x.Request.ChainedHeadRequest ?? x.Request,
                ReferenceEqualityComparer.Instance)
            .Select(x => x.ToArray())
            .ToArray();

    private static IosFallbackNotificationPayload CreatePayload(
        IReadOnlyList<NotificationPlayingTicket> tickets)
    {
        var providerName = tickets
            .Select(x => x.Request.NotificationSource?.Name)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        return IosFallbackNotificationPayloadPolicy.Create(
            providerName,
            tickets.Select(x => new IosFallbackNotificationTextEntry(
                GetContentText(x.Request.MaskContent),
                GetContentText(x.Request.OverlayContent))));
    }

    private static string? GetContentText(NotificationContent? content)
    {
        if (content == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(content.SpeechContent))
        {
            return content.SpeechContent;
        }

        return content.Content switch
        {
            TwoIconsMaskTemplateData data => data.Text,
            SimpleTextTemplateData data => data.Text,
            RollingTextTemplate { DataContext: RollingTextTemplateData data } => data.Text,
            RollingTextTemplateData data => data.Text,
            _ => null
        };
    }

    private async Task PullNextBatchAsync()
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _isStopped) != 0 ||
                    Volatile.Read(ref _isScheduleSynchronizationPending) != 0 ||
                    Volatile.Read(ref _queuedNotificationCount) != 0)
                {
                    return;
                }

                var nextBatch = _notificationHostService.PullNotificationRequests();
                if (nextBatch.Count > 0)
                {
                    ReceiveNotifications(nextBatch.ToArray());
                }
            });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"iOS 拉取后续提醒票据失败：{exception}");
        }
    }

    private static bool IsCoveredBySystemSchedule(
        NotificationRequest request,
        IReadOnlyCollection<IosLessonNotificationRequest> scheduledRequests,
        DateTime logicalNow,
        DateTimeOffset systemNow)
    {
        var chainedHead = request.ChainedHeadRequest;
        var expectedFireTime = IosNotificationSchedulingPolicy
            .GetExpectedQueueTicketLocalFireTime(
                request.ChannelId,
                chainedHead != null && !ReferenceEquals(chainedHead, request),
                chainedHead?.OverlayContent?.EndTime,
                logicalNow,
                systemNow);
        return IosNotificationSchedulingPolicy.CanCompleteQueueTicket(
            request.NotificationSourceGuid,
            request.ChannelId,
            expectedFireTime,
            scheduledRequests,
            ScheduleMatchTolerance);
    }

    private static bool IsTicketActive(NotificationPlayingTicket ticket) =>
        !ticket.CancellationToken.IsCancellationRequested &&
        !ticket.Request.CancellationToken.IsCancellationRequested &&
        !ticket.Request.CompletedToken.IsCancellationRequested;

    private async Task<ChainFinalization> FinalizeChainOnUiThreadAsync(
        IReadOnlyList<NotificationPlayingTicket> tickets)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(() => FinalizeChain(tickets));
        }

        return FinalizeChain(tickets);
    }

    private ChainFinalization FinalizeChain(
        IReadOnlyList<NotificationPlayingTicket> tickets)
    {
        lock (_processingLock)
        {
            if (Volatile.Read(ref _isStopped) != 0 ||
                tickets.Any(x => !IsTicketActive(x)))
            {
                CompleteTickets(tickets, "提醒链已取消或消费者停止");
                return ChainFinalization.RemoveAndFinish;
            }

            if (Volatile.Read(ref _isScheduleSynchronizationPending) != 0)
            {
                return ChainFinalization.RemoveAndRetry;
            }

            CompleteTickets(tickets, "即时本地通知处理");
            return ChainFinalization.KeepAndFinish;
        }
    }

    private static async Task CompleteTicketsOnUiThreadAsync(
        IEnumerable<NotificationPlayingTicket> tickets,
        string reason)
    {
        var ticketArray = tickets as NotificationPlayingTicket[] ?? tickets.ToArray();
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => CompleteTickets(ticketArray, reason));
            return;
        }

        CompleteTickets(ticketArray, reason);
    }

    private static void CompleteTickets(
        IEnumerable<NotificationPlayingTicket> tickets,
        string reason)
    {
        foreach (var ticket in tickets)
        {
            CompleteTicket(ticket, reason);
        }
    }

    private static void CompleteTicket(
        NotificationPlayingTicket ticket,
        string reason)
    {
        if (!IsTicketActive(ticket))
        {
            return;
        }

        ticket.Request.State = NotificationState.Completed;
        try
        {
            ticket.Request.CompletedTokenSource.Cancel();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"完成 iOS 提醒票据失败（{reason}）：{exception}");
        }
    }

    private static TaskCompletionSource CreatePendingCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record ChainPreparation(
        bool ShouldRetry,
        bool IsTerminal,
        IosFallbackNotificationPayload? Payload,
        bool PlaySound)
    {
        public static ChainPreparation Retry { get; } = new(true, false, null, false);

        public static ChainPreparation Terminal { get; } = new(false, true, null, false);
    }

    private sealed record ChainFinalization(bool RemoveFallback, bool ShouldRetry)
    {
        public static ChainFinalization KeepAndFinish { get; } = new(false, false);

        public static ChainFinalization RemoveAndFinish { get; } = new(true, false);

        public static ChainFinalization RemoveAndRetry { get; } = new(true, true);
    }
}
