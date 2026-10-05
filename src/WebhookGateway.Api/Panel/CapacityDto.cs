namespace WebhookGateway.Api.Panel;

/// <summary>Lo que devuelve <c>/api/capacity</c>: el informe de <c>sp_Gateway_CapacityReport</c>.</summary>
/// <param name="Desde">Inicio de la ventana analizada, en UTC.</param>
/// <param name="Hasta">Fin de la ventana analizada, en UTC.</param>
public sealed record CapacityReportDto(
    int Minutes, DateTime Desde, DateTime Hasta,
    CapacityGlobalDto Global, IReadOnlyList<CapacityDestinationDto> Destinations);

/// <param name="ConcurrenciaMedia">
/// Peticiones en vuelo de media en la ventana: suma de lo que tardaron ÷ duración de la ventana
/// (ley de Little). Comparada con <see cref="MaxConcurrency"/> dice si el destino usa todo su cupo.
/// </param>
/// <param name="PendientesVencidas">Solo en el informe del momento; nulo en ventanas pasadas.</param>
/// <param name="RetrasoSegundos">Solo en el informe del momento; nulo en ventanas pasadas.</param>
public sealed record CapacityDestinationDto(
    int Destino, string Nombre, bool Activo, int MaxConcurrency, int RateLimitPerMinute,
    decimal IntentosPorMinuto, int? LatenciaMediaMs, int? LatenciaP95Ms,
    decimal? ConcurrenciaMedia, decimal? UsoConcurrencia, decimal? UsoRitmo, decimal? TasaFallos,
    long? PendientesVencidas, int? RetrasoSegundos, string Diagnostico);

/// <param name="SesionesSql">Nulo si el usuario de SQL no tiene permiso para verlas.</param>
public sealed record CapacityGlobalDto(
    decimal ConcurrenciaTotal, int MaxGlobalConcurrency, decimal UsoCupoGlobal,
    int? DestinosConTrabajo, int MaxEndpointsInParallel,
    int? SesionesSql, int MaxPoolSize, string Diagnostico);
