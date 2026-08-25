using System.Net.Http.Headers;
using System.Text;
using WebhookGateway.Core.Domain;

namespace WebhookGateway.Core.Auth.Providers;

/// <summary>El destino no pide nada. No toca la petición.</summary>
public sealed class NoOutboundAuthProvider : IOutboundAuthProvider
{
    public OutboundAuthType Type => OutboundAuthType.None;

    public ValueTask ApplyAsync(
        HttpRequestMessage request, OutboundAuthConfig config, ReadOnlyMemory<byte> body,
        int endpointId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>
/// Clave de API en cabecera o en la query string. El nombre del parámetro es configuración:
/// unos destinos esperan <c>X-API-Key</c>, otros <c>Api-Key</c> y otros <c>?api_key=</c>.
/// </summary>
public sealed class ApiKeyOutboundProvider : IOutboundAuthProvider
{
    public OutboundAuthType Type => OutboundAuthType.ApiKey;

    public ValueTask ApplyAsync(
        HttpRequestMessage request, OutboundAuthConfig config, ReadOnlyMemory<byte> body,
        int endpointId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var auth = (ApiKeyOutboundAuth)config;
        var value = auth.ValuePrefix + auth.Value;

        if (auth.Location == ApiKeyLocation.Header)
        {
            request.Headers.TryAddWithoutValidation(auth.ParameterName, value);
            return ValueTask.CompletedTask;
        }

        var builder = new UriBuilder(request.RequestUri!);
        var existing = builder.Query.TrimStart('?');
        var appended = $"{Uri.EscapeDataString(auth.ParameterName)}={Uri.EscapeDataString(value)}";

        builder.Query = existing.Length == 0 ? appended : $"{existing}&{appended}";
        request.RequestUri = builder.Uri;

        return ValueTask.CompletedTask;
    }
}

/// <summary>Usuario y contraseña en <c>Authorization: Basic</c>.</summary>
public sealed class BasicOutboundProvider : IOutboundAuthProvider
{
    public OutboundAuthType Type => OutboundAuthType.Basic;

    public ValueTask ApplyAsync(
        HttpRequestMessage request, OutboundAuthConfig config, ReadOnlyMemory<byte> body,
        int endpointId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var auth = (BasicOutboundAuth)config;
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password}"));

        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        return ValueTask.CompletedTask;
    }
}

/// <summary>Token fijo en <c>Authorization: Bearer</c>.</summary>
public sealed class BearerOutboundProvider : IOutboundAuthProvider
{
    public OutboundAuthType Type => OutboundAuthType.Bearer;

    public ValueTask ApplyAsync(
        HttpRequestMessage request, OutboundAuthConfig config, ReadOnlyMemory<byte> body,
        int endpointId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var auth = (BearerOutboundAuth)config;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        return ValueTask.CompletedTask;
    }
}
