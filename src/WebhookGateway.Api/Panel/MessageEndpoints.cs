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
        int? integrationId, int? inboundEndpointId, byte? status,
        DateTime? from, DateTime? to,
        long? afterId, int? pageSize,
        MessageExplorer explorer, CancellationToken ct)
    {
        var query = new MessageSearchQuery(integrationId, inboundEndpointId, status, from, to, afterId, pageSize);
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
        long id, MessageExplorer explorer, MessagePayloadReader reader,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        // Se consulta el mensaje para poder distinguir las dos ausencias: que no exista el
        // mensaje y que exista pero sin cuerpo. El lector del cuerpo localiza la fecha por su
        // cuenta, así que ya no depende de lo que devuelva esta consulta.
        var message = await explorer.GetMessageAsync(id, ct);

        if (message is null)
        {
            return Results.NotFound(new ErrorResponse("Mensaje no encontrado."));
        }

        var payload = await reader.LoadAsync(id, ct);

        /*
            Que el cuerpo ya no esté puede ser normal —su retención es más corta que la de la
            metadata— o puede ser un fallo. Distinguirlo desde fuera es imposible, así que
            queda registrado: si esto aparece para un mensaje reciente, no se purgó nada y hay
            algo que mirar.
        */
        if (payload is null)
        {
            loggerFactory
                .CreateLogger("WebhookGateway.Api.Panel.Messages")
                .LogWarning(
                    "Sin cuerpo para el mensaje {MessageId}, recibido el {ReceivedAt:O}. La metadata está, pero la consulta no devolvió fila de WebhookPayload.",
                    id, message.ReceivedAt);

            return Results.NotFound(new ErrorResponse(
                "El cuerpo de este mensaje no está en la base de datos. Si el mensaje es reciente no se ha purgado: revisa el registro del servidor."));
        }

        return Results.Ok(MessageBodyFactory.From(id, payload));
    }
}
