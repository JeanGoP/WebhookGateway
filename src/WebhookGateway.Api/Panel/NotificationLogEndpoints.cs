using Dapper;
using WebhookGateway.Core.Notifications;
using WebhookGateway.Data.Db;

namespace WebhookGateway.Api.Panel;

public static class NotificationLogEndpoints
{
    private const string SelectGlobalSql = """
        SELECT TOP (50)
            Id, SentAt, ChannelType, Recipient, IntegrationId, OutboundEndpointId,
            DeliveryId, AlertType, Subject, SmtpMessageId, Status, ErrorMessage
        FROM dbo.NotificationLog
        ORDER BY SentAt DESC;
        """;

    private const string SelectByIntegrationSql = """
        SELECT TOP (50)
            Id, SentAt, ChannelType, Recipient, IntegrationId, OutboundEndpointId,
            DeliveryId, AlertType, Subject, SmtpMessageId, Status, ErrorMessage
        FROM dbo.NotificationLog
        WHERE IntegrationId = @IntegrationId
        ORDER BY SentAt DESC;
        """;

    public static void MapNotificationLogs(this WebApplication app)
    {
        var group = app.MapGroup("/api/notifications")
            .WithTags("Notifications")
            .RequireAuthorization();

        group.MapGet("/", GetGlobalAsync);
        app.MapGet("/api/integrations/{integrationId:int}/notifications", GetByIntegrationAsync)
            .WithTags("Notifications")
            .RequireAuthorization();
    }

    private static async Task<IResult> GetGlobalAsync(
        ISqlConnectionFactory connectionFactory,
        CancellationToken ct)
    {
        using var connection = await connectionFactory.OpenAsync(ct);
        var rows = await connection.QueryAsync<NotificationLogRow>(
            new CommandDefinition(SelectGlobalSql, cancellationToken: ct));

        return Results.Ok(rows.Select(r => r.ToDto()));
    }

    private static async Task<IResult> GetByIntegrationAsync(
        int integrationId,
        ISqlConnectionFactory connectionFactory,
        CancellationToken ct)
    {
        using var connection = await connectionFactory.OpenAsync(ct);
        var rows = await connection.QueryAsync<NotificationLogRow>(
            new CommandDefinition(SelectByIntegrationSql, new { IntegrationId = integrationId }, cancellationToken: ct));

        return Results.Ok(rows.Select(r => r.ToDto()));
    }

    private sealed record NotificationLogRow(
        long Id,
        DateTime SentAt,
        byte ChannelType,
        string Recipient,
        int? IntegrationId,
        int? OutboundEndpointId,
        long? DeliveryId,
        byte AlertType,
        string Subject,
        string? SmtpMessageId,
        byte Status,
        string? ErrorMessage)
    {
        public NotificationLogDto ToDto() => new(
            Id,
            SentAt,
            ((NotificationChannel)ChannelType).ToString(),
            Recipient,
            IntegrationId,
            OutboundEndpointId,
            DeliveryId,
            ((NotificationAlertType)AlertType).ToString(),
            Subject,
            SmtpMessageId,
            ((NotificationLogStatus)Status).ToString(),
            ErrorMessage);
    }
}

public sealed record NotificationLogDto(
    long Id,
    DateTime SentAt,
    string ChannelType,
    string Recipient,
    int? IntegrationId,
    int? OutboundEndpointId,
    long? DeliveryId,
    string AlertType,
    string Subject,
    string? SmtpMessageId,
    string Status,
    string? ErrorMessage);
