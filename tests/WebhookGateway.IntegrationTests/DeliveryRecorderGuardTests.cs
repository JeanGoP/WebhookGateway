using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using WebhookGateway.Core.Domain;
using WebhookGateway.Dispatcher;
using WebhookGateway.Dispatcher.Recording;
using Xunit;

namespace WebhookGateway.IntegrationTests;

/// <summary>
/// Un worker cuyo lease venció a mitad de lote vuelca su resultado tarde. Para entonces la entrega
/// pudo pasar a otro worker, o cerrarse. El volcado no puede pisar ninguna de las dos cosas.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class DeliveryRecorderGuardTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    [RequiresSqlServerFact]
    public async Task Un_resultado_tardio_no_pisa_lo_que_otro_worker_tiene_o_ya_cerro()
    {
        await fixture.ResetDeliveriesAsync();

        var propia = await Insert(DeliveryStatus.InFlight, workerId: "A");
        var deOtro = await Insert(DeliveryStatus.InFlight, workerId: "B");
        var cerrada = await Insert(DeliveryStatus.Delivered, workerId: null);
        var devuelta = await Insert(DeliveryStatus.Retrying, workerId: null);

        var recorder = new DeliveryRecorder(
            fixture.ConnectionFactory,
            Options.Create(new DispatcherOptions { WorkerId = "A" }),
            NullLogger<DeliveryRecorder>.Instance);

        foreach (var id in new[] { propia, deOtro, devuelta })
        {
            recorder.Add(null, Update(id, DeliveryStatus.Delivered));
        }

        recorder.Add(null, Update(cerrada, DeliveryStatus.Retrying));

        var guardadas = await recorder.FlushAsync(CancellationToken.None);

        guardadas.ShouldBe(4, "FlushAsync informa de lo que intentó volcar");

        // La suya, normal.
        (await fixture.ReadDeliveryAsync(propia)).Status.ShouldBe((byte)DeliveryStatus.Delivered);

        // La que tiene otro worker sigue siendo de ese worker: el resultado es suyo.
        var otro = await fixture.ReadDeliveryAsync(deOtro);
        otro.Status.ShouldBe((byte)DeliveryStatus.InFlight);
        otro.WorkerId.ShouldBe("B");

        // Una entregada no se reabre.
        (await fixture.ReadDeliveryAsync(cerrada)).Status.ShouldBe((byte)DeliveryStatus.Delivered);

        // La que volvió a la cola sin que nadie la tocara sí recibe el resultado: es lo último que
        // se sabe de ella, y no guardarlo haría que se enviase otra vez.
        (await fixture.ReadDeliveryAsync(devuelta)).Status.ShouldBe((byte)DeliveryStatus.Delivered);
    }

    private Task<long> Insert(DeliveryStatus status, string? workerId) =>
        fixture.InsertDeliveryAsync(
            status, outboundEndpointId: 1, Now.AddMinutes(-1), Now.AddHours(1),
            leaseUntil: status == DeliveryStatus.InFlight ? Now.AddMinutes(3) : null,
            workerId: workerId, createdAt: Now);

    private static DeliveryUpdate Update(long id, DeliveryStatus status) =>
        new(id, Now, (byte)status, AttemptCount: 1, NextAttemptAt: Now, LastStatusCode: 200, LastError: null,
            CompletedAt: status == DeliveryStatus.Delivered ? Now : null);
}
