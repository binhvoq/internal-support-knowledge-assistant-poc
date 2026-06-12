using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;

namespace SupportPoc.Shared.Messaging;

public static class MessagingOutboxDiagnostics
{
    public const string ConsumerOutboxNote =
        "MassTransit EF consumer outbox enabled with InboxState duplicate detection (MessageId).";

    public static async Task<object> BuildSnapshotAsync(
        DbContext db,
        TimeSpan duplicateDetectionWindow,
        int recentLimit = 50,
        CancellationToken cancellationToken = default)
    {
        var pendingQuery = db.Set<OutboxMessage>().Where(x => x.SentTime == default);
        var pendingCount = await pendingQuery.CountAsync(cancellationToken);
        var oldestPendingEnqueueTime = await pendingQuery
            .OrderBy(x => x.EnqueueTime)
            .Select(x => (DateTime?)x.EnqueueTime)
            .FirstOrDefaultAsync(cancellationToken);

        var recentOutbox = await db.Set<OutboxMessage>()
            .OrderByDescending(x => x.SequenceNumber)
            .Take(recentLimit)
            .Select(x => new
            {
                x.SequenceNumber,
                x.MessageId,
                x.EnqueueTime,
                x.SentTime,
                pending = x.SentTime == default,
                x.ContentType,
                x.MessageType
            })
            .ToListAsync(cancellationToken);

        var recentInbox = await db.Set<InboxState>()
            .OrderByDescending(x => x.Received)
            .Take(recentLimit)
            .Select(x => new
            {
                x.MessageId,
                x.ConsumerId,
                x.Received,
                x.Consumed,
                x.ReceiveCount,
                x.Delivered,
                x.LastSequenceNumber,
                x.ExpirationTime
            })
            .ToListAsync(cancellationToken);

        return new
        {
            consumerOutbox = ConsumerOutboxNote,
            duplicateDetectionWindow = duplicateDetectionWindow.ToString(),
            outbox = new
            {
                pendingCount,
                oldestPendingEnqueueTime,
                recent = recentOutbox
            },
            inbox = new
            {
                recentCount = recentInbox.Count,
                recent = recentInbox
            }
        };
    }
}
