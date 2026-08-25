using System.Data.Common;
using Microsoft.Data.SqlClient;
using WebhookGateway.Core.Auth;
using WebhookGateway.Core.Common;

namespace WebhookGateway.Api.Reception;

/// <summary>Superficie HTTP de <c>/in/*</c>. La lógica vive en <see cref="InboundMessageReceiver"/>.</summary>
public static class ReceptionEndpoints
{
    // Techo de seguridad del proceso, independiente del límite configurable por endpoint
    // (InboundEndpoint.MaxBodyBytes, 1 MB por defecto). Este es el que protege la memoria
    // cuando el endpoint ni siquiera se ha resuelto todavía.
    private const int GlobalMaxBodyBytes = 10 * 1024 * 1024;

    public static void MapReception(this WebApplication app) =>
        app.MapPost("/in/{integration}/{endpoint}", HandleAsync);

    private static async Task<IResult> HandleAsync(
        string integration,
        string endpoint,
        HttpRequest httpRequest,
        InboundMessageReceiver receiver,
        CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(httpRequest, cancellationToken);
        if (body is null)
        {
            return Results.Problem("El cuerpo supera el máximo global admitido.", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var request = new InboundRequest(
            Headers: httpRequest.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            Query: httpRequest.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            Body: body.Value,
            Method: httpRequest.Method,
            Path: httpRequest.Path.ToString(),
            SourceIp: httpRequest.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        try
        {
            var result = await receiver.ReceiveAsync(integration, endpoint, request, cancellationToken);

            return result.Match(
                onSuccess: outcome => Results.Accepted(value: new
                {
                    messageId = outcome.MessageId,
                    status = outcome.Status.ToString(),
                    deliveries = outcome.DeliveryCount,
                }),
                onFailure: MapFailure);
        }
        catch (Exception ex) when (ex is SqlException or DbException or TimeoutException)
        {
            // La regla dura: nunca 2xx sin haber persistido. Mejor que el emisor reintente
            // a que crea que el webhook llegó cuando en realidad se perdió.
            httpRequest.HttpContext.Response.Headers["Retry-After"] = "5";
            return Results.Problem(
                "El sistema no puede persistir en este momento. Reintenta en unos segundos.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IResult MapFailure(Failure error) => error.Code switch
    {
        "reception.not_found" => Results.NotFound(new { error = error.Message }),
        "reception.body_too_large" => Results.Problem(error.Message, statusCode: StatusCodes.Status413PayloadTooLarge),
        _ when error.Code.StartsWith("auth.", StringComparison.Ordinal) =>
            Results.Problem(error.Message, statusCode: StatusCodes.Status401Unauthorized),
        _ => Results.Problem(error.Message, statusCode: StatusCodes.Status500InternalServerError),
    };

    private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;

        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > GlobalMaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
