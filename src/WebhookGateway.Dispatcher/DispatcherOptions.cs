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

    /// <summary>
    /// Entregas que reclama un destino cada vez que pide trabajo. No es un techo de ritmo: el
    /// destino vuelve a pedir en cuanto termina, así que esto solo decide el tamaño del viaje a
    /// SQL. Que ningún destino acapare a los demás ya no depende de este número, sino de que cada
    /// uno avance por su cuenta.
    /// </summary>
    public int MaxPerEndpointPerClaim { get; set; } = 20;

    /// <summary>
    /// Peticiones HTTP simultáneas en vuelo sumando todos los destinos.
    /// </summary>
    /// <remarks>
    /// Antes el paralelismo real era <c>Environment.ProcessorCount</c> —ocho en este servidor—
    /// porque <c>Parallel.ForEachAsync</c> lo usa por defecto, aunque el comentario del código
    /// dijera "sin límite". Ocho entregas a la vez con destinos que tardan 300–400 ms son unas
    /// 20–25 por segundo como mucho, y hacen falta 4,6 de media con picos muy por encima.
    /// <para>
    /// Entregar es esperar a la red, no calcular, así que el número correcto no tiene que ver con
    /// los núcleos. Lo que de verdad limita es lo que cada destino aguanta, y de eso ya se encarga
    /// su propio límite de ritmo y de concurrencia; esto es solo la red de seguridad para que mil
    /// destinos a la vez no se traduzcan en mil conexiones.
    /// </para>
    /// </remarks>
    public int MaxGlobalConcurrency { get; set; } = 128;

    /// <summary>
    /// Destinos avanzando a la vez. Lo que acota es el uso del pool de conexiones, no el ritmo: cada
    /// destino en marcha abre una conexión para reclamar y otra para volcar resultados. Con el puñado
    /// de destinos que hay hoy este techo no se llega a tocar.
    /// </summary>
    public int MaxEndpointsInParallel { get; set; } = 32;

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

    /// <summary>
    /// Margen para el apagado ordenado: terminar lo que está en vuelo, volcar los resultados y
    /// liberar los leases. Si se agota, los leases se liberan solos al vencer, pero eso son tres
    /// minutos de entregas quietas en cada despliegue.
    /// </summary>
    /// <remarks>
    /// Tiene que caber dentro de <c>HostOptions.ShutdownTimeout</c> (30 s, fijado en Program.cs) y,
    /// detrás de IIS, dentro del <c>shutdownTimeLimit</c> de web.config.
    /// </remarks>
    public int ShutdownGraceSeconds { get; set; } = 15;

    /// <summary>Segundos que se cachea la configuración de un destino antes de releerla.</summary>
    public int TargetCacheSeconds { get; set; } = 30;

    /// <summary>
    /// Identifica a esta instancia en <c>WorkerId</c>. Se genera solo; solo hay que fijarlo
    /// si hace falta reconocer una instancia concreta en los registros.
    /// </summary>
    public string WorkerId { get; set; } =
        $"{Environment.MachineName}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}";
}
