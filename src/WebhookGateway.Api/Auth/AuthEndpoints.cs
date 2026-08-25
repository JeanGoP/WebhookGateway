namespace WebhookGateway.Api.Auth;

/// <summary>Superficie HTTP de <c>/api/auth/*</c>.</summary>
public static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync)
            .Produces<AuthResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/refresh", RefreshAsync)
            .Produces<AuthResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/logout", LogoutAsync)
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent);
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, AuthService auth, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.BadRequest(new ErrorResponse("Email y contraseña son obligatorios."));
        }

        var result = await auth.LoginAsync(request.Email, request.Password, ct);

        return result.Match(
            onSuccess: response => Results.Ok(response),
            onFailure: error => error.Code switch
            {
                "auth.locked" => Results.Problem(error.Message, statusCode: StatusCodes.Status429TooManyRequests),
                _ => Results.Problem(error.Message, statusCode: StatusCodes.Status401Unauthorized),
            });
    }

    private static async Task<IResult> RefreshAsync(RefreshRequest request, AuthService auth, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Results.BadRequest(new ErrorResponse("El token de refresco es obligatorio."));
        }

        var result = await auth.RefreshAsync(request.RefreshToken, ct);

        return result.Match(
            onSuccess: response => Results.Ok(response),
            onFailure: error => Results.Problem(error.Message, statusCode: StatusCodes.Status401Unauthorized));
    }

    private static async Task<IResult> LogoutAsync(RefreshRequest request, AuthService auth, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            await auth.LogoutAsync(request.RefreshToken, ct);
        }

        return Results.NoContent();
    }
}

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);
