/*
    WebhookGateway — jobs de SQL Server Agent. Idempotente: crea el que falte y no toca el que ya
    esté. Para cambiar la frecuencia o un paso de un job existente, bórralo y vuelve a ejecutar esto.

    ⚠ ESTO SE EJECUTA EN LA BASE DEL GATEWAY, y crea los jobs en msdb apuntando a la base en la que
    se ejecuta. Los jobs son del servidor, no de la base: por eso **su nombre lleva el nombre de la
    base**. Ejecutado en WebhookGateway_dev crea "WebhookGateway_dev - …" apuntando a _dev, y
    ejecutado en producción crea los suyos. Así los dos juegos conviven en la misma instancia sin
    pisarse, que es exactamente la situación de este servidor.

    La purga entra con @DryRun = 1 a propósito: hay que dejarla una semana contando lo que borraría
    antes de dejarla borrar. Para activarla se edita el paso del job y se pone @DryRun = 0.

    Permisos: crear jobs necesita pertenecer a SQLAgentUserRole (o más) en msdb. Si el script falla
    con un error de permisos, es cosa del DBA, no del script.
*/

SET NOCOUNT ON;
GO

/* Guarda: esto no tiene sentido fuera de la base del gateway. */
IF OBJECT_ID(N'dbo.sp_Gateway_Watchdog') IS NULL
   OR OBJECT_ID(N'dbo.sp_Gateway_PurgeExpiredPartitions') IS NULL
   OR OBJECT_ID(N'dbo.sp_Gateway_PurgeUnpartitionedLogs') IS NULL
BEGIN
    /*
        Severidad 16 y SET NOEXEC ON, no severidad 20: los niveles 19 a 25 solo los puede usar un
        sysadmin, y este script está pensado para ejecutarse en un servidor compartido donde puede
        que no lo seamos. NOEXEC hace que el resto no se ejecute.
    */
    RAISERROR(N'Esta base no tiene los procedimientos del gateway. Ejecuta primero 04, 12 y 13, y hazlo en la base del gateway.', 16, 1);
    SET NOEXEC ON;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.dm_server_services WHERE servicename LIKE N'SQL Server Agent%' AND status_desc = N'Running')
BEGIN
    PRINT 'AVISO: SQL Server Agent no aparece como en ejecución. Los jobs se crearán, pero no se dispararán hasta que arranque.';
END
GO

DECLARE @db sysname = DB_NAME();
/*
    @jobId se reutiliza para los cuatro jobs y TIENE que volver a NULL antes de cada sp_add_job: si llega
    con el id del anterior, no se crea un job nuevo y los pasos y horarios siguientes se cuelgan del
    primero. Así estaba, y salía un solo job con cinco pasos y cuatro horarios (visto en LocalDB el
    2026-10-05, antes de ejecutarlo nunca en un servidor real).
*/
DECLARE @jobId uniqueidentifier;
DECLARE @nombre sysname;
DECLARE @paso nvarchar(max);
-- Los parámetros de un EXEC no admiten expresiones: el nombre de cada horario se arma aquí antes.
DECLARE @horario sysname;

/* Categoría propia, para que no se mezclen con los jobs de otros sistemas de la instancia. */
IF NOT EXISTS (SELECT 1 FROM msdb.dbo.syscategories WHERE name = N'WebhookGateway' AND category_class = 1)
BEGIN
    EXEC msdb.dbo.sp_add_category @class = N'JOB', @type = N'LOCAL', @name = N'WebhookGateway';
END

/* ====================================================================
   1. Particiones futuras — semanal, domingo 02:00

   No es opcional: si se agotan las particiones futuras, los INSERT de tráfico fallan y la recepción
   devuelve 503. Seis meses de margen dan tiempo de sobra a que alguien se entere.
   ==================================================================== */

SET @nombre = @db + N' - Particiones futuras';

IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @nombre)
BEGIN
    SET @jobId = NULL;  -- si llega con el id del job anterior, sp_add_job no crea uno nuevo
    EXEC msdb.dbo.sp_add_job
        @job_name = @nombre,
        @category_name = N'WebhookGateway',
        @description = N'Crea las particiones mensuales que faltan hacia delante. Sin esto, la recepción acaba devolviendo 503.',
        @enabled = 1,
        @job_id = @jobId OUTPUT;

    EXEC msdb.dbo.sp_add_jobstep
        @job_id = @jobId,
        @step_name = N'EnsureFuturePartitions',
        @subsystem = N'TSQL',
        @database_name = @db,
        @command = N'EXEC dbo.sp_Gateway_EnsureFuturePartitions @MonthsAhead = 6;',
        @retry_attempts = 2,
        @retry_interval = 5;

    SET @horario = @nombre + N' - semanal';
    EXEC msdb.dbo.sp_add_jobschedule
        @job_id = @jobId,
        @name = @horario,
        @freq_type = 8,                 -- semanal
        @freq_interval = 1,             -- domingo
        @freq_recurrence_factor = 1,
        @active_start_time = 20000;     -- 02:00:00

    EXEC msdb.dbo.sp_add_jobserver @job_id = @jobId;

    PRINT 'Creado: ' + @nombre;
END
ELSE PRINT 'Ya existía: ' + @nombre;

/* ====================================================================
   2. Purga — diaria 03:00

   Entra en seco. TRUNCATE toma un bloqueo exclusivo breve y la purga por lotes mete pausas, pero
   de madrugada es cuando menos molesta.

   Retención: la de dbo.RetentionPolicy, editable desde el panel (por defecto mensajes y entregas
   180 días, cuerpos 30, intentos 30). La purga de particiones solo borra meses completos, así que
   "30 días" en la práctica son entre 30 y 61.
   ==================================================================== */

SET @nombre = @db + N' - Purga';

IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @nombre)
BEGIN
    SET @jobId = NULL;  -- si llega con el id del job anterior, sp_add_job no crea uno nuevo
    EXEC msdb.dbo.sp_add_job
        @job_name = @nombre,
        @category_name = N'WebhookGateway',
        @description = N'Vacía particiones vencidas y purga por lotes las tablas sin particionar. ENTRA EN SECO: para que borre de verdad, poner @DryRun = 0 en los dos pasos.',
        @enabled = 1,
        @job_id = @jobId OUTPUT;

    -- Sin días: los toma de dbo.RetentionPolicy, que se edita desde el panel.
    SET @paso = N'EXEC dbo.sp_Gateway_PurgeExpiredPartitions
    @DryRun = 1;';

    EXEC msdb.dbo.sp_add_jobstep
        @job_id = @jobId,
        @step_name = N'Particiones vencidas',
        @subsystem = N'TSQL',
        @database_name = @db,
        @command = @paso,
        @on_success_action = 3;         -- seguir al paso siguiente

    SET @paso = N'EXEC dbo.sp_Gateway_PurgeUnpartitionedLogs
    @NotificationRetentionDays = 90,
    @AuditRetentionDays        = 365,
    @DryRun                    = 1;';

    EXEC msdb.dbo.sp_add_jobstep
        @job_id = @jobId,
        @step_name = N'Tablas sin particionar',
        @subsystem = N'TSQL',
        @database_name = @db,
        @command = @paso;

    SET @horario = @nombre + N' - diaria';
    EXEC msdb.dbo.sp_add_jobschedule
        @job_id = @jobId,
        @name = @horario,
        @freq_type = 4,                 -- diaria
        @freq_interval = 1,
        @active_start_time = 30000;     -- 03:00:00

    EXEC msdb.dbo.sp_add_jobserver @job_id = @jobId;

    PRINT 'Creado: ' + @nombre + ' (EN SECO)';
END
ELSE PRINT 'Ya existía: ' + @nombre;

/* ====================================================================
   3. Estadísticas — diaria 03:30

   Después de la purga, que es lo que más mueve el reparto de datos. Sin estadísticas al día, el
   claim y el buscador del panel empiezan a elegir planes malos.
   ==================================================================== */

SET @nombre = @db + N' - Estadisticas';

IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @nombre)
BEGIN
    SET @jobId = NULL;  -- si llega con el id del job anterior, sp_add_job no crea uno nuevo
    EXEC msdb.dbo.sp_add_job
        @job_name = @nombre,
        @category_name = N'WebhookGateway',
        @description = N'Actualiza estadísticas de las cuatro tablas de tráfico, después de la purga.',
        @enabled = 1,
        @job_id = @jobId OUTPUT;

    SET @paso = N'UPDATE STATISTICS dbo.WebhookMessage;
UPDATE STATISTICS dbo.WebhookPayload;
UPDATE STATISTICS dbo.WebhookDelivery;
UPDATE STATISTICS dbo.DeliveryAttempt;';

    EXEC msdb.dbo.sp_add_jobstep
        @job_id = @jobId,
        @step_name = N'UPDATE STATISTICS',
        @subsystem = N'TSQL',
        @database_name = @db,
        @command = @paso;

    SET @horario = @nombre + N' - diaria';
    EXEC msdb.dbo.sp_add_jobschedule
        @job_id = @jobId,
        @name = @horario,
        @freq_type = 4,
        @freq_interval = 1,
        @active_start_time = 33000;     -- 03:30:00

    EXEC msdb.dbo.sp_add_jobserver @job_id = @jobId;

    PRINT 'Creado: ' + @nombre;
END
ELSE PRINT 'Ya existía: ' + @nombre;

/* ====================================================================
   4. Vigilancia — cada 10 minutos

   sp_Gateway_Watchdog lanza RAISERROR si encuentra algo, así que el job falla y queda en su
   historial. Para que además avise por correo, el DBA define un operador y marca "notificar al
   fallar" (@notify_level_eventlog / @notify_email_operator_name).
   ==================================================================== */

SET @nombre = @db + N' - Vigilancia';

IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @nombre)
BEGIN
    SET @jobId = NULL;  -- si llega con el id del job anterior, sp_add_job no crea uno nuevo
    EXEC msdb.dbo.sp_add_job
        @job_name = @nombre,
        @category_name = N'WebhookGateway',
        @description = N'Avisa si quedan menos de 3 meses de particiones, si el backlog se dispara, si alguna entrega lleva más de 15 minutos esperando o si hay leases huérfanos. Falla a propósito cuando encuentra algo.',
        @enabled = 1,
        @notify_level_eventlog = 2,     -- al fallar, al registro de eventos de Windows
        @job_id = @jobId OUTPUT;

    EXEC msdb.dbo.sp_add_jobstep
        @job_id = @jobId,
        @step_name = N'Watchdog',
        @subsystem = N'TSQL',
        @database_name = @db,
        -- @MaxLagMinutes tiene que coincidir con Gateway:Monitoring:MaxLagMinutes del panel.
        @command = N'EXEC dbo.sp_Gateway_Watchdog @MinMonthsOfPartitions = 3, @MaxBacklog = 50000, @MaxLagMinutes = 15;';

    SET @horario = @nombre + N' - cada 10 min';
    EXEC msdb.dbo.sp_add_jobschedule
        @job_id = @jobId,
        @name = @horario,
        @freq_type = 4,                 -- diaria…
        @freq_interval = 1,
        @freq_subday_type = 4,          -- …cada N minutos
        @freq_subday_interval = 10,
        @active_start_time = 0;

    EXEC msdb.dbo.sp_add_jobserver @job_id = @jobId;

    PRINT 'Creado: ' + @nombre;
END
ELSE PRINT 'Ya existía: ' + @nombre;
GO

/* Qué quedó creado para esta base. */
SELECT
    Job        = j.name,
    Habilitado = j.enabled,
    Pasos      = (SELECT COUNT(1) FROM msdb.dbo.sysjobsteps s WHERE s.job_id = j.job_id),
    Frecuencia = sch.name
FROM msdb.dbo.sysjobs j
LEFT JOIN msdb.dbo.sysjobschedules js ON js.job_id = j.job_id
LEFT JOIN msdb.dbo.sysschedules sch ON sch.schedule_id = js.schedule_id
WHERE j.name LIKE DB_NAME() + N' - %'
ORDER BY j.name;
GO

SET NOEXEC OFF;
GO
