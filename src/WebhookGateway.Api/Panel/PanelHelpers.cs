using System.Globalization;
using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace WebhookGateway.Api.Panel;

/// <summary>Utilidades compartidas por los endpoints del panel.</summary>
internal static class PanelHelpers
{
    /// <summary>IP del cliente, para auditoría.</summary>
    internal static string? ClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString();

    /// <summary>Id del usuario autenticado.</summary>
    internal static int UserId(ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub)!, CultureInfo.InvariantCulture);

    /// <summary>¿Es administrador?</summary>
    internal static bool IsAdmin(ClaimsPrincipal user) =>
        string.Equals(user.FindFirstValue("admin"), "true", StringComparison.OrdinalIgnoreCase);
}
