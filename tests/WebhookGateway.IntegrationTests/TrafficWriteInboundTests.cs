using System.Data;
using Dapper;
using Shouldly;
using Xunit;

namespace WebhookGateway.IntegrationTests;

/// <summary>
/// La deduplicación de <c>sp_Traffic_WriteInbound</c>. Esto solo se puede probar contra un motor
/// real: lo que está en juego son bloqueos de rango y el estado de la transacción, que ninguna
/// simulación reproduce.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class TrafficWriteInboundTests(SqlServerFixture fixture)
{
    // Los estados coinciden con MessageStatus; aquí se usan en crudo porque es lo que devuelve el SP.
    private const byte StatusReceived = 0;
    private const byte StatusDuplicate = 1;

    /// <summary>
    /// El caso que antes acababa en 503. Dos peticiones con la misma clave a la vez: la segunda
    /// chocaba con la clave única, el CATCH intentaba seguir sobre una transacción ya inservible
    /// (3930) y el procedimiento fallaba, así que la recepción respondía 503 a un duplicado, que
    /// es justo lo que tiene que resolverse sin error.
    /// </summary>
    [RequiresDockerFact]
    public async Task Dos_recepciones_simultaneas_con_la_misma_clave_dejan_un_solo_mensaje_bueno()
    {
        var seed = await ResetAndSeedAsync();
        const string key = "evento-repetido";

        var both = await Task.WhenAll(
            WriteAsync(seed.InboundEndpointId, key),
            WriteAsync(seed.InboundEndpointId, key));

        both.Count(r => r.Status == StatusReceived).ShouldBe(1);
        both.Count(r => r.Status == StatusDuplicate).ShouldBe(1);

        // El duplicado apunta al mensaje que ganó, y no creó entregas.
        var winner = both.Single(r => r.Status == StatusReceived);
        var loser = both.Single(r => r.Status == StatusDuplicate);

        loser.ExistingMessageId.ShouldBe(winner.MessageId);
        loser.DeliveryIds.ShouldBeEmpty();
        winner.DeliveryIds.Count.ShouldBe(1);

        (await CountAsync("SELECT COUNT(*) FROM dbo.MessageDedupe;")).ShouldBe(1);
        (await CountAsync("SELECT COUNT(*) FROM dbo.WebhookDelivery;")).ShouldBe(1);

        // Los dos mensajes se registran: saber que llegó dos veces es parte de lo que se depura.
        (await CountAsync("SELECT COUNT(*) FROM dbo.WebhookMessage;")).ShouldBe(2);
    }

    [RequiresDockerFact]
    public async Task Una_clave_que_ya_existe_se_marca_duplicada_sin_crear_entregas()
    {
        var seed = await ResetAndSeedAsync();
        const string key = "evento-de-ayer";

        var first = await WriteAsync(seed.InboundEndpointId, key);
        var second = await WriteAsync(seed.InboundEndpointId, key);

        first.Status.ShouldBe(StatusReceived);
        second.Status.ShouldBe(StatusDuplicate);
        second.ExistingMessageId.ShouldBe(first.MessageId);
        second.DeliveryIds.ShouldBeEmpty();

        (await CountAsync("SELECT COUNT(*) FROM dbo.WebhookDelivery;")).ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task Claves_distintas_no_se_estorban()
    {
        var seed = await ResetAndSeedAsync();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => WriteAsync(seed.InboundEndpointId, $"evento-{i}")));

        results.ShouldAllBe(r => r.Status == StatusReceived);

        (await CountAsync("SELECT COUNT(*) FROM dbo.MessageDedupe;")).ShouldBe(8);
        (await CountAsync("SELECT COUNT(*) FROM dbo.WebhookDelivery;")).ShouldBe(8);
    }

    [RequiresDockerFact]
    public async Task Sin_clave_de_deduplicacion_cada_recepcion_es_un_mensaje_nuevo()
    {
        var seed = await ResetAndSeedAsync();

        var first = await WriteAsync(seed.InboundEndpointId, dedupeKey: null);
        var second = await WriteAsync(seed.InboundEndpointId, dedupeKey: null);

        first.Status.ShouldBe(StatusReceived);
        second.Status.ShouldBe(StatusReceived);
        second.MessageId.ShouldNotBe(first.MessageId);

        (await CountAsync("SELECT COUNT(*) FROM dbo.MessageDedupe;")).ShouldBe(0);
        (await CountAsync("SELECT COUNT(*) FROM dbo.WebhookDelivery;")).ShouldBe(2);
    }

    private async Task<WriteResult> WriteAsync(int inboundEndpointId, string? dedupeKey)
    {
        var now = DateTime.UtcNow;

        using var conn = await fixture.OpenAsync();
        using var multi = await conn.QueryMultipleAsync(
            "dbo.sp_Traffic_WriteInbound",
            new
            {
                InboundEndpointId = inboundEndpointId,
                ReceivedAt = now,
                SourceIp = "10.0.0.1",
                HttpMethod = "POST",
                HeadersJson = "{}",
                QueryString = (string?)null,
                BodySizeBytes = 2,
                BodyHash = new byte[32],
                DedupeKey = dedupeKey,
                DedupeExpiresAt = now.AddHours(24),
                PayloadEncoding = (byte)0,
                PayloadSizeBytes = 2,
                PayloadBody = new byte[] { 0x7B, 0x7D },
                PayloadStorageRef = (string?)null,
            },
            commandType: CommandType.StoredProcedure);

        var header = await multi.ReadSingleAsync<WriteHeader>();
        var deliveryIds = (await multi.ReadAsync<long>()).ToList();

        return new WriteResult(header.MessageId, header.Status, header.ExistingMessageId, deliveryIds);
    }

    /// <summary>Deja las tablas de tráfico vacías y una ruta con un destino suscrito.</summary>
    private async Task<Seed> ResetAndSeedAsync()
    {
        using var conn = await fixture.OpenAsync();

        await conn.ExecuteAsync(
            """
            TRUNCATE TABLE dbo.WebhookDelivery;
            TRUNCATE TABLE dbo.WebhookPayload;
            TRUNCATE TABLE dbo.WebhookMessage;
            DELETE FROM dbo.MessageDedupe;
            """);

        // Las de configuración tienen FKs entre sí, así que se siembran en orden y se reutilizan.
        return await conn.QuerySingleAsync<Seed>(
            """
            DECLARE @integration int = (SELECT TOP 1 Id FROM dbo.Integration WHERE Slug = 'pruebas');

            IF @integration IS NULL
            BEGIN
                INSERT INTO dbo.Integration (Name, Slug) VALUES (N'Pruebas', 'pruebas');
                SET @integration = SCOPE_IDENTITY();

                INSERT INTO dbo.InboundEndpoint (IntegrationId, Name, Slug)
                VALUES (@integration, N'Entrada', 'entrada');
                DECLARE @inbound int = SCOPE_IDENTITY();

                INSERT INTO dbo.OutboundEndpoint (IntegrationId, Name, TargetUrl)
                VALUES (@integration, N'Destino', N'https://ejemplo.invalid/hook');
                DECLARE @outbound int = SCOPE_IDENTITY();

                INSERT INTO dbo.Subscription (InboundEndpointId, OutboundEndpointId)
                VALUES (@inbound, @outbound);
            END

            SELECT TOP 1 InboundEndpointId = e.Id
            FROM dbo.InboundEndpoint e
            WHERE e.IntegrationId = @integration;
            """);
    }

    private async Task<int> CountAsync(string sql)
    {
        using var conn = await fixture.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(sql);
    }

    private sealed record Seed(int InboundEndpointId);

    private sealed record WriteHeader(long MessageId, byte Status, long? ExistingMessageId);

    private sealed record WriteResult(long MessageId, byte Status, long? ExistingMessageId, IReadOnlyList<long> DeliveryIds);
}
