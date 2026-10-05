/*
    WebhookGateway — informe de capacidad. Idempotente. Solo lee.

    Responde "¿se está quedando corta la configuración?" con lo que el gateway ya guarda: cada
    DeliveryAttempt apunta cuándo empezó y cuánto tardó la petición HTTP. De ahí sale, por la ley de
    Little, cuántas peticiones tuvo cada destino en vuelo de media (suma de duraciones ÷ duración de
    la ventana), y eso se compara con su MaxConcurrency. Sumado sobre todos los destinos, con el cupo
    global.

    Los topes de appsettings.json (MaxGlobalConcurrency, MaxEndpointsInParallel,
    MaxPerEndpointPerClaim, MaxPoolSize) SQL no los conoce: se pasan como parámetros, con los
    valores recomendados por defecto. Si en appsettings son otros, pasar los de allí.

    Cómo leerlo, por destino:
      - UsoConcurrencia cerca de 1 y retraso creciendo: el destino necesita más MaxConcurrency
        (si lo aguanta) o responder más rápido.
      - UsoRitmo cerca de 1: el límite es RateLimitPerMinute.
      - Retraso creciendo con UsoConcurrencia bajo: el freno está fuera del destino (cupo global,
        latencia a SQL, circuito abierto). Mirar el resumen global.
    Y global: UsoCupoGlobal cerca de 1 pide subir MaxGlobalConcurrency; DestinosConTrabajo por
    encima de MaxEndpointsInParallel significa destinos esperando turno; SesionesSql cerca de
    MaxPoolSize, riesgo de agotar el pool.

    PendientesVencidas y RetrasoSegundos son el estado de AHORA: solo salen cuando @Until es NULL. En
    una ventana pasada la cola ya no es la que era, y mostrarla engañaría.

    Coste: recorre los intentos de la ventana (por la clave agrupada, que empieza por StartedAt) y
    las entregas de las particiones posibles, y los cruza con un hash join: unas 600 páginas por
    cinco minutos a 70/s, medido en LocalDB. El percentil 95 es APPROX_PERCENTILE_CONT (SQL Server
    2022 o posterior): el exacto, como función de ventana, copiaba todas las filas a una tabla de
    trabajo y costaba 85.000 lecturas. Para decidir capacidad, un percentil aproximado basta.
*/

SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.sp_Gateway_CapacityReport
    @Minutes                int          = 15,
    @Until                  datetime2(3) = NULL,   -- fin de la ventana; NULL = ahora
    @AlertLagSeconds        int          = 120,    -- a partir de aquí un destino se considera acumulando
    @MaxGlobalConcurrency   int          = 256,
    @MaxEndpointsInParallel int          = 32,
    @MaxPerEndpointPerClaim int          = 40,
    @MaxPoolSize            int          = 120
AS
BEGIN
    SET NOCOUNT ON;
    -- Es una foto para vigilar: no debe esperar a nadie ni hacer esperar a nadie.
    SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

    DECLARE @hasta datetime2(3) = ISNULL(@Until, SYSUTCDATETIME());
    DECLARE @desde datetime2(3) = DATEADD(MINUTE, -@Minutes, @hasta);
    DECLARE @ventanaMs float = @Minutes * 60000.0;

    -- Una entrega intentada en la ventana no puede haberse creado antes de la ventana de entrega más
    -- larga: eso acota la búsqueda de cada entrega a una o dos particiones.
    DECLARE @entregasDesde datetime2(3) = DATEADD(HOUR,
        -(SELECT ISNULL(MAX(DeliveryWindowHours), 72) + 1 FROM dbo.OutboundEndpoint), @desde);

    DECLARE @porDestino TABLE (
        OutboundEndpointId int PRIMARY KEY, Intentos int, Exitos int,
        LatenciaMediaMs float, LatenciaP95Ms float, ConcurrenciaMedia float);

    INSERT INTO @porDestino
    SELECT d.OutboundEndpointId, COUNT(*),
           SUM(CASE WHEN a.StatusCode BETWEEN 200 AND 299 THEN 1 ELSE 0 END),
           AVG(CAST(a.DurationMs AS float)),
           APPROX_PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY a.DurationMs),
           SUM(CAST(a.DurationMs AS float)) / @ventanaMs
    FROM dbo.DeliveryAttempt AS a
    JOIN dbo.WebhookDelivery AS d WITH (INDEX(UX_Delivery_Id))
      ON d.Id = a.DeliveryId AND d.CreatedAt >= @entregasDesde AND d.CreatedAt <= @hasta
    WHERE a.StartedAt >= @desde AND a.StartedAt < @hasta
    GROUP BY d.OutboundEndpointId
    OPTION (HASH JOIN);

    DECLARE @pendientes TABLE (OutboundEndpointId int PRIMARY KEY, Vencidas bigint);
    DECLARE @retraso TABLE (OutboundEndpointId int PRIMARY KEY, LagSeconds int);

    IF @Until IS NULL
    BEGIN
        INSERT INTO @pendientes
        SELECT OutboundEndpointId, COUNT_BIG(*)
        FROM dbo.WebhookDelivery WITH (INDEX(IX_Delivery_DispatchByEndpoint))
        WHERE Status IN (0, 2) AND NextAttemptAt <= @hasta AND ExpiresAt > @hasta
        GROUP BY OutboundEndpointId;

        INSERT INTO @retraso SELECT OutboundEndpointId, LagSeconds FROM dbo.fn_Gateway_DeliveryLag(@hasta);
    END

    /* ---------- 1. Por destino ---------- */
    SELECT
        o.Id AS Destino, o.Name AS Nombre, o.IsActive AS Activo,
        o.MaxConcurrency, o.RateLimitPerMinute,
        ISNULL(p.Intentos, 0) / (1.0 * @Minutes)                         AS IntentosPorMinuto,
        CAST(p.LatenciaMediaMs AS int)                                    AS LatenciaMediaMs,
        CAST(p.LatenciaP95Ms AS int)                                      AS LatenciaP95Ms,
        CAST(p.ConcurrenciaMedia AS decimal(9, 1))                        AS ConcurrenciaMedia,
        CAST(p.ConcurrenciaMedia / NULLIF(o.MaxConcurrency, 0) AS decimal(5, 2)) AS UsoConcurrencia,
        CAST(ISNULL(p.Intentos, 0) / (1.0 * @Minutes)
             / NULLIF(o.RateLimitPerMinute, 0) AS decimal(5, 2))          AS UsoRitmo,
        CAST(1.0 - ISNULL(p.Exitos, 0) * 1.0 / NULLIF(p.Intentos, 0) AS decimal(5, 2)) AS TasaFallos,
        CASE WHEN @Until IS NULL THEN ISNULL(q.Vencidas, 0) END          AS PendientesVencidas,
        CASE WHEN @Until IS NULL THEN ISNULL(r.LagSeconds, 0) END         AS RetrasoSegundos,
        CASE
            WHEN p.Intentos IS NULL AND ISNULL(q.Vencidas, 0) = 0 THEN N'Sin tráfico en la ventana'
            WHEN 1.0 - ISNULL(p.Exitos, 0) * 1.0 / NULLIF(p.Intentos, 0) >= 0.20
                THEN N'El destino está fallando: no es capacidad, revisar sus errores'
            WHEN ISNULL(r.LagSeconds, 0) >= @AlertLagSeconds
                 AND p.ConcurrenciaMedia >= 0.85 * o.MaxConcurrency
                THEN N'Concurrencia al límite: subir MaxConcurrency si el destino lo aguanta, o que responda más rápido'
            WHEN ISNULL(r.LagSeconds, 0) >= @AlertLagSeconds AND o.RateLimitPerMinute > 0
                 AND ISNULL(p.Intentos, 0) / (1.0 * @Minutes) >= 0.9 * o.RateLimitPerMinute
                THEN N'Ritmo al límite: subir RateLimitPerMinute si el destino lo acepta'
            WHEN ISNULL(r.LagSeconds, 0) >= @AlertLagSeconds
                THEN N'Acumula sin llenar su concurrencia: el freno está fuera del destino (despachador parado, circuito abierto, cupo global o SQL)'
            WHEN o.MaxConcurrency / 4.0 > @MaxPerEndpointPerClaim
                THEN N'Al día, pero MaxPerEndpointPerClaim es menor que MaxConcurrency / 4: no llena su concurrencia'
            ELSE N'Al día'
        END AS Diagnostico
    FROM dbo.OutboundEndpoint AS o
    LEFT JOIN @porDestino AS p ON p.OutboundEndpointId = o.Id
    LEFT JOIN @pendientes AS q ON q.OutboundEndpointId = o.Id
    LEFT JOIN @retraso    AS r ON r.OutboundEndpointId = o.Id
    WHERE o.IsActive = 1 OR p.Intentos IS NOT NULL OR q.Vencidas IS NOT NULL
    ORDER BY ISNULL(r.LagSeconds, 0) DESC, p.ConcurrenciaMedia DESC;

    /* ---------- 2. Global ---------- */
    DECLARE @concTotal float = (SELECT ISNULL(SUM(ConcurrenciaMedia), 0) FROM @porDestino);
    DECLARE @conTrabajo int = CASE WHEN @Until IS NULL THEN (SELECT COUNT(*) FROM @retraso) END;
    DECLARE @sesiones int =
        CASE WHEN HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE') = 1
             THEN (SELECT COUNT(*) FROM sys.dm_exec_sessions
                   WHERE program_name = N'WebhookGateway' AND database_id = DB_ID())
        END;

    SELECT
        @desde AS Desde, @hasta AS Hasta,
        CAST(@concTotal AS decimal(9, 1))                          AS ConcurrenciaTotal,
        @MaxGlobalConcurrency                                      AS MaxGlobalConcurrency,
        CAST(@concTotal / @MaxGlobalConcurrency AS decimal(5, 2))  AS UsoCupoGlobal,
        @conTrabajo                                                AS DestinosConTrabajo,  -- NULL en ventanas pasadas
        @MaxEndpointsInParallel                                    AS MaxEndpointsInParallel,
        @sesiones                                                  AS SesionesSql,   -- NULL sin VIEW SERVER STATE
        @MaxPoolSize                                               AS MaxPoolSize,
        CASE
            WHEN @concTotal >= 0.85 * @MaxGlobalConcurrency
                THEN N'Cupo global al límite: subir MaxGlobalConcurrency'
            WHEN @conTrabajo > @MaxEndpointsInParallel
                THEN N'Hay destinos esperando turno: subir MaxEndpointsInParallel (y vigilar el pool)'
            WHEN @sesiones >= 0.85 * @MaxPoolSize
                THEN N'Pool de SQL cerca del tope: subir MaxPoolSize o revisar quién lo ocupa'
            ELSE N'Holgado'
        END AS Diagnostico;
END
GO
