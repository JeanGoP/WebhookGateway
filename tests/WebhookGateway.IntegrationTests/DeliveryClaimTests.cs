using System.Collections.Concurrent;
using Shouldly;
using WebhookGateway.Core.Domain;
using Xunit;

namespace WebhookGateway.IntegrationTests;

/// <summary>
/// El punto donde un bug se convierte en entregas duplicadas en producción: durante un
/// despliegue hay dos instancias vivas reclamando la misma cola. Estos tests exigen SQL
/// Server real (la fixture de la colección).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class DeliveryClaimTests(SqlServerFixture fixture)
{
    // Hora fija: el claim la recibe por parámetro, así que la prueba es determinista.
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    [RequiresDockerFact]
    public async Task Claim_bajo_workers_concurrentes_no_duplica_ni_pierde_ninguna_entrega()
    {
        await fixture.ResetDeliveriesAsync();

        const int total = 500;
        const int endpoints = 25;
        const int workers = 8;

        // Backlog repartido entre destinos: ejercita el ROW_NUMBER por OutboundEndpointId.
        for (var i = 0; i < total; i++)
        {
            await fixture.InsertDeliveryAsync(
                DeliveryStatus.Pending, outboundEndpointId: i % endpoints + 1,
                nextAttemptAt: Now.AddMinutes(-1), expiresAt: Now.AddHours(1), createdAt: Now);
        }

        // N workers reclaman a la vez hasta drenar la cola. Si el claim no fuese atómico,
        // dos verían la misma fila y saldría repetida en esta bolsa.
        var claimed = new ConcurrentBag<long>();
        var leaseUntil = Now.AddSeconds(180);

        async Task RunWorker(string workerId)
        {
            while (true)
            {
                var batch = await fixture.Claimer.ClaimAsync(
                    Now, leaseUntil, workerId, batchSize: 100, perEndpoint: 20, CancellationToken.None);
                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var d in batch)
                {
                    claimed.Add(d.Id);
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, workers).Select(i => RunWorker($"worker-{i}")));

        var ids = claimed.ToList();
        ids.Count.ShouldBe(total);                                       // ni una perdida
        ids.Distinct().Count().ShouldBe(total);                          // ni una reclamada dos veces
        (await fixture.CountByStatusAsync(DeliveryStatus.InFlight)).ShouldBe(total);
    }

    [RequiresDockerFact]
    public async Task El_claim_reparte_por_destino_y_no_deja_sin_servicio_a_los_pequeños()
    {
        await fixture.ResetDeliveriesAsync();

        // Un destino saturado (1) y otro con poco trabajo (2).
        for (var i = 0; i < 100; i++)
        {
            await fixture.InsertDeliveryAsync(
                DeliveryStatus.Pending, 1, Now.AddMinutes(-1), Now.AddHours(1), createdAt: Now);
        }

        for (var i = 0; i < 5; i++)
        {
            await fixture.InsertDeliveryAsync(
                DeliveryStatus.Pending, 2, Now.AddMinutes(-1), Now.AddHours(1), createdAt: Now);
        }

        var batch = await fixture.Claimer.ClaimAsync(
            Now, Now.AddSeconds(180), "solo", batchSize: 100, perEndpoint: 20, CancellationToken.None);

        var porDestino = batch.GroupBy(d => d.OutboundEndpointId).ToDictionary(g => g.Key, g => g.Count());
        porDestino[1].ShouldBe(20);   // el saturado queda capado al techo por destino
        porDestino[2].ShouldBe(5);    // el pequeño entra entero, no se queda esperando
    }
}
