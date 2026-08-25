using System.Threading.Channels;
using WebhookGateway.Core.Abstractions;

namespace WebhookGateway.Dispatcher.Queue;

/// <summary>
/// Implementación en memoria de <see cref="IDeliveryQueue"/> sobre un canal acotado.
/// </summary>
/// <remarks>
/// El límite existe para no acumular memoria sin freno durante una ráfaga: si se llena, se
/// descarta la señal y el barredor periódico del despachador (F2) recoge esas entregas
/// directamente de SQL. Perder la señal solo retrasa el primer intento, nunca el mensaje.
/// </remarks>
public sealed class ChannelDeliveryQueue : IDeliveryQueue
{
    private readonly Channel<long> _channel = Channel.CreateBounded<long>(new BoundedChannelOptions(10_000)
    {
        SingleReader = false,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    public void TryEnqueue(IReadOnlyCollection<long> deliveryIds)
    {
        foreach (var id in deliveryIds)
        {
            _channel.Writer.TryWrite(id);
        }
    }

    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public void Complete() => _channel.Writer.TryComplete();
}
