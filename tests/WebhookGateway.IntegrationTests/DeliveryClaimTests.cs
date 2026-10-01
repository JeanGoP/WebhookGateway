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

    /// <summary>
    /// Obligatorio según CLAUDE.md. Con el claim por destino la contención se concentra: todos los
    /// workers pelean por las mismas filas del mismo destino, que es el peor caso para READPAST y
    /// UPDLOCK. Si el claim no fuese atómico, aquí saldría una entrega repetida.
    /// </summary>
    [RequiresDockerFact]
    public async Task Claim_bajo_workers_concurrentes_no_duplica_ni_pierde_ninguna_entrega()
    {
        await fixture.ResetDeliveriesAsync();

        const int total = 500;
        const int endpoints = 25;
        const int workers = 8;

        for (var i = 0; i < total; i++)
        {
            await fixture.InsertDeliveryAsync(
                DeliveryStatus.Pending, outboundEndpointId: i % endpoints + 1,
                nextAttemptAt: Now.AddMinutes(-1), expiresAt: Now.AddHours(1), createdAt: Now);
        }

        var claimed = new ConcurrentBag<long>();
        var leaseUntil = Now.AddSeconds(180);

        // Cada worker recorre todos los destinos, como hará el supervisor al descubrirlos.
        async Task RunWorker(string workerId)
        {
            var pendientes = true;

            while (pendientes)
            {
                pendientes = false;

                for (var endpointId = 1; endpointId <= endpoints; endpointId++)
                {
                    var batch = await fixture.Claimer.ClaimForEndpointAsync(
                        endpointId, Now, leaseUntil, workerId, batchSize: 20, CancellationToken.None);

                    if (batch.Count > 0)
                    {
                        pendientes = true;
                    }

                    foreach (var d in batch)
                    {
                        batch.ShouldAllBe(x => x.OutboundEndpointId == endpointId);
                        claimed.Add(d.Id);
                    }
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, workers).Select(i => RunWorker($"worker-{i}")));

        var ids = claimed.ToList();
        ids.Count.ShouldBe(total);                                       // ni una perdida
        ids.Distinct().Count().ShouldBe(total);                          // ni una reclamada dos veces
        (await fixture.CountByStatusAsync(DeliveryStatus.InFlight)).ShouldBe(total);
    }

    /// <summary>
    /// Lo que antes garantizaba el <c>ROW_NUMBER() PARTITION BY</c> y ahora da la estructura: el
    /// destino con cinco entregas no espera detrás del que tiene cien, porque ni siquiera mira su
    /// backlog.
    /// </summary>
    [RequiresDockerFact]
    public async Task Un_destino_con_mucho_backlog_no_estorba_al_que_tiene_poco()
    {
        await fixture.ResetDeliveriesAsync();

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

        var pequeno = await fixture.Claimer.ClaimForEndpointAsync(
            endpointId: 2, Now, Now.AddSeconds(180), "solo", batchSize: 20, CancellationToken.None);

        pequeno.Count.ShouldBe(5);
        pequeno.ShouldAllBe(d => d.OutboundEndpointId == 2);

        // Y las cien del saturado siguen intactas: nadie las ha tocado al reclamar las del pequeño.
        (await fixture.CountByStatusAsync(DeliveryStatus.Pending)).ShouldBe(100);
    }

    [RequiresDockerFact]
    public async Task El_claim_respeta_el_tamaño_de_lote_y_sirve_lo_mas_vencido_primero()
    {
        await fixture.ResetDeliveriesAsync();

        // La más antigua primero: el claim ordena por NextAttemptAt.
        var primera = await fixture.InsertDeliveryAsync(
            DeliveryStatus.Pending, 7, Now.AddMinutes(-30), Now.AddHours(1), createdAt: Now);

        for (var i = 0; i < 9; i++)
        {
            await fixture.InsertDeliveryAsync(
                DeliveryStatus.Pending, 7, Now.AddMinutes(-1), Now.AddHours(1), createdAt: Now);
        }

        var batch = await fixture.Claimer.ClaimForEndpointAsync(
            endpointId: 7, Now, Now.AddSeconds(180), "solo", batchSize: 4, CancellationToken.None);

        batch.Count.ShouldBe(4);
        batch.Select(d => d.Id).ShouldContain(primera);
    }

    [RequiresDockerFact]
    public async Task El_descubrimiento_solo_devuelve_destinos_con_trabajo_vencido()
    {
        await fixture.ResetDeliveriesAsync();

        // Vencida: toca ya.
        await fixture.InsertDeliveryAsync(
            DeliveryStatus.Pending, 10, Now.AddMinutes(-1), Now.AddHours(1), createdAt: Now);

        // Programada para más tarde: todavía no.
        await fixture.InsertDeliveryAsync(
            DeliveryStatus.Retrying, 11, Now.AddMinutes(5), Now.AddHours(1), createdAt: Now);

        // Fuera de ventana: ya no.
        await fixture.InsertDeliveryAsync(
            DeliveryStatus.Pending, 12, Now.AddMinutes(-1), Now.AddSeconds(-1), createdAt: Now);

        // Ya entregada: nunca más.
        await fixture.InsertDeliveryAsync(
            DeliveryStatus.Delivered, 13, Now.AddMinutes(-1), Now.AddHours(1), createdAt: Now);

        var conTrabajo = await fixture.Claimer.FindEndpointsWithWorkAsync(Now, CancellationToken.None);

        conTrabajo.ShouldBe([10]);
    }
}
