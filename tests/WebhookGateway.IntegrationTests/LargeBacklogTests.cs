using Shouldly;
using WebhookGateway.Core.Domain;
using WebhookGateway.Dispatcher.Claiming;
using Xunit;

namespace WebhookGateway.IntegrationTests;

/// <summary>
/// El caso para el que existe el gateway: un destino lleva horas caído o es lento, y acumula un
/// backlog enorme. Reclamar y descubrir trabajo tiene que costar lo mismo con diez pendientes que
/// con cientos de miles; si no, el despachador castiga a la base justo cuando más falta hace, y con
/// ella a la recepción, que comparte tabla.
/// </summary>
/// <remarks>
/// Medido en LocalDB con el SQL anterior: el claim de veinte entregas pedía 6.228 bloqueos de fila
/// y escalaba a bloqueo de tabla en cada llamada, y el descubrimiento leía unas mil páginas por
/// pasada con 300.000 pendientes. Estas pruebas fijan los números de ahora.
/// </remarks>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class LargeBacklogTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Unas 18 horas de la integración grande (400.000 al día) sin entregar.</summary>
    private const int Backlog = 300_000;

    /// <summary>
    /// Lo que se mide son los bloqueos. El claim lleva UPDLOCK: cada fila que lee queda bloqueada
    /// hasta que termina, y pasadas unas 5.000 SQL Server intenta escalar a un bloqueo de tabla que
    /// frenaría también los INSERT de la recepción.
    /// </summary>
    [RequiresSqlServerFact]
    public async Task Reclamar_con_mucho_pendiente_solo_bloquea_lo_que_reclama()
    {
        await fixture.ResetDeliveriesAsync();
        await fixture.SeedBacklogAsync(
            outboundEndpointId: 1, Backlog, Now.AddHours(-1), Now.AddDays(2), createdAt: Now.AddHours(-1));

        var (claimed, spent) = await ClaimMeasuringAsync(endpointId: 1);

        claimed.ShouldBe(20);
        spent.EscalationAttempts.ShouldBe(0, "el claim no debe intentar bloquear la tabla entera");

        // Veinte filas actualizadas tocan cinco índices cada una: unos cientos de bloqueos. El
        // margen es amplio a propósito; lo que se descarta es leer el backlog, que serían 300.000.
        spent.RowLocks.ShouldBeLessThan(1_000);
    }

    /// <summary>
    /// Un destino que responde mal acumula reintentos programados para más tarde. Reclamar sus pocas
    /// entregas ya vencidas no puede pasar por todas esas: es el caso en que el optimizador, si
    /// elige el índice por (destino, estado), mira la fecha de cada fila en la tabla.
    /// </summary>
    [RequiresSqlServerFact]
    public async Task Reclamar_no_recorre_los_reintentos_que_aun_no_vencen()
    {
        await fixture.ResetDeliveriesAsync();
        await fixture.SeedBacklogAsync(
            outboundEndpointId: 3, 100_000, Now.AddMinutes(10), Now.AddDays(2),
            createdAt: Now.AddHours(-2), status: DeliveryStatus.Retrying);
        await fixture.SeedBacklogAsync(
            outboundEndpointId: 3, 5, Now.AddMinutes(-1), Now.AddHours(1), createdAt: Now);

        var (claimed, spent) = await ClaimMeasuringAsync(endpointId: 3);

        claimed.ShouldBe(5);
        spent.EscalationAttempts.ShouldBe(0);
        spent.RowLocks.ShouldBeLessThan(1_000);
    }

    /// <summary>
    /// El descubrimiento corre cada vez que llega algo, hasta cuatro veces por segundo. El destino 2
    /// es el que importa: para saber que tiene trabajo no puede atravesar el backlog del 1.
    /// </summary>
    [RequiresSqlServerFact]
    public async Task Descubrir_con_mucho_pendiente_no_recorre_el_backlog()
    {
        await fixture.ResetDeliveriesAsync();
        await fixture.EnsureOutboundEndpointsAsync(1, 2);
        await fixture.SeedBacklogAsync(
            outboundEndpointId: 1, Backlog, Now.AddHours(-1), Now.AddDays(2), createdAt: Now.AddHours(-1));
        await fixture.InsertDeliveryAsync(
            DeliveryStatus.Pending, 2, Now.AddMinutes(-1), Now.AddHours(1), createdAt: Now);

        var counting = new ReadCountingConnectionFactory(fixture.ConnectionFactory);
        var conTrabajo = await new DeliveryClaimer(counting).FindEndpointsWithWorkAsync(Now, CancellationToken.None);

        conTrabajo.Order().ShouldBe([1, 2]);

        // Una búsqueda por destino que para en la primera fila: una docena de páginas. Recorrer el
        // backlog, que es lo que pasaba, eran más de mil.
        counting.LogicalReads.ShouldBeLessThan(100);
    }

    private async Task<(int Claimed, LockCounters Spent)> ClaimMeasuringAsync(int endpointId)
    {
        var before = await fixture.ReadLockCountersAsync();

        var claimed = await fixture.Claimer.ClaimForEndpointAsync(
            endpointId, Now, Now.AddSeconds(180), "solo", batchSize: 20, CancellationToken.None);

        return (claimed.Count, (await fixture.ReadLockCountersAsync()).Since(before));
    }
}
