using Shouldly;
using WebhookGateway.Core.Auth;
using WebhookGateway.Core.Delivery;
using WebhookGateway.Core.Domain;
using WebhookGateway.Dispatcher.Sending;
using WebhookGateway.Dispatcher.Throttling;
using Xunit;

namespace WebhookGateway.UnitTests;

/// <summary>
/// El freno por destino. Esto es el producto: absorber un pico y drenarlo al ritmo que el destino
/// aguanta es exactamente para lo que existe el gateway.
/// </summary>
public sealed class EndpointThrottlesTests
{
    private static OutboundTarget Target(int id = 1, int maxConcurrency = 4, int ratePerMinute = 600) =>
        new(
            Id: id,
            IntegrationId: 1,
            IntegrationName: "Pruebas",
            EndpointName: "Destino",
            TargetUrl: new Uri("https://ejemplo.invalid/hook"),
            Method: HttpMethod.Post,
            AuthType: OutboundAuthType.None,
            AuthConfig: new NoOutboundAuth(),
            CustomHeaders: new Dictionary<string, string>(),
            RateLimitPerMinute: ratePerMinute,
            MaxConcurrency: maxConcurrency,
            Timeout: TimeSpan.FromSeconds(30),
            RetryPolicy: RetryPolicy.Default,
            BreakerFailureThreshold: 5,
            BreakerOpenSeconds: 60);

    [Fact]
    public async Task Concede_turno_cuando_el_destino_tiene_hueco()
    {
        using var throttles = new EndpointThrottles();

        using var turn = await throttles.AcquireAsync(Target(), CancellationToken.None);

        turn.IsAcquired.ShouldBeTrue();
    }

    /// <summary>
    /// Con un solo hueco de concurrencia, el segundo turno espera. Es lo que impide mandarle a un
    /// destino más peticiones simultáneas de las que dijo que aguanta.
    /// </summary>
    [Fact]
    public async Task El_segundo_turno_espera_a_que_se_libere_el_primero()
    {
        using var throttles = new EndpointThrottles();
        var target = Target(maxConcurrency: 1);

        var primero = await throttles.AcquireAsync(target, CancellationToken.None);
        primero.IsAcquired.ShouldBeTrue();

        var segundo = throttles.AcquireAsync(target, CancellationToken.None);

        // Sigue esperando mientras el primero no suelte.
        (await Task.WhenAny(segundo, Task.Delay(250))).ShouldNotBe(segundo);

        primero.Dispose();

        using var concedido = await segundo;
        concedido.IsAcquired.ShouldBeTrue();
    }

    /// <summary>
    /// Cada destino tiene su propio freno: que uno esté saturado no puede dejar a otro esperando.
    /// Es la misma propiedad que ahora sostiene el avance independiente por destino.
    /// </summary>
    [Fact]
    public async Task Un_destino_saturado_no_frena_a_otro()
    {
        using var throttles = new EndpointThrottles();

        var saturado = await throttles.AcquireAsync(Target(id: 1, maxConcurrency: 1), CancellationToken.None);
        saturado.IsAcquired.ShouldBeTrue();

        using var otro = await throttles.AcquireAsync(Target(id: 2, maxConcurrency: 1), CancellationToken.None);

        otro.IsAcquired.ShouldBeTrue();

        saturado.Dispose();
    }

    [Fact]
    public async Task Sin_limite_de_ritmo_solo_queda_el_de_concurrencia()
    {
        using var throttles = new EndpointThrottles();

        using var turn = await throttles.AcquireAsync(
            Target(ratePerMinute: 0, maxConcurrency: 2), CancellationToken.None);

        turn.IsAcquired.ShouldBeTrue();
    }

    /// <summary>
    /// Cambiar la configuración en el panel tiene que notarse sin reiniciar: el freno viejo se
    /// desecha y el siguiente turno sale del nuevo.
    /// </summary>
    [Fact]
    public async Task Cambiar_la_configuracion_del_destino_rehace_su_freno()
    {
        using var throttles = new EndpointThrottles();

        var antes = await throttles.AcquireAsync(Target(maxConcurrency: 1), CancellationToken.None);
        antes.IsAcquired.ShouldBeTrue();

        // Mismo destino, otro ritmo: el freno se reemplaza, así que hay hueco otra vez aunque el
        // turno anterior siga sin liberarse.
        using var despues = await throttles.AcquireAsync(
            Target(maxConcurrency: 1, ratePerMinute: 120), CancellationToken.None);

        despues.IsAcquired.ShouldBeTrue();

        antes.Dispose();
    }

    /// <summary>
    /// La entrega que estaba en vuelo cuando se cambió la configuración tiene que poder liberar su
    /// turno sin más. Antes no: el freno viejo se desechaba con su semáforo, y liberar ese turno
    /// lanzaba <see cref="ObjectDisposedException"/>. Como el <c>using</c> del despachador cae
    /// dentro de su try/catch, el síntoma era un error en el log por cada entrega en vuelo cada vez
    /// que alguien tocaba el ritmo de un destino en el panel.
    /// </summary>
    [Fact]
    public async Task Liberar_un_turno_de_un_freno_ya_reemplazado_no_revienta()
    {
        using var throttles = new EndpointThrottles();

        var enVuelo = await throttles.AcquireAsync(Target(ratePerMinute: 600), CancellationToken.None);
        enVuelo.IsAcquired.ShouldBeTrue();

        // Alguien cambia el destino en el panel mientras esa entrega sigue en vuelo.
        using var nuevo = await throttles.AcquireAsync(Target(ratePerMinute: 90), CancellationToken.None);

        Should.NotThrow(() => enVuelo.Dispose());
    }

    /// <summary>
    /// Un turno liberado devuelve el hueco. Si no lo hiciera, el destino se quedaría mudo tras unas
    /// cuantas entregas: es el fallo más fácil de cometer aquí y el más difícil de ver.
    /// </summary>
    [Fact]
    public async Task Liberar_el_turno_devuelve_el_hueco()
    {
        using var throttles = new EndpointThrottles();
        var target = Target(maxConcurrency: 2);

        for (var i = 0; i < 10; i++)
        {
            using var turn = await throttles.AcquireAsync(target, CancellationToken.None);
            turn.IsAcquired.ShouldBeTrue();
        }
    }
}
