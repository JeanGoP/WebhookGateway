using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using WebhookGateway.Core.Auth;
using WebhookGateway.Core.Delivery;
using WebhookGateway.Core.Domain;
using WebhookGateway.Core.Notifications;
using WebhookGateway.Dispatcher.Notifications;
using WebhookGateway.Dispatcher.Sending;
using Xunit;

namespace WebhookGateway.IntegrationTests;

/// <summary>
/// Un destino tiene decenas de entregas en vuelo, y cuando cae todas lo notan casi a la vez. En la
/// prueba de carga del 2026-10-05 dos de ellas escribieron la misma transición a la vez, el MERGE
/// chocó con la clave primaria y la excepción cortó la entrega antes de apuntar su resultado.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class EndpointHealthConcurrencyTests(SqlServerFixture fixture)
{
    [RequiresSqlServerFact]
    public async Task Muchas_entregas_que_notan_la_misma_caida_no_chocan_al_escribirla()
    {
        const int endpointId = 101;
        await fixture.EnsureOutboundEndpointsAsync(endpointId);
        await ClearHealthAsync(endpointId);

        var tracker = NewTracker();

        // 64 fallos a la vez: cruzan juntos los umbrales de Degraded (3) y Down (5).
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
            tracker.RecordAttemptAsync(Target(endpointId), AttemptVerdict.Retryable, 503, "caído", CancellationToken.None))));

        (await HealthStatusAsync(endpointId)).ShouldBe((byte)EndpointHealthStatus.Down);
    }

    /// <summary>
    /// Durante un despliegue hay dos instancias vivas, cada una con su memoria: las dos pueden escribir
    /// la primera transición de un destino a la vez. Eso lo resuelve el HOLDLOCK del MERGE, no el
    /// cerrojo en memoria.
    /// </summary>
    [RequiresSqlServerFact]
    public async Task Dos_instancias_que_transicionan_a_la_vez_no_chocan_en_la_base()
    {
        var ids = Enumerable.Range(110, 20).ToArray();
        await fixture.EnsureOutboundEndpointsAsync(ids);

        foreach (var id in ids)
        {
            await ClearHealthAsync(id);
        }

        var instancias = new[] { NewTracker(), NewTracker() };

        await Task.WhenAll(ids.SelectMany(id => instancias.Select(tracker => Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                await tracker.RecordAttemptAsync(Target(id), AttemptVerdict.Retryable, 503, "caído", CancellationToken.None);
            }
        }))));

        foreach (var id in ids)
        {
            (await HealthStatusAsync(id)).ShouldBe((byte)EndpointHealthStatus.Down);
        }
    }

    private EndpointHealthTracker NewTracker() => new(
        new NotificationStore(fixture.ConnectionFactory, TimeProvider.System),
        fixture.ConnectionFactory,
        TimeProvider.System,
        Options.Create(new NotificationOptions { Enabled = false }),
        NullLogger<EndpointHealthTracker>.Instance);

    private async Task<byte?> HealthStatusAsync(int endpointId)
    {
        using var conn = await fixture.OpenAsync();
        return await conn.ExecuteScalarAsync<byte?>(
            "SELECT HealthStatus FROM dbo.EndpointHealthState WHERE OutboundEndpointId = @endpointId;", new { endpointId });
    }

    private async Task ClearHealthAsync(int endpointId)
    {
        using var conn = await fixture.OpenAsync();
        await conn.ExecuteAsync("DELETE FROM dbo.EndpointHealthState WHERE OutboundEndpointId = @endpointId;", new { endpointId });
    }

    private static OutboundTarget Target(int id) => new(
        Id: id,
        IntegrationId: 1,
        IntegrationName: "Pruebas",
        EndpointName: $"Destino {id}",
        TargetUrl: new Uri("https://ejemplo.invalid/hook"),
        Method: HttpMethod.Post,
        AuthType: OutboundAuthType.None,
        AuthConfig: new NoOutboundAuth(),
        CustomHeaders: new Dictionary<string, string>(),
        RateLimitPerMinute: 600,
        MaxConcurrency: 32,
        Timeout: TimeSpan.FromSeconds(30),
        RetryPolicy: RetryPolicy.Default,
        BreakerFailureThreshold: 5,
        BreakerOpenSeconds: 60);
}
