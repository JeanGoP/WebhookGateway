using System.Net;
using System.Text;
using NSubstitute;
using Shouldly;
using WebhookGateway.Core.Auth;
using WebhookGateway.Core.Auth.Providers;
using WebhookGateway.Core.Delivery;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data.Traffic;
using WebhookGateway.Dispatcher.Sending;
using Xunit;

namespace WebhookGateway.UnitTests;

/// <summary>
/// Qué se guarda de la respuesta del destino. La respuesta solo interesa cuando el intento salió
/// mal: a 400.000 entregas al día, casi todas buenas, el cuerpo y las cabeceras de los 200 son la
/// mayor parte de lo que ocupa <c>DeliveryAttempt</c>, la tabla de más volumen, y nadie los lee.
/// </summary>
public sealed class DeliverySenderTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task Un_intento_bueno_no_guarda_la_respuesta(HttpStatusCode status)
    {
        var sender = SenderReturning(status, "{\"ok\":true}");

        var result = await sender.SendAsync(Target(), Payload(), CancellationToken.None);

        result.StatusCode.ShouldBe((int)status);
        result.ResponseBody.ShouldBeNull();
        result.ResponseHeadersJson.ShouldBeNull();
        result.ErrorMessage.ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]          // permanente: el problema es este mensaje
    [InlineData(HttpStatusCode.InternalServerError)] // transitorio
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Un_intento_fallido_si_guarda_la_respuesta(HttpStatusCode status)
    {
        var sender = SenderReturning(status, "el motivo del rechazo");

        var result = await sender.SendAsync(Target(), Payload(), CancellationToken.None);

        result.StatusCode.ShouldBe((int)status);
        result.ResponseBody.ShouldBe("el motivo del rechazo");
        result.ResponseHeadersJson.ShouldNotBeNull();
    }

    /// <summary>
    /// Un 4xx es justo el caso que alguien va a abrir en el panel para entender por qué el destino
    /// rechazó el mensaje. Si no se guardara el cuerpo, no habría nada que mirar.
    /// </summary>
    [Fact]
    public async Task El_cuerpo_de_un_rechazo_llega_entero_al_registro()
    {
        const string motivo = "{\"error\":\"falta el campo documento\"}";
        var sender = SenderReturning(HttpStatusCode.UnprocessableEntity, motivo);

        var result = await sender.SendAsync(Target(), Payload(), CancellationToken.None);

        result.ResponseBody.ShouldBe(motivo);
    }

    [Fact]
    public async Task Un_error_de_red_no_tiene_codigo_ni_respuesta_pero_si_motivo()
    {
        var sender = SenderThrowing(new HttpRequestException("no se pudo resolver el host"));

        var result = await sender.SendAsync(Target(), Payload(), CancellationToken.None);

        result.StatusCode.ShouldBeNull();
        result.ResponseBody.ShouldBeNull();
        result.ResponseHeadersJson.ShouldBeNull();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("no se pudo resolver el host");
    }

    // ---- Andamiaje ----

    private static DeliverySender SenderReturning(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("X-Destino", "prueba");

        return SenderWith(new StubHandler(response));
    }

    private static DeliverySender SenderThrowing(Exception ex) => SenderWith(new StubHandler(ex));

    private static DeliverySender SenderWith(StubHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(DeliverySender.ClientName)
            .Returns(_ => new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });

        return new DeliverySender(factory, [new NoOutboundAuthProvider()], TimeProvider.System);
    }

    private static OutgoingPayload Payload() =>
        new(Encoding.UTF8.GetBytes("{}"), "application/json");

    private static OutboundTarget Target() =>
        new(
            Id: 1,
            IntegrationId: 1,
            IntegrationName: "Pruebas",
            EndpointName: "Destino",
            TargetUrl: new Uri("https://ejemplo.invalid/hook"),
            Method: HttpMethod.Post,
            AuthType: OutboundAuthType.None,
            AuthConfig: new NoOutboundAuth(),
            CustomHeaders: new Dictionary<string, string>(),
            RateLimitPerMinute: 600,
            MaxConcurrency: 4,
            Timeout: TimeSpan.FromSeconds(30),
            RetryPolicy: RetryPolicy.Default,
            BreakerFailureThreshold: 5,
            BreakerOpenSeconds: 60);

    /// <summary>Devuelve siempre lo mismo, o lanza siempre lo mismo. Nada de red.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage? _response;
        private readonly Exception? _throw;

        public StubHandler(HttpResponseMessage response) => _response = response;

        public StubHandler(Exception toThrow) => _throw = toThrow;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            _throw is not null
                ? Task.FromException<HttpResponseMessage>(_throw)
                : Task.FromResult(_response!);
    }
}
