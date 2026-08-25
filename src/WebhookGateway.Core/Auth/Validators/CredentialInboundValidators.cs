using System.Security.Cryptography;
using System.Text;
using WebhookGateway.Core.Common;
using WebhookGateway.Core.Domain;

namespace WebhookGateway.Core.Auth.Validators;

/// <summary>Sin autenticación. Siempre pasa.</summary>
public sealed class NoAuthValidator : IInboundAuthValidator
{
    public InboundAuthType Type => InboundAuthType.None;

    public ValueTask<Result> ValidateAsync(in InboundRequest request, InboundAuthConfig config, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Ok());
}

/// <summary>Clave de API en cabecera o query string.</summary>
public sealed class ApiKeyInboundValidator : IInboundAuthValidator
{
    public InboundAuthType Type => InboundAuthType.ApiKey;

    public ValueTask<Result> ValidateAsync(in InboundRequest request, InboundAuthConfig config, CancellationToken cancellationToken)
    {
        var auth = (ApiKeyInboundAuth)config;
        var actual = auth.Location switch
        {
            ApiKeyLocation.Header => request.Headers.GetValueOrDefault(auth.ParameterName),
            ApiKeyLocation.QueryString => request.Query.GetValueOrDefault(auth.ParameterName),
            _ => null,
        };

        return ValueTask.FromResult(Matches(actual, auth.ExpectedValue)
            ? Result.Ok()
            : Result.Fail("auth.invalid_api_key", "La clave de API no coincide o falta."));
    }

    /// <summary>Comparación en tiempo constante: evita que el tiempo de respuesta filtre el secreto.</summary>
    internal static bool Matches(string? actual, string expected) =>
        actual is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));
}

/// <summary>Usuario y contraseña en <c>Authorization: Basic</c>.</summary>
public sealed class BasicInboundValidator : IInboundAuthValidator
{
    public InboundAuthType Type => InboundAuthType.Basic;

    public ValueTask<Result> ValidateAsync(in InboundRequest request, InboundAuthConfig config, CancellationToken cancellationToken)
    {
        var auth = (BasicInboundAuth)config;

        if (!request.Headers.TryGetValue("Authorization", out var header) ||
            !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(Result.Fail("auth.missing_basic", "Falta la cabecera Authorization: Basic."));
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        }
        catch (FormatException)
        {
            return ValueTask.FromResult(Result.Fail("auth.malformed_basic", "La cabecera Authorization no es Base64 válido."));
        }

        var separator = decoded.IndexOf(':');
        var user = separator < 0 ? decoded : decoded[..separator];
        var pass = separator < 0 ? string.Empty : decoded[(separator + 1)..];

        // Con `&` en vez de `&&` a propósito: así ambas comparaciones tardan lo mismo
        // aunque el usuario ya falle, y el tiempo de respuesta no delata cuál de los dos
        // campos fue el que no coincidió.
        var ok = ApiKeyInboundValidator.Matches(user, auth.Username) & ApiKeyInboundValidator.Matches(pass, auth.Password);
        return ValueTask.FromResult(ok ? Result.Ok() : Result.Fail("auth.invalid_basic", "Usuario o contraseña incorrectos."));
    }
}

/// <summary>Token fijo en <c>Authorization: Bearer</c>.</summary>
public sealed class BearerInboundValidator : IInboundAuthValidator
{
    public InboundAuthType Type => InboundAuthType.Bearer;

    public ValueTask<Result> ValidateAsync(in InboundRequest request, InboundAuthConfig config, CancellationToken cancellationToken)
    {
        var auth = (BearerInboundAuth)config;

        if (!request.Headers.TryGetValue("Authorization", out var header) ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(Result.Fail("auth.missing_bearer", "Falta la cabecera Authorization: Bearer."));
        }

        var token = header["Bearer ".Length..].Trim();
        return ValueTask.FromResult(ApiKeyInboundValidator.Matches(token, auth.ExpectedToken)
            ? Result.Ok()
            : Result.Fail("auth.invalid_bearer", "El token no coincide."));
    }
}
