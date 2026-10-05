using System.Data;
using System.Globalization;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebhookGateway.Data.Db;

namespace WebhookGateway.Dispatcher.Recording;

/// <summary>
/// Acumula resultados y los escribe en bloque.
/// </summary>
public sealed class DeliveryRecorder(
    ISqlConnectionFactory connectionFactory,
    IOptions<DispatcherOptions> options,
    ILogger<DeliveryRecorder> logger)
{
    private readonly string _workerId = options.Value.WorkerId;
    private readonly Lock _gate = new();
    private readonly List<AttemptRecord> _attempts = [];
    private readonly List<DeliveryUpdate> _updates = [];

    public void Add(AttemptRecord? attempt, DeliveryUpdate update)
    {
        lock (_gate)
        {
            if (attempt is not null)
            {
                _attempts.Add(ColumnLimits.Fit(attempt));
            }

            _updates.Add(ColumnLimits.Fit(update));
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
            var applied = await UpdateDeliveriesBatchAsync(connection, transaction, updates, cancellationToken);

            if (applied < updates.Length)
            {
                logger.LogWarning(
                    "{Skipped} de {Total} resultados no se guardaron: su lease venció y otro worker ya tenía " +
                    "la entrega o ya la había cerrado. El destino pudo recibirlas dos veces.",
                    updates.Length - applied, updates.Length);
            }
        }

        if (attempts.Length > 0)
        {
            await InsertAttemptsBatchAsync(connection, transaction, attempts, cancellationToken);
        }

        transaction.Commit();

        return updates.Length;
    }

    /*
        La guarda del WHERE es lo que impide que un worker con el lease vencido pise a otro. Si el lease
        venció a mitad de lote, el mantenimiento devolvió la entrega a la cola, y para cuando este
        worker vuelca su resultado puede que otro la haya reclamado o incluso cerrado.

        - Status IN (1, 2): nunca se reabre ni se reescribe una entrega terminada.
        - WorkerId propio o nulo: si otro worker la tiene ahora, el resultado es suyo. Si está
          nula, la entrega volvió a la cola y nadie la ha tocado aún: el resultado de este worker es
          lo más reciente que se sabe de ella, y guardarlo evita enviarla otra vez.
    */
    private async Task<int> UpdateDeliveriesBatchAsync(
        IDbConnection connection, IDbTransaction transaction, DeliveryUpdate[] updates, CancellationToken ct)
    {
        var applied = 0;

        foreach (var chunk in updates.Chunk(50))
        {
            var sb = new StringBuilder();
            var parameters = new DynamicParameters();
            parameters.Add("wk", _workerId);

            for (int i = 0; i < chunk.Length; i++)
            {
                var item = chunk[i];
                sb.AppendLine(CultureInfo.InvariantCulture, $"""
                    UPDATE dbo.WebhookDelivery
                    SET Status = @st{i}, AttemptCount = @at{i}, NextAttemptAt = @nx{i},
                        LastStatusCode = @sc{i}, LastError = @er{i}, CompletedAt = @cm{i},
                        LeaseUntil = NULL, WorkerId = NULL
                    WHERE CreatedAt = @cr{i} AND Id = @id{i}
                      AND Status IN (1, 2) AND (WorkerId = @wk OR WorkerId IS NULL);
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

            applied += await connection.ExecuteAsync(
                new CommandDefinition(sb.ToString(), parameters, transaction, cancellationToken: ct));
        }

        return applied;
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
}
