using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using WebhookGateway.Data.Db;
using WebhookGateway.Dispatcher;

namespace WebhookGateway.Api.Panel;

/// <summary>
/// ¿Se está quedando corta la configuración? <c>/api/capacity</c> ejecuta
/// <c>sp_Gateway_CapacityReport</c> (db/18) con los topes que de verdad tiene esta instancia.
/// </summary>
/// <remarks>
/// SQL no conoce lo que hay en appsettings.json, así que el procedimiento los recibe por parámetro.
/// Llamarlo desde aquí es lo que garantiza que compare con la configuración real y no con los
/// valores por defecto del script.
/// </remarks>
public static class CapacityEndpoints
{
    public static void MapCapacity(this WebApplication app) =>
        app.MapGet("/api/capacity", GetAsync)
            .WithTags("Monitor")
            .RequireAuthorization()
            .Produces<CapacityReportDto>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> GetAsync(
        ISqlConnectionFactory connectionFactory,
        IOptions<DispatcherOptions> dispatcher,
        IOptions<SqlOptions> sql,
        IOptions<MonitoringOptions> monitoring,
        CancellationToken ct,
        int minutes = 15)
    {
        // Más de un día recorre demasiados intentos para una pantalla.
        if (minutes is < 1 or > 1440)
        {
            return Results.BadRequest(new ErrorResponse("La ventana tiene que estar entre 1 y 1.440 minutos."));
        }

        try
        {
            using var connection = await connectionFactory.OpenAsync(ct);
            using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
                "dbo.sp_Gateway_CapacityReport",
                new
                {
                    Minutes = minutes,
                    // Avisa de que un destino acumula bastante antes de que rompa el umbral del monitor.
                    AlertLagSeconds = Math.Max(60, monitoring.Value.MaxLagMinutes * 60 / 4),
                    dispatcher.Value.MaxGlobalConcurrency,
                    dispatcher.Value.MaxEndpointsInParallel,
                    dispatcher.Value.MaxPerEndpointPerClaim,
                    sql.Value.MaxPoolSize,
                },
                commandType: CommandType.StoredProcedure,
                commandTimeout: 60,
                cancellationToken: ct));

            var destinations = (await multi.ReadAsync<DestinationRow>()).Select(r => r.ToDto()).ToList();
            var global = await multi.ReadSingleAsync<GlobalRow>();

            return Results.Ok(new CapacityReportDto(
                minutes,
                DateTime.SpecifyKind(global.Desde, DateTimeKind.Utc),
                DateTime.SpecifyKind(global.Hasta, DateTimeKind.Utc),
                global.ToDto(),
                destinations));
        }
        catch (SqlException ex) when (ex.Number == 2812)
        {
            return Results.Json(
                new ErrorResponse("Falta el procedimiento sp_Gateway_CapacityReport: aplica db/18-capacity-report.sql."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    // Clases con propiedades y no records posicionales: Dapper las rellena por nombre y convierte
    // tipos, en vez de exigir que el constructor coincida exactamente con cada columna.
    private sealed class DestinationRow
    {
        public int Destino { get; init; }
        public string Nombre { get; init; } = "";
        public bool Activo { get; init; }
        public int MaxConcurrency { get; init; }
        public int RateLimitPerMinute { get; init; }
        public decimal IntentosPorMinuto { get; init; }
        public int? LatenciaMediaMs { get; init; }
        public int? LatenciaP95Ms { get; init; }
        public decimal? ConcurrenciaMedia { get; init; }
        public decimal? UsoConcurrencia { get; init; }
        public decimal? UsoRitmo { get; init; }
        public decimal? TasaFallos { get; init; }
        public long? PendientesVencidas { get; init; }
        public int? RetrasoSegundos { get; init; }
        public string Diagnostico { get; init; } = "";

        public CapacityDestinationDto ToDto() => new(
            Destino, Nombre, Activo, MaxConcurrency, RateLimitPerMinute, Math.Round(IntentosPorMinuto, 1),
            LatenciaMediaMs, LatenciaP95Ms, ConcurrenciaMedia, UsoConcurrencia, UsoRitmo, TasaFallos,
            PendientesVencidas, RetrasoSegundos, Diagnostico);
    }

    private sealed class GlobalRow
    {
        public DateTime Desde { get; init; }
        public DateTime Hasta { get; init; }
        public decimal ConcurrenciaTotal { get; init; }
        public int MaxGlobalConcurrency { get; init; }
        public decimal UsoCupoGlobal { get; init; }
        public int? DestinosConTrabajo { get; init; }
        public int MaxEndpointsInParallel { get; init; }
        public int? SesionesSql { get; init; }
        public int MaxPoolSize { get; init; }
        public string Diagnostico { get; init; } = "";

        public CapacityGlobalDto ToDto() => new(
            ConcurrenciaTotal, MaxGlobalConcurrency, UsoCupoGlobal, DestinosConTrabajo,
            MaxEndpointsInParallel, SesionesSql, MaxPoolSize, Diagnostico);
    }
}
