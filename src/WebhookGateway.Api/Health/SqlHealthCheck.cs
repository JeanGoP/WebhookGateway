using Microsoft.Extensions.Diagnostics.HealthChecks;
using WebhookGateway.Data.Db;

namespace WebhookGateway.Api.Health;

/// <summary>
/// Comprueba que SQL Server responde de verdad.
/// </summary>
/// <remarks>
/// Tiene que reflejar la conectividad real, no devolver «sano» sin más: si esta instancia
/// no puede persistir, no puede aceptar webhooks, y es preferible que el balanceador deje
/// de mandarle tráfico a que acepte y pierda.
/// </remarks>
public sealed class SqlHealthCheck(ISqlConnectionFactory connections) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = await connections.OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();

            command.CommandText = "SELECT 1";
            command.CommandTimeout = 3;
            command.ExecuteScalar();

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("No hay conexión con SQL Server.", ex);
        }
    }
}
