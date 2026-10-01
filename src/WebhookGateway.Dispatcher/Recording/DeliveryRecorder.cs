using System.Data;
using System.Globalization;
using System.Text;
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
public sealed class DeliveryRecorder(ISqlConnectionFactory connectionFactory)
{
    /*
        Estos tres topes son las anchuras de las columnas, no números elegidos: WebhookDelivery.LastError
        y DeliveryAttempt.ErrorMessage son nvarchar(1000), y las dos de respuesta nvarchar(4000). Recortar
        de más pierde información; recortar de menos revienta el insert del lote entero.
    */
    private const int MaxErrorLength = 1000;
    private const int MaxResponseLength = 4000;

    private readonly Lock _gate = new();
    private readonly List<AttemptRecord> _attempts = [];
    private readonly List<DeliveryUpdate> _updates = [];

    public void Add(AttemptRecord? attempt, DeliveryUpdate update)
    {
        lock (_gate)
        {
            if (attempt is not null)
            {
                _attempts.Add(TruncateAttempt(attempt));
            }

            _updates.Add(TruncateUpdate(update));
        }
    }

    /// <summary>
    /// Vuelca lo acumulado en lotes reales de red, todo dentro de una transacción.
    /// </summary>
    /// <remarks>
    /// La transacción es lo que hace que el estado de las entregas y el registro de sus intentos no
    /// puedan quedar descuadrados: antes cada <c>UPDATE</c> se confirmaba por su cuenta, así que una
    /// caída a mitad dejaba unas entregas actualizadas y otras no, con sus intentos sin escribir.
    /// <para>
    /// Si el volcado falla, se propaga la excepción y estos resultados se pierden: las entregas
    /// siguen reclamadas por este worker hasta que vence su lease, y entonces otro las recoge y las
    /// vuelve a enviar. Es a propósito. La alternativa —guardarlas para reintentar el volcado— crece
    /// sin techo si el fallo no es pasajero, y ante una caída de SQL es mejor entregar algo dos veces
    /// que perderlo.
    /// </para>
    /// </remarks>
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
        using var transaction = connection.BeginTransaction();

        if (updates.Length > 0)
        {
            await UpdateDeliveriesBatchAsync(connection, transaction, updates, cancellationToken);
        }

        if (attempts.Length > 0)
        {
            await InsertAttemptsBatchAsync(connection, transaction, attempts, cancellationToken);
        }

        transaction.Commit();

        return updates.Length;
    }

    private static async Task UpdateDeliveriesBatchAsync(
        IDbConnection connection, IDbTransaction transaction, DeliveryUpdate[] updates, CancellationToken ct)
    {
        foreach (var chunk in updates.Chunk(50))
        {
            var sb = new StringBuilder();
            var parameters = new DynamicParameters();

            for (int i = 0; i < chunk.Length; i++)
            {
                var item = chunk[i];
                sb.AppendLine(CultureInfo.InvariantCulture, $"""
                    UPDATE dbo.WebhookDelivery
                    SET Status = @st{i}, AttemptCount = @at{i}, NextAttemptAt = @nx{i},
                        LastStatusCode = @sc{i}, LastError = @er{i}, CompletedAt = @cm{i},
                        LeaseUntil = NULL, WorkerId = NULL
                    WHERE CreatedAt = @cr{i} AND Id = @id{i};
                    """);

                parameters.Add($"st{i}", item.Status);
                parameters.Add($"at{i}", item.AttemptCount);
                parameters.Add($"nx{i}", item.NextAttemptAt);
                parameters.Add($"sc{i}", item.LastStatusCode);
                parameters.Add($"er{i}", item.LastError);
                parameters.Add($"cm{i}", item.CompletedAt);
                parameters.Add($"cr{i}", item.CreatedAt);
                parameters.Add($"id{i}", item.Id);
            }

            await connection.ExecuteAsync(new CommandDefinition(sb.ToString(), parameters, transaction, cancellationToken: ct));
        }
    }

    private static async Task InsertAttemptsBatchAsync(
        IDbConnection connection, IDbTransaction transaction, AttemptRecord[] attempts, CancellationToken ct)
    {
        foreach (var chunk in attempts.Chunk(100))
        {
            var sb = new StringBuilder("INSERT INTO dbo.DeliveryAttempt (StartedAt, DeliveryId, AttemptNumber, DurationMs, StatusCode, ResponseHeadersJson, ResponseBody, ErrorMessage, WorkerId) VALUES ");
            var parameters = new DynamicParameters();

            for (int i = 0; i < chunk.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(CultureInfo.InvariantCulture, $"(@s{i}, @d{i}, @n{i}, @m{i}, @c{i}, @h{i}, @b{i}, @e{i}, @w{i})");

                var item = chunk[i];
                parameters.Add($"s{i}", item.StartedAt);
                parameters.Add($"d{i}", item.DeliveryId);
                parameters.Add($"n{i}", item.AttemptNumber);
                parameters.Add($"m{i}", item.DurationMs);
                parameters.Add($"c{i}", item.StatusCode);
                parameters.Add($"h{i}", item.ResponseHeadersJson);
                parameters.Add($"b{i}", item.ResponseBody);
                parameters.Add($"e{i}", item.ErrorMessage);
                parameters.Add($"w{i}", item.WorkerId);
            }

            sb.Append(';');
            await connection.ExecuteAsync(new CommandDefinition(sb.ToString(), parameters, transaction, cancellationToken: ct));
        }
    }

    private static DeliveryUpdate TruncateUpdate(DeliveryUpdate update) =>
        update.LastError is { Length: > MaxErrorLength } error
            ? update with { LastError = error[..MaxErrorLength] }
            : update;

    private static AttemptRecord TruncateAttempt(AttemptRecord attempt)
    {
        var headers = attempt.ResponseHeadersJson is { Length: > MaxResponseLength } h ? h[..MaxResponseLength] : attempt.ResponseHeadersJson;
        var body = attempt.ResponseBody is { Length: > MaxResponseLength } b ? b[..MaxResponseLength] : attempt.ResponseBody;
        var error = attempt.ErrorMessage is { Length: > MaxErrorLength } e ? e[..MaxErrorLength] : attempt.ErrorMessage;

        return attempt with
        {
            ResponseHeadersJson = headers,
            ResponseBody = body,
            ErrorMessage = error
        };
    }
}
