using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WebhookGateway.Core.Auth;
using WebhookGateway.Core.Delivery;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data.Traffic;

namespace WebhookGateway.Dispatcher.Sending;

/// <summary>Lo que ocurrió en un intento HTTP, sin interpretar todavía.</summary>
public sealed record SendResult(
    int? StatusCode,
    string? ResponseHeadersJson,
    string? ResponseBody,
    string? ErrorMessage,
    TimeSpan? RetryAfter);

/// <summary>
/// Manda una entrega a su destino. El cuerpo viaja tal cual llegó: el gateway no transforma
/// nada, y esa línea es lo único que lo separa de convertirse en un motor de integración.
/// </summary>
public sealed class DeliverySender(
    IHttpClientFactory httpClientFactory,
    IEnumerable<IOutboundAuthProvider> providers,
    TimeProvider clock)
{
    /// <summary>Nombre del cliente configurado en <see cref="DependencyInjection"/>.</summary>
    public const string ClientName = "outbound";

    /// <summary>Se lee acotado: un destino no puede hacernos tragar una respuesta enorme.</summary>
    private const int MaxResponseBytes = 8 * 1024;

    private readonly Dictionary<OutboundAuthType, IOutboundAuthProvider> _providers =
        providers.ToDictionary(p => p.Type);

    public async Task<SendResult> SendAsync(OutboundTarget target, OutgoingPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(payload);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(target.Timeout);

        // Se pide a la fábrica en cada envío para que rote los manejadores y no se quede
        // pegado a una IP cuando el destino cambia de DNS.
        var httpClient = httpClientFactory.CreateClient(ClientName);

        try
        {
            using var request = await BuildRequestAsync(target, payload, timeout.Token);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            // Un 401 puede significar credencial caducada. Se invalida para que el siguiente
            // intento pida una nueva; las estrategias sin estado no hacen nada.
            if (response.StatusCode == HttpStatusCode.Unauthorized &&
                _providers.TryGetValue(target.AuthType, out var provider))
            {
                await provider.InvalidateAsync(target.Id, cancellationToken);
            }

            return new SendResult(
                (int)response.StatusCode,
                SerializeHeaders(response),
                await ReadBoundedAsync(response, timeout.Token),
                null,
                AttemptClassifier.ParseRetryAfter(HeaderValue(response, "Retry-After"), clock.GetUtcNow()));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Se agotó el tiempo del destino, no nos están apagando a nosotros.
            return new SendResult(null, null, null, $"Tiempo de espera agotado tras {target.Timeout.TotalSeconds:0} s.", null);
        }
        catch (HttpRequestException ex)
        {
            return new SendResult(null, null, null, $"Error de red: {ex.Message}", null);
        }
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(
        OutboundTarget target, OutgoingPayload payload, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(target.Method, target.TargetUrl)
        {
            Content = new ReadOnlyMemoryContent(payload.Body),
        };

        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(payload.ContentType);

        // Las cabeceras fijas son ortogonales a la autenticación: un destino puede pedir
        // Bearer y además un X-Tenant-Id. Se aplican antes para que la firma, si la hay,
        // sea lo último y nadie la pise.
        foreach (var (name, value) in target.CustomHeaders)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (_providers.TryGetValue(target.AuthType, out var provider))
        {
            await provider.ApplyAsync(request, target.AuthConfig, payload.Body, target.Id, cancellationToken);
        }

        return request;
    }

    private static string? HeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string SerializeHeaders(HttpResponseMessage response)
    {
        var headers = response.Headers
            .Concat(response.Content.Headers)
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);

        var json = JsonSerializer.Serialize(headers);

        return json.Length <= 4000 ? json : json[..4000];
    }

    private static async Task<string?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxResponseBytes];
        var read = await stream.ReadAtLeastAsync(buffer, MaxResponseBytes, throwOnEndOfStream: false, cancellationToken);

        return read == 0
            ? null
            : DeliveryAttempt.TruncateResponse(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
    }
}
