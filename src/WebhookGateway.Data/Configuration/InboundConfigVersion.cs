namespace WebhookGateway.Data.Configuration;

/// <summary>
/// Marca de versión de la configuración de entrada. Va dentro de la clave de caché de
/// <see cref="InboundEndpointLookup"/>: cuando el panel cambia algo, se incrementa y toda entrada
/// anterior queda inalcanzable de golpe.
///
/// Se hace así y no borrando claves concretas porque lo que afecta a una ruta está repartido:
/// el endpoint, su integración, sus suscripciones y el estado de cada destino. Averiguar qué
/// rutas toca un cambio en un destino obligaría a consultar la base en cada edición, y las
/// ediciones son puntuales mientras las recepciones son constantes. Tirar la caché entera cuesta
/// unas pocas consultas de más una vez.
///
/// Limitación conocida: la marca es de este proceso. Durante un despliegue hay dos instancias
/// vivas, y la que no atendió la edición sigue con su caché hasta que venza el TTL. Por eso el
/// TTL se mantiene corto en vez de ser indefinido.
/// </summary>
public sealed class InboundConfigVersion
{
    private long _value;

    public long Current => Interlocked.Read(ref _value);

    /// <summary>Invalida la caché de entrada. Se llama tras guardar un cambio en el panel.</summary>
    public void Invalidate() => Interlocked.Increment(ref _value);
}
