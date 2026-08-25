using Microsoft.EntityFrameworkCore;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data;
using WebhookGateway.Data.Security;

namespace WebhookGateway.Api.Auth;

/// <summary>
/// Endpoint de instalación inicial: crea el primer usuario admin. Solo funciona cuando la
/// tabla <c>AppUser</c> está vacía. Después de la primera llamada, devuelve <c>403</c>.
/// </summary>
public static class SetupEndpoints
{
    public static void MapSetup(this WebApplication app)
    {
        app.MapPost("/api/setup", CreateFirstAdminAsync)
            .WithTags("Setup")
            .AllowAnonymous()
            .Produces<SetupResponse>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> CreateFirstAdminAsync(
        SetupRequest request, GatewayDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Password) ||
            string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Results.BadRequest(new ErrorResponse("Email, contraseña y nombre son obligatorios."));
        }

        if (request.Password.Length < 8)
        {
            return Results.BadRequest(new ErrorResponse("La contraseña debe tener al menos 8 caracteres."));
        }

        // Solo funciona cuando no hay usuarios. Después de crear el primero, este
        // endpoint queda permanentemente desactivado.
        if (await db.Users.AnyAsync(ct))
        {
            return Results.Forbid();
        }

        var user = new AppUser
        {
            Email = request.Email.Trim().ToLowerInvariant(),
            DisplayName = request.DisplayName.Trim(),
            PasswordHash = PasswordHasher.Hash(request.Password),
            IsAdmin = true,
            IsActive = true,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        return Results.Created("/api/setup", new SetupResponse(
            user.Id, user.Email, user.DisplayName, user.IsAdmin,
            "Administrador creado. Este endpoint ya no responderá."));
    }
}

public sealed record SetupRequest(string Email, string Password, string DisplayName);

public sealed record SetupResponse(
    int Id,
    string Email,
    string DisplayName,
    bool IsAdmin,
    string Message);
