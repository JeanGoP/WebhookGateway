using WebhookGateway.Data.Traffic;

namespace WebhookGateway.Api.Panel;

/// <summary>Explorador de mensajes (solo lectura): <c>/api/messages</c>.</summary>
public static class MessageEndpoints
{
    public static void MapMessages(this WebApplication app)
    {
        var group = app.MapGroup("/api/messages")
            .WithTags("Messages")
            .RequireAuthorization();

        group.MapGet("/", SearchAsync)
            .Produces<IReadOnlyList<MessageSummaryDto>>();

        group.MapGet("/{id:long}", GetMessageAsync)
            .Produces<MessageDetailDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        group.MapGet("/{id:long}/deliveries", GetDeliveriesAsync)
            .Produces<IReadOnlyList<DeliveryDto>>();

        group.MapGet("/{id:long}/body", GetBodyAsync)
            .Produces<MessageBodyDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> SearchAsync(
        int? inboundEndpointId, byte? status,
        DateTime? from, DateTime? to,
        long? afterId, int? pageSize,
        MessageExplorer explorer, CancellationToken ct)
    {
        var query = new MessageSearchQuery(inboundEndpointId, status, from, to, afterId, pageSize);
        var results = await explorer.SearchAsync(query, ct);

        return Results.Ok(results.Select(m => m.ToDto()).ToList());
    }

    private static async Task<IResult> GetMessageAsync(
        long id, MessageExplorer explorer, CancellationToken ct)
    {
        var message = await explorer.GetMessageAsync(id, ct);

        return message is null
            ? Results.NotFound(new ErrorResponse("Mensaje no encontrado."))
            : Results.Ok(message.ToDto());
    }

    private static async Task<IResult> GetDeliveriesAsync(
        long id, MessageExplorer explorer, CancellationToken ct)
    {
        var deliveries = await explorer.GetDeliveriesAsync(id, ct);

        return Results.Ok(deliveries.Select(d => d.ToDto()).ToList());
    }

    /*
        El cuerpo va aparte del detalle a propósito: el listado y la ficha del mensaje se
        abren siempre, y el cuerpo solo cuando alguien lo pide. Cargarlo en el detalle
        obligaría a leer la tabla de payloads en cada vista.
    */
    private static async Task<IResult> GetBodyAsync(
        long id, MessageExplorer explorer, MessagePayloadReader reader, CancellationToken ct)
    {
        // Hace falta el ReceivedAt para localizar el cuerpo con un seek sobre la clave
        // agrupada (ReceivedAt, MessageId) en lugar de recorrer todas las particiones.
        var message = await explorer.GetMessageAsync(id, ct);

        if (message is null)
        {
            return Results.NotFound(new ErrorResponse("Mensaje no encontrado."));
        }

        var payload = await reader.LoadAsync(id, message.ReceivedAt, ct);

        // Que el cuerpo ya no esté no es un fallo: su retención es más corta que la de la
        // metadata a propósito. El panel debe poder decirlo con esas palabras.
        return payload is null
            ? Results.NotFound(new ErrorResponse(
                "El cuerpo de este mensaje ya se purgó. La metadata se conserva más tiempo que el cuerpo."))
            : Results.Ok(MessageBodyFactory.From(id, payload));
    }
}
