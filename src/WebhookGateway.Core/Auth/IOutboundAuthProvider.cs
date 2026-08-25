using WebhookGateway.Core.Domain;

namespace WebhookGateway.Core.Auth;

/// <summary>
/// Aplica la autenticación a una petición saliente. Una implementación por
/// <see cref="OutboundAuthType"/>; se resuelven por <see cref="Type"/>.
/// </summary>
public interface IOutboundAuthProvider
{
    OutboundAuthType Type { get; }

    /// <summary>
    /// Modifica <paramref name="request"/> añadiendo lo que haga falta: cabeceras,
    /// parámetros de query o una firma.
    /// </summary>
    /// <param name="body">
    /// El cuerpo que se va a enviar. Lo necesitan las estrategias que firman; las demás
    /// lo ignoran.
    /// </param>
    /// <param name="endpointId">
    /// Identifica al destino. Lo usa OAuth2 para cachear el token por endpoint en vez de
    /// globalmente.
    /// </param>
    ValueTask ApplyAsync(
        HttpRequestMessage request,
        OutboundAuthConfig config,
        ReadOnlyMemory<byte> body,
        int endpointId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Invalida cualquier credencial cacheada para este destino. El despachador la llama
    /// tras un 401 para forzar un único reintento con credenciales frescas.
    /// Las estrategias sin estado no hacen nada.
    /// </summary>
    ValueTask InvalidateAsync(int endpointId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
