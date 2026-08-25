using System.Globalization;

namespace WebhookGateway.Dispatcher;

/// <summary>Ajustes del despachador. Los valores por defecto sirven sin tocar nada.</summary>
public sealed class DispatcherOptions
{
    public const string SectionName = "Gateway:Dispatcher";

    /// <summary>
    /// Apagar esto deja la instancia solo recibiendo. Es el interruptor que permite separar
    /// recepción y despacho en procesos distintos sin cambiar una línea de código.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Entregas reclamadas por ciclo, sumando todos los destinos.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Techo por destino dentro de un mismo ciclo. Es lo que impide que un destino con
    /// mucho backlog se lleve el lote entero y deje a los demás esperando.
    /// </summary>
    public int MaxPerEndpointPerClaim { get; set; } = 20;

    /// <summary>
    /// Cuánto dura la reclamación. Debe superar con holgura el tiempo de entrega más lento;
    /// si vence antes, otra instancia recogería la entrega y la enviaría dos veces.
    /// </summary>
    public int LeaseSeconds { get; set; } = 180;

    /// <summary>Espera cuando no hay nada que hacer. La cola en memoria la interrumpe antes.</summary>
    public int IdlePollSeconds { get; set; } = 5;

    /// <summary>Cada cuánto se recuperan leases huérfanos y se caducan entregas vencidas.</summary>
    public int MaintenanceIntervalSeconds { get; set; } = 60;

    /// <summary>Filas tocadas como máximo por cada pasada de mantenimiento.</summary>
    public int MaintenanceBatchSize { get; set; } = 500;

    /// <summary>Segundos que se cachea la configuración de un destino antes de releerla.</summary>
    public int TargetCacheSeconds { get; set; } = 30;

    /// <summary>
    /// Identifica a esta instancia en <c>WorkerId</c>. Se genera solo; solo hay que fijarlo
    /// si hace falta reconocer una instancia concreta en los registros.
    /// </summary>
    public string WorkerId { get; set; } =
        $"{Environment.MachineName}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}";
}
