using Shouldly;
using WebhookGateway.Core.Domain;
using Xunit;

namespace WebhookGateway.IntegrationTests;

/// <summary>
/// Las dos pasadas de mantenimiento del despachador contra SQL Server real: recuperar
/// leases que dejó colgados una instancia caída y caducar lo que se pasó de ventana.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class DeliveryMaintenanceTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    [RequiresDockerFact]
    public async Task Recupera_solo_los_leases_vencidos_y_deja_intactos_los_vigentes()
    {
        await fixture.ResetDeliveriesAsync();

        // Un worker muerto dejó este lease vencido: sigue en InFlight pero ya nadie lo sirve.
        var huerfano = await fixture.InsertDeliveryAsync(
            DeliveryStatus.InFlight, 1, Now.AddMinutes(-5), Now.AddHours(1),
            leaseUntil: Now.AddSeconds(-1), workerId: "worker-muerto", createdAt: Now);

        // Este otro lo tiene un worker vivo: su lease aún no vence y no debe tocarse.
        var vigente = await fixture.InsertDeliveryAsync(
            DeliveryStatus.InFlight, 1, Now.AddMinutes(-5), Now.AddHours(1),
            leaseUntil: Now.AddSeconds(120), workerId: "worker-vivo", createdAt: Now);

        var recuperadas = await fixture.Claimer.RecoverOrphanedLeasesAsync(Now, limit: 500, CancellationToken.None);

        recuperadas.ShouldBe(1);

        var h = await fixture.ReadDeliveryAsync(huerfano);
        h.Status.ShouldBe((byte)DeliveryStatus.Retrying);   // vuelve a la cola
        h.WorkerId.ShouldBeNull();
        h.LeaseUntil.ShouldBeNull();

        var v = await fixture.ReadDeliveryAsync(vigente);
        v.Status.ShouldBe((byte)DeliveryStatus.InFlight);   // intacto
        v.WorkerId.ShouldBe("worker-vivo");
    }

    [RequiresDockerFact]
    public async Task Caduca_las_entregas_pasadas_de_ventana_y_dejan_de_reclamarse()
    {
        await fixture.ResetDeliveriesAsync();

        var vencida = await fixture.InsertDeliveryAsync(
            DeliveryStatus.Pending, 1, nextAttemptAt: Now.AddMinutes(-5),
            expiresAt: Now.AddSeconds(-1), createdAt: Now);

        var caducadas = await fixture.Claimer.ExpireOverdueAsync(Now, limit: 500, CancellationToken.None);

        caducadas.ShouldBe(1);
        (await fixture.ReadDeliveryAsync(vencida)).Status.ShouldBe((byte)DeliveryStatus.Expired);

        // Y una vez expirada, el claim no la toca: exige ExpiresAt > now.
        var batch = await fixture.Claimer.ClaimAsync(
            Now, Now.AddSeconds(180), "w", batchSize: 100, perEndpoint: 20, CancellationToken.None);
        batch.ShouldBeEmpty();
    }
}
