using Dapper;
using WebhookGateway.Data.Db;

namespace WebhookGateway.Dispatcher.Recording;

/// <summary>Una fila de <c>DeliveryAttempt</c> pendiente de escribir.</summary>
public sealed record AttemptRecord(
    long DeliveryId,
    DateTime StartedAt,
    short AttemptNumber,
    int DurationMs,
    short? StatusCode,
    string? ResponseHeadersJson,
    string? ResponseBody,
    string? ErrorMessage,
    string WorkerId);

/// <summary>El nuevo estado de una entrega tras un intento.</summary>
public sealed record DeliveryUpdate(
    long Id,
    DateTime CreatedAt,
    byte Status,
    short AttemptCount,
    DateTime NextAttemptAt,
    short? LastStatusCode,
    string? LastError,
    DateTime? CompletedAt);

/// <summary>
/// Acumula resultados y los escribe en bloque.
/// </summary>
/// <remarks>
/// <c>DeliveryAttempt</c> es la tabla con más volumen del sistema y el servidor SQL es
/// compartido, así que escribir intento a intento sería la forma más fácil de que el DBA
/// notara que existimos. Acumular un ciclo de despacho y volcarlo de una vez convierte cien
/// escrituras sueltas en un solo viaje.
/// </remarks>
public sealed class DeliveryRecorder(ISqlConnectionFactory connectionFactory)
{
    private const int MaxLastErrorLength = 1000;

    private const string InsertAttemptSql = """
        INSERT INTO dbo.DeliveryAttempt
            (StartedAt, DeliveryId, AttemptNumber, DurationMs, StatusCode,
             ResponseHeadersJson, ResponseBody, ErrorMessage, WorkerId)
        VALUES (@StartedAt, @DeliveryId, @AttemptNumber, @DurationMs, @StatusCode,
                @ResponseHeadersJson, @ResponseBody, @ErrorMessage, @WorkerId);
        """;

    private const string UpdateDeliverySql = """
        UPDATE dbo.WebhookDelivery
        SET Status = @Status,
            AttemptCount = @AttemptCount,
            NextAttemptAt = @NextAttemptAt,
            LastStatusCode = @LastStatusCode,
            LastError = @LastError,
            CompletedAt = @CompletedAt,
            LeaseUntil = NULL,
            WorkerId = NULL
        WHERE CreatedAt = @CreatedAt AND Id = @Id;
        """;

    private readonly Lock _gate = new();
    private readonly List<AttemptRecord> _attempts = [];
    private readonly List<DeliveryUpdate> _updates = [];

    public void Add(AttemptRecord? attempt, DeliveryUpdate update)
    {
        lock (_gate)
        {
            if (attempt is not null)
            {
                _attempts.Add(attempt);
            }

            _updates.Add(Truncate(update));
        }
    }

    /// <summary>
    /// Vuelca lo acumulado. Las actualizaciones van primero: si el proceso muere entre las
    /// dos escrituras, se pierde el registro de un intento —que solo sirve para depurar—
    /// pero no el estado de la entrega, que es lo que decide si se reintenta.
    /// </summary>
    public async Task<int> FlushAsync(CancellationToken cancellationToken)
    {
        AttemptRecord[] attempts;
        DeliveryUpdate[] updates;

        lock (_gate)
        {
            if (_attempts.Count == 0 && _updates.Count == 0)
            {
                return 0;
            }

            attempts = [.. _attempts];
            updates = [.. _updates];
            _attempts.Clear();
            _updates.Clear();
        }

        using var connection = await connectionFactory.OpenAsync(cancellationToken);

        if (updates.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(UpdateDeliverySql, updates, cancellationToken: cancellationToken));
        }

        if (attempts.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(InsertAttemptSql, attempts, cancellationToken: cancellationToken));
        }

        return updates.Length;
    }

    private static DeliveryUpdate Truncate(DeliveryUpdate update) =>
        update.LastError is { Length: > MaxLastErrorLength } error
            ? update with { LastError = error[..MaxLastErrorLength] }
            : update;
}
