/*
    WebhookGateway — vigilante. Idempotente.

    Comprueba las dos cosas que, si pasan desapercibidas, se convierten en pérdida de mensajes:

      1. Que no se acaben las particiones futuras. Si se acaban, los INSERT de tráfico empiezan a
         fallar y la recepción devuelve 503 a todo el mundo. Es el fallo más grave posible aquí y el
         más fácil de evitar, porque se ve venir con meses de antelación.
      2. Que el backlog de entregas no crezca sin parar. Un backlog que sube y no baja significa que
         el despachador no da el ritmo o que un destino está caído.

    Cómo avisa: devuelve una fila por problema y, si hay alguno, lanza RAISERROR con severidad 16.
    Eso hace que el job de SQL Agent **falle**, y un job que falla sale en su historial y puede
    notificar a un operador. Se hace así a propósito, en vez de con sp_send_dbmail: no depende de
    que Database Mail esté configurado en esta instancia, que es compartida y no es nuestra.

    Conectar el aviso a un correo es el paso del DBA: definir un operador y marcar "notificar al
    fallar" en el job. Ver 14-sql-agent-jobs.sql.
*/

SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.sp_Gateway_Watchdog
    @MinMonthsOfPartitions int = 3,
    @MaxBacklog            int = 50000
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @problemas TABLE (Asunto varchar(60), Detalle nvarchar(400));

    /* ---------- 1. Margen de particiones ---------- */

    DECLARE @ultimaFrontera date = (
        SELECT MAX(CONVERT(date, CONVERT(datetime2(3), prv.value)))
        FROM sys.partition_functions pf
        JOIN sys.partition_range_values prv ON prv.function_id = pf.function_id
        WHERE pf.name = N'PF_Monthly');

    DECLARE @mesesDeMargen int = DATEDIFF(MONTH, CONVERT(date, SYSUTCDATETIME()), @ultimaFrontera);

    IF @ultimaFrontera IS NULL
    BEGIN
        INSERT INTO @problemas VALUES ('Particiones',
            N'No existe la función de partición PF_Monthly. El esquema no está completo.');
    END
    ELSE IF @mesesDeMargen < @MinMonthsOfPartitions
    BEGIN
        INSERT INTO @problemas VALUES ('Particiones',
            N'Quedan ' + CONVERT(nvarchar(10), @mesesDeMargen) + N' meses de particiones (última frontera '
            + CONVERT(nvarchar(10), @ultimaFrontera, 23) + N'). Cuando se agoten, la recepción empezará a '
            + N'devolver 503. Ejecutar sp_Gateway_EnsureFuturePartitions.');
    END

    /* ---------- 2. Backlog de entregas ---------- */

    /*
        Status IN (0, 2) es Pending y Retrying, lo que el despachador tiene por hacer. Se cuenta con
        el índice filtrado del claim, así que cuesta lo mismo con la tabla vacía que con quince
        millones de filas; NOLOCK porque es una cifra para vigilar, no para decidir nada.
    */
    DECLARE @backlog bigint = (
        SELECT COUNT_BIG(1)
        FROM dbo.WebhookDelivery WITH (NOLOCK)
        WHERE Status IN (0, 2));

    IF @backlog > @MaxBacklog
    BEGIN
        INSERT INTO @problemas VALUES ('Backlog',
            N'Hay ' + CONVERT(nvarchar(20), @backlog) + N' entregas pendientes, por encima del umbral de '
            + CONVERT(nvarchar(20), @MaxBacklog) + N'. Revisar si el despachador está en marcha y si algún '
            + N'destino está caído o limitando el ritmo.');
    END

    /* ---------- 3. Leases huérfanos ---------- */

    /*
        Entregas reclamadas cuyo lease venció hace rato: el mantenimiento del despachador debería
        haberlas devuelto a la cola. Si se acumulan, es que el despachador no está corriendo.
    */
    DECLARE @huerfanas bigint = (
        SELECT COUNT_BIG(1)
        FROM dbo.WebhookDelivery WITH (NOLOCK)
        WHERE Status = 1 AND LeaseUntil < DATEADD(MINUTE, -15, SYSUTCDATETIME()));

    IF @huerfanas > 0
    BEGIN
        INSERT INTO @problemas VALUES ('Leases',
            N'Hay ' + CONVERT(nvarchar(20), @huerfanas) + N' entregas reclamadas con el lease vencido hace '
            + N'más de 15 minutos. El mantenimiento del despachador debería haberlas liberado: '
            + N'comprobar que el proceso está vivo.');
    END

    /* ---------- Resultado ---------- */

    SELECT
        Asunto,
        Detalle,
        ComprobadoUtc   = SYSUTCDATETIME(),
        MesesDeMargen   = @mesesDeMargen,
        Backlog         = @backlog,
        LeasesHuerfanos = @huerfanas
    FROM @problemas;

    IF EXISTS (SELECT 1 FROM @problemas)
    BEGIN
        DECLARE @resumen nvarchar(2000) = N'';

        SELECT @resumen = @resumen + Asunto + N': ' + Detalle + NCHAR(10)
        FROM @problemas;

        -- Severidad 16: el job falla y queda visible en su historial.
        RAISERROR(N'Vigilante del gateway: %s', 16, 1, @resumen);
    END
END
GO
