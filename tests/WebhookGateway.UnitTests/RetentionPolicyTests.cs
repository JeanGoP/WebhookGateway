using Shouldly;
using WebhookGateway.Core.Domain;
using Xunit;

namespace WebhookGateway.UnitTests;

/// <summary>
/// Lo que se cambia en la pantalla de retención decide qué borra la purga, y lo borrado no vuelve.
/// Estas reglas impiden que una retención mal puesta se lleve algo que todavía hace falta.
/// </summary>
public sealed class RetentionPolicyTests
{
    private const int VentanaDeTresDias = 72;

    [Fact]
    public void Los_valores_por_defecto_son_validos()
    {
        RetentionPolicy.Check(180, 30, 30, VentanaDeTresDias).ShouldBeNull();
    }

    [Theory]
    [InlineData(0, 30, 30)]
    [InlineData(180, 0, 30)]
    [InlineData(180, 30, 0)]
    [InlineData(RetentionPolicy.MaxDays + 1, 30, 30)]
    public void Cada_retencion_tiene_que_estar_en_rango(int metadata, int payload, int attempts)
    {
        RetentionPolicy.Check(metadata, payload, attempts, VentanaDeTresDias).ShouldNotBeNull();
    }

    [Fact]
    public void Los_cuerpos_no_pueden_durar_menos_que_la_ventana_de_entrega_mas_larga()
    {
        // Una ventana de 7 días con cuerpos de 5: la purga borraría cuerpos de entregas que aún se
        // reintentan, y esas entregas ya no se podrían enviar.
        RetentionPolicy.Check(180, 5, 5, longestDeliveryWindowHours: 7 * 24).ShouldNotBeNull();
        RetentionPolicy.Check(180, 7, 7, longestDeliveryWindowHours: 7 * 24).ShouldBeNull();
    }

    [Fact]
    public void Los_mensajes_no_pueden_durar_menos_que_sus_cuerpos()
    {
        RetentionPolicy.Check(20, 30, 10, VentanaDeTresDias).ShouldNotBeNull();
    }

    [Fact]
    public void Los_intentos_no_pueden_durar_mas_que_sus_entregas()
    {
        RetentionPolicy.Check(60, 30, 90, VentanaDeTresDias).ShouldNotBeNull();
    }
}
