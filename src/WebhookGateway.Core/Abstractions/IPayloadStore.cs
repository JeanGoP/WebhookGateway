using WebhookGateway.Core.Domain;

namespace WebhookGateway.Core.Abstractions;

/// <summary>
/// Dónde quedó el cuerpo de un mensaje. O bien inline en SQL (<paramref name="Body"/>),
/// o bien en almacenamiento externo (<paramref name="StorageRef"/>). Exactamente uno de
/// los dos tiene valor.
/// </summary>
public readonly record struct StoredPayload(
    PayloadEncoding Encoding,
    int SizeBytes,
    byte[]? Body,
    string? StorageRef)
{
    public bool IsExternal => StorageRef is not null;
}

/// <summary>
/// Guarda y recupera cuerpos de mensajes.
/// </summary>
/// <remarks>
/// F1 entrega solo la implementación inline. La indirección existe desde el primer día
/// para que soportar adjuntos grandes en F5 sea añadir una implementación y no migrar el
/// esquema.
/// <para>
/// Cuando llegue la implementación externa, el orden importa: primero se escribe el blob,
/// después se inserta la fila. Al revés se pierde el cuerpo de un mensaje ya confirmado.
/// La contrapartida es que hace falta recoger blobs huérfanos.
/// </para>
/// </remarks>
public interface IPayloadStore
{
    /// <summary>
    /// Persiste el cuerpo y devuelve cómo recuperarlo. Debe completar antes de que la
    /// recepción responda 2xx.
    /// </summary>
    Task<StoredPayload> SaveAsync(
        long messageId,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken);

    /// <summary>
    /// Recupera el cuerpo ya descomprimido, listo para enviar.
    /// </summary>
    Task<ReadOnlyMemory<byte>> LoadAsync(StoredPayload payload, CancellationToken cancellationToken);

    /// <summary>
    /// Borra un cuerpo externo. No hace nada para los inline: esos se van con la partición.
    /// </summary>
    Task DeleteAsync(StoredPayload payload, CancellationToken cancellationToken);
}
