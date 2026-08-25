using System.IO.Compression;
using WebhookGateway.Core.Abstractions;
using WebhookGateway.Core.Domain;

namespace WebhookGateway.Data.Payloads;

/// <summary>
/// Guarda los cuerpos comprimidos dentro de SQL Server.
/// </summary>
/// <remarks>
/// Es la única implementación que necesita la v1: los payloads van de 1 a 10 KB y gzip
/// les quita en torno al 85 %, así que un año de historial cabe en un par de gigas.
/// <para>
/// La implementación externa para adjuntos grandes llega en F5 y solo tiene que
/// implementar esta misma interfaz. Cuando llegue, el orden importa: primero se escribe
/// el blob y después la fila, nunca al revés, porque un cuerpo perdido en un mensaje ya
/// confirmado sí es pérdida de datos.
/// </para>
/// </remarks>
public sealed class GzipPayloadStore : IPayloadStore
{
    /// <summary>
    /// Por debajo de esto no se comprime: la cabecera de gzip pesa más que lo que ahorra.
    /// </summary>
    private const int CompressionThreshold = 512;

    public Task<StoredPayload> SaveAsync(long messageId, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        var original = body.Length;

        if (original < CompressionThreshold)
        {
            return Task.FromResult(new StoredPayload(PayloadEncoding.Raw, original, body.ToArray(), null));
        }

        var compressed = Compress(body.Span);

        // Un cuerpo ya comprimido o con mucha entropía puede crecer. Si pasa, se deja crudo.
        return Task.FromResult(compressed.Length < original
            ? new StoredPayload(PayloadEncoding.Gzip, original, compressed, null)
            : new StoredPayload(PayloadEncoding.Raw, original, body.ToArray(), null));
    }

    public Task<ReadOnlyMemory<byte>> LoadAsync(StoredPayload payload, CancellationToken cancellationToken)
    {
        if (payload.IsExternal)
        {
            throw new NotSupportedException(
                "Este cuerpo está en almacenamiento externo y aquí solo se manejan los inline. " +
                "Registra un IPayloadStore externo para poder leerlo.");
        }

        if (payload.Body is null)
        {
            throw new InvalidOperationException($"El cuerpo está vacío y no tiene referencia externa.");
        }

        return Task.FromResult<ReadOnlyMemory<byte>>(payload.Encoding switch
        {
            PayloadEncoding.Raw => payload.Body,
            PayloadEncoding.Gzip => Decompress(payload.Body, payload.SizeBytes),
            _ => throw new InvalidOperationException($"Codificación de cuerpo desconocida: {payload.Encoding}."),
        });
    }

    /// <summary>Los cuerpos inline se van con su partición; no hay nada que borrar.</summary>
    public Task DeleteAsync(StoredPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

    private static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream(data.Length / 2);

        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data, int expectedSize)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expectedSize > 0 ? expectedSize : data.Length * 4);

        gzip.CopyTo(output);

        return output.ToArray();
    }
}
