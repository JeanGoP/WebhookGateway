using Shouldly;
using WebhookGateway.Dispatcher.Notifications;
using Xunit;

namespace WebhookGateway.UnitTests;

/// <summary>
/// La agrupación de avisos de entrega descartada. Un destino que responde 400 a todo descarta una
/// entrega por mensaje: sin agrupar, un pico de 2.000 mensajes son 2.000 correos a cada
/// destinatario, que nadie lee y que ahogan los avisos de los destinos que sí importan.
/// </summary>
public sealed class DeadLetterWindowsTests
{
    private static readonly TimeSpan Ventana = TimeSpan.FromMinutes(15);

    private sealed class RelojMovible(DateTimeOffset inicio) : TimeProvider
    {
        private DateTimeOffset _ahora = inicio;

        public override DateTimeOffset GetUtcNow() => _ahora;

        public void Avanza(TimeSpan cuanto) => _ahora += cuanto;
    }

    private static RelojMovible Reloj() => new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void El_primer_aviso_pasa_y_no_tiene_nada_callado_detras()
    {
        var windows = new DeadLetterWindows(Reloj());

        windows.TryClaimAlert(endpointId: 1, Ventana).ShouldBe(0);
    }

    [Fact]
    public void Dentro_de_la_ventana_se_calla()
    {
        var windows = new DeadLetterWindows(Reloj());

        windows.TryClaimAlert(1, Ventana).ShouldBe(0);
        windows.TryClaimAlert(1, Ventana).ShouldBeNull();
        windows.TryClaimAlert(1, Ventana).ShouldBeNull();
    }

    /// <summary>
    /// Callar no es perder: el siguiente aviso dice cuántas hubo mientras callaba, que es la cifra
    /// que de verdad hace falta para saber el tamaño del problema.
    /// </summary>
    [Fact]
    public void Al_vencer_la_ventana_el_aviso_dice_cuantas_se_callaron()
    {
        var reloj = Reloj();
        var windows = new DeadLetterWindows(reloj);

        windows.TryClaimAlert(1, Ventana).ShouldBe(0);

        for (var i = 0; i < 42; i++)
        {
            windows.TryClaimAlert(1, Ventana).ShouldBeNull();
        }

        reloj.Avanza(Ventana + TimeSpan.FromSeconds(1));

        windows.TryClaimAlert(1, Ventana).ShouldBe(42);
    }

    [Fact]
    public void La_cuenta_se_reinicia_tras_avisar()
    {
        var reloj = Reloj();
        var windows = new DeadLetterWindows(reloj);

        windows.TryClaimAlert(1, Ventana);
        windows.TryClaimAlert(1, Ventana);
        reloj.Avanza(Ventana + TimeSpan.FromSeconds(1));

        windows.TryClaimAlert(1, Ventana).ShouldBe(1);

        reloj.Avanza(Ventana + TimeSpan.FromSeconds(1));

        windows.TryClaimAlert(1, Ventana).ShouldBe(0);
    }

    /// <summary>
    /// La ventana es por destino. Que uno esté descartando a mansalva no puede silenciar el primer
    /// aviso de otro, que es justo el que hay que ver.
    /// </summary>
    [Fact]
    public void La_ventana_es_por_destino()
    {
        var windows = new DeadLetterWindows(Reloj());

        windows.TryClaimAlert(1, Ventana).ShouldBe(0);
        windows.TryClaimAlert(1, Ventana).ShouldBeNull();

        windows.TryClaimAlert(2, Ventana).ShouldBe(0);
        windows.TryClaimAlert(3, Ventana).ShouldBe(0);
    }

    [Fact]
    public void Una_ventana_de_cero_avisa_siempre()
    {
        var windows = new DeadLetterWindows(Reloj());

        for (var i = 0; i < 5; i++)
        {
            windows.TryClaimAlert(1, TimeSpan.Zero).ShouldBe(0);
        }
    }

    [Fact]
    public void Lo_callado_se_puede_consultar_sin_avisar()
    {
        var windows = new DeadLetterWindows(Reloj());

        windows.TryClaimAlert(1, Ventana);
        windows.TryClaimAlert(1, Ventana);
        windows.TryClaimAlert(1, Ventana);

        windows.SuppressedFor(1).ShouldBe(2);
        windows.SuppressedFor(99).ShouldBe(0);
    }

    /// <summary>
    /// Varias entregas del mismo destino se descartan a la vez, en paralelo, una por cada bomba.
    /// Exactamente una tiene que llevarse el aviso y el resto contarse.
    /// </summary>
    [Fact]
    public void Bajo_concurrencia_solo_uno_se_lleva_el_aviso()
    {
        var windows = new DeadLetterWindows(Reloj());
        const int total = 200;

        var resultados = new int?[total];

        Parallel.For(0, total, i => resultados[i] = windows.TryClaimAlert(1, Ventana));

        resultados.Count(r => r is not null).ShouldBe(1);
        windows.SuppressedFor(1).ShouldBe(total - 1);
    }
}
