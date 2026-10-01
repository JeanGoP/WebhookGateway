using Dapper;
using WebhookGateway.Core.Notifications;
using WebhookGateway.Data.Db;

namespace WebhookGateway.Dispatcher.Notifications;

/// <summary>
/// Acceso a datos de alto rendimiento con Dapper para el ciclo de vida de la NotificationOutbox y NotificationLog.
/// </summary>
public sealed class NotificationStore(ISqlConnectionFactory connectionFactory, TimeProvider clock)
{
    private const string EnqueueSql = """
        INSERT INTO dbo.NotificationOutbox
            (CreatedAt, ChannelType, Recipient, IntegrationId, OutboundEndpointId,
             DeliveryId, AlertType, Subject, MetadataJson, Status, AttemptCount, NextAttemptAt)
        VALUES
            (@CreatedAt, @ChannelType, @Recipient, @IntegrationId, @OutboundEndpointId,
             @DeliveryId, @AlertType, @Subject, @MetadataJson, 0, 0, @NextAttemptAt);
        """;

    private const string ClaimSql = """
        UPDATE o
        SET Status = 1,
            LeaseUntil = @LeaseUntil,
            WorkerId = @WorkerId
        OUTPUT INSERTED.Id, INSERTED.ChannelType, INSERTED.Recipient,
               INSERTED.IntegrationId, INSERTED.OutboundEndpointId, INSERTED.DeliveryId,
               INSERTED.AlertType, INSERTED.Subject, INSERTED.MetadataJson, INSERTED.AttemptCount
        FROM dbo.NotificationOutbox AS o
        INNER JOIN (
            SELECT TOP (@BatchSize) Id
            FROM dbo.NotificationOutbox WITH (READPAST, UPDLOCK, ROWLOCK)
            WHERE Status IN (0, 2)
              AND NextAttemptAt <= @Now
            ORDER BY NextAttemptAt, Id
        ) AS Pick ON Pick.Id = o.Id;
        """;

    private const string MarkDeliveredSql = """
        BEGIN TRANSACTION;

        INSERT INTO dbo.NotificationLog
            (SentAt, ChannelType, Recipient, IntegrationId, OutboundEndpointId,
             DeliveryId, AlertType, Subject, SmtpMessageId, Status, ErrorMessage)
        SELECT
            @SentAt, ChannelType, Recipient, IntegrationId, OutboundEndpointId,
            DeliveryId, AlertType, Subject, @SmtpMessageId, 0, NULL
        FROM dbo.NotificationOutbox
        WHERE Id = @Id;

        DELETE FROM dbo.NotificationOutbox
        WHERE Id = @Id;

        COMMIT TRANSACTION;
        """;

    private const string MarkRetrySql = """
        UPDATE dbo.NotificationOutbox
        SET Status = 2,
            AttemptCount = AttemptCount + 1,
            NextAttemptAt = @NextAttemptAt,
            LeaseUntil = NULL,
            WorkerId = NULL,
            LastError = @LastError
        WHERE Id = @Id;
        """;

    private const string MarkDeadLetterSql = """
        BEGIN TRANSACTION;

        INSERT INTO dbo.NotificationLog
            (SentAt, ChannelType, Recipient, IntegrationId, OutboundEndpointId,
             DeliveryId, AlertType, Subject, SmtpMessageId, Status, ErrorMessage)
        SELECT
            @SentAt, ChannelType, Recipient, IntegrationId, OutboundEndpointId,
            DeliveryId, AlertType, Subject, NULL, 2, @LastError
        FROM dbo.NotificationOutbox
        WHERE Id = @Id;

        DELETE FROM dbo.NotificationOutbox
        WHERE Id = @Id;

        COMMIT TRANSACTION;
        """;

    private const string RecoverLeasesSql = """
        UPDATE dbo.NotificationOutbox
        SET Status = 2, LeaseUntil = NULL, WorkerId = NULL
        WHERE Status = 1 AND LeaseUntil < @Now;
        """;

    private const string GetActiveSubscribersSql = """
        SELECT Email
        FROM dbo.IntegrationEmailSubscriber
        WHERE IntegrationId = @IntegrationId AND Status = 1;
        """;

    public async Task EnqueueAsync(
        NotificationChannel channel,
        string recipient,
        int? integrationId,
        int? outboundEndpointId,
        long? deliveryId,
        NotificationAlertType alertType,
        string subject,
        string metadataJson,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        using var connection = await connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            EnqueueSql,
            new
            {
                CreatedAt = now,
                ChannelType = (byte)channel,
                Recipient = recipient,
                IntegrationId = integrationId,
                OutboundEndpointId = outboundEndpointId,
                DeliveryId = deliveryId,
                AlertType = (byte)alertType,
                Subject = subject,
                MetadataJson = metadataJson,
                NextAttemptAt = now
            },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<ClaimedNotification>> ClaimAsync(
        DateTime now, DateTime leaseUntil, string workerId, int batchSize, CancellationToken cancellationToken)
    {
        using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<ClaimedNotification>(new CommandDefinition(
            ClaimSql,
            new { Now = now, LeaseUntil = leaseUntil, WorkerId = workerId, BatchSize = batchSize },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task MarkDeliveredAsync(long id, string? smtpMessageId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        using var connection = await connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            MarkDeliveredSql,
            new { Id = id, SentAt = now, SmtpMessageId = smtpMessageId },
            cancellationToken: cancellationToken));
    }

    public async Task MarkFailedAsync(
        long id, string error, bool isRetryable, DateTime nextAttemptAt, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var sql = isRetryable ? MarkRetrySql : MarkDeadLetterSql;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { Id = id, LastError = error, NextAttemptAt = nextAttemptAt, SentAt = now },
            cancellationToken: cancellationToken));
    }

    public async Task<int> RecoverOrphanedLeasesAsync(DateTime now, CancellationToken cancellationToken)
    {
        using var connection = await connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteAsync(new CommandDefinition(
            RecoverLeasesSql,
            new { Now = now },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<string>> GetVerifiedSubscriberEmailsAsync(int integrationId, CancellationToken cancellationToken)
    {
        using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var emails = await connection.QueryAsync<string>(new CommandDefinition(
            GetActiveSubscribersSql,
            new { IntegrationId = integrationId },
            cancellationToken: cancellationToken));

        return [.. emails];
    }
}
