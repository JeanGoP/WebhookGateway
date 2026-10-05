using Dapper;
using WebhookGateway.Core.Domain;

namespace WebhookGateway.IntegrationTests;

/// <summary>
/// Lo necesario para probar el despachador con un backlog de verdad: sembrar cientos de miles de
/// entregas de golpe y medir lo que el motor hizo para atender una consulta.
/// </summary>
/// <remarks>
/// Con diez filas cualquier consulta es barata, y así es como pasó desapercibido que el claim
/// leía todo lo pendiente: se verificó con cuarenta. Lo que hay que comprobar no es el resultado,
/// que sale bien igual, sino cuánto lee y cuánto bloquea con un destino que lleva horas caído.
/// </remarks>
internal static class BacklogProbe
{
    /// <summary>Siembra <paramref name="count"/> entregas de un destino en una sola sentencia.</summary>
    public static async Task SeedBacklogAsync(
        this SqlServerFixture fixture, int outboundEndpointId, int count,
        DateTime nextAttemptAt, DateTime expiresAt, DateTime createdAt,
        DeliveryStatus status = DeliveryStatus.Pending)
    {
        using var conn = await fixture.OpenAsync();

        await conn.ExecuteAsync(
            """
            INSERT INTO dbo.WebhookDelivery
                (CreatedAt, MessageId, OutboundEndpointId, Status, NextAttemptAt, ExpiresAt)
            SELECT TOP (@Count) @CreatedAt, 1, @Endpoint, @Status, @NextAttemptAt, @ExpiresAt
            FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b;
            """,
            new
            {
                Count = count,
                CreatedAt = createdAt,
                Endpoint = outboundEndpointId,
                Status = (byte)status,
                NextAttemptAt = nextAttemptAt,
                ExpiresAt = expiresAt,
            },
            commandTimeout: 300);
    }

    /// <summary>
    /// Da de alta destinos con Ids fijos. El descubrimiento parte de la tabla de destinos, así que
    /// una entrega de un destino que no está dado de alta no se descubre.
    /// </summary>
    /// <remarks>
    /// Con su propia integración: la siembra de <c>TrafficWriteInboundTests</c> busca la suya por slug
    /// y, si la encuentra, da por hecho que ya tiene su endpoint de entrada. Compartir el slug hacía
    /// que esas pruebas fallaran o no según el orden en que corrieran.
    /// </remarks>
    public static async Task EnsureOutboundEndpointsAsync(this SqlServerFixture fixture, params int[] ids)
    {
        using var conn = await fixture.OpenAsync();

        await conn.ExecuteAsync(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.Integration WHERE Slug = 'pruebas-destinos')
                INSERT INTO dbo.Integration (Name, Slug) VALUES (N'Pruebas de destinos', 'pruebas-destinos');
            """);

        foreach (var id in ids)
        {
            await conn.ExecuteAsync(
                """
                IF NOT EXISTS (SELECT 1 FROM dbo.OutboundEndpoint WHERE Id = @Id)
                BEGIN
                    SET IDENTITY_INSERT dbo.OutboundEndpoint ON;

                    INSERT INTO dbo.OutboundEndpoint (Id, IntegrationId, Name, TargetUrl)
                    SELECT @Id, i.Id, CONCAT(N'Destino ', @Id), N'https://destino.invalid/'
                    FROM dbo.Integration AS i
                    WHERE i.Slug = 'pruebas-destinos';

                    SET IDENTITY_INSERT dbo.OutboundEndpoint OFF;
                END
                """,
                new { Id = id });
        }
    }

    /// <summary>
    /// Bloqueos de fila pedidos sobre <c>WebhookDelivery</c> y escalados a bloqueo de tabla
    /// intentados, acumulados desde que arrancó el servidor. Se usan por diferencia.
    /// </summary>
    public static async Task<LockCounters> ReadLockCountersAsync(this SqlServerFixture fixture)
    {
        using var conn = await fixture.OpenAsync();

        return await conn.QuerySingleAsync<LockCounters>(
            """
            SELECT ISNULL(SUM(row_lock_count), 0)                  AS RowLocks,
                   ISNULL(SUM(index_lock_promotion_attempt_count), 0) AS EscalationAttempts
            FROM sys.dm_db_index_operational_stats(DB_ID(), OBJECT_ID(N'dbo.WebhookDelivery'), NULL, NULL);
            """);
    }
}

/// <summary>Contadores de bloqueo de una tabla, para comparar antes y después.</summary>
internal sealed record LockCounters(long RowLocks, long EscalationAttempts)
{
    /// <summary>Lo ocurrido entre <paramref name="before"/> y esta lectura.</summary>
    public LockCounters Since(LockCounters before) =>
        new(RowLocks - before.RowLocks, EscalationAttempts - before.EscalationAttempts);
}
