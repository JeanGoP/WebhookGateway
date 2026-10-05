/*
    Esquema completo para la base de DESARROLLO WebhookGateway_dev.
    Abrir en SSMS, conectarse al servidor y pulsar F5 (Ejecutar).
    Solo toca WebhookGateway_dev: si por cualquier motivo la base activa no es esa,
    se detiene sin ejecutar nada (SET NOEXEC ON).
*/
USE master;
GO
IF DB_ID(N'WebhookGateway_dev') IS NULL
BEGIN
    RAISERROR('No existe la base WebhookGateway_dev. Creala primero.', 16, 1);
    SET NOEXEC ON;
END
GO
ALTER DATABASE [WebhookGateway_dev] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
ALTER DATABASE [WebhookGateway_dev] SET RECOVERY SIMPLE;
ALTER DATABASE [WebhookGateway_dev] SET AUTO_SHRINK OFF;
ALTER DATABASE [WebhookGateway_dev] SET AUTO_CLOSE OFF;
GO
USE [WebhookGateway_dev];
GO
IF DB_NAME() <> N'WebhookGateway_dev'
BEGIN
    RAISERROR('Proteccion: la base activa no es WebhookGateway_dev. No se ejecuta nada.', 16, 1);
    SET NOEXEC ON;
END
GO

-- =====================================================================
-- 01-schema.sql
-- =====================================================================
PRINT '-> 01-schema.sql';
GO
/*
    WebhookGateway — esquema base. Idempotente: se puede volver a ejecutar.

    Este script es la fuente de verdad del esquema, no las migraciones de EF.
    Las migraciones cubren solo las tablas de configuración; el particionado, los
    índices filtrados y la compresión viven aquí.

    Requiere SQL Server 2016 SP1 o superior. Probado en 2019 Enterprise.
*/

SET NOCOUNT ON;
GO

/* ------------------------------------------------------------------------
   1. Particionado mensual
   ------------------------------------------------------------------------
   RANGE RIGHT sobre el día 1 de cada mes: la frontera pertenece al mes que
   empieza. Se crean 6 meses hacia atrás y 12 hacia delante; el job de
   mantenimiento (02-partition-maintenance.sql) mantiene la ventana.
*/

IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'PF_Monthly')
BEGIN
    DECLARE @start date = DATEADD(MONTH, -6, DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1));
    DECLARE @i int = 0, @bounds nvarchar(max) = N'';

    WHILE @i < 18
    BEGIN
        SET @bounds += CASE WHEN @i > 0 THEN N', ' ELSE N'' END
                     + N'''' + CONVERT(nvarchar(10), DATEADD(MONTH, @i, @start), 23) + N'''';
        SET @i += 1;
    END

    EXEC(N'CREATE PARTITION FUNCTION PF_Monthly (datetime2(3)) AS RANGE RIGHT FOR VALUES (' + @bounds + N');');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'PS_Monthly')
BEGIN
    CREATE PARTITION SCHEME PS_Monthly AS PARTITION PF_Monthly ALL TO ([PRIMARY]);
END
GO

/* ------------------------------------------------------------------------
   2. Configuración — tablas pequeñas, sin particionar
   ------------------------------------------------------------------------ */

IF OBJECT_ID(N'dbo.Integration') IS NULL
CREATE TABLE dbo.Integration (
    Id                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Integration PRIMARY KEY,
    Name                 nvarchar(200)  NOT NULL,
    Slug                 varchar(100)   NOT NULL,
    Description          nvarchar(1000) NULL,
    IsActive             bit            NOT NULL CONSTRAINT DF_Integration_IsActive DEFAULT 1,
    RetentionDays        int            NOT NULL CONSTRAINT DF_Integration_Retention DEFAULT 365,
    PayloadRetentionDays int            NOT NULL CONSTRAINT DF_Integration_PayloadRetention DEFAULT 90,
    CreatedAt            datetime2(3)   NOT NULL CONSTRAINT DF_Integration_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Integration_Slug UNIQUE (Slug)
);
GO

IF OBJECT_ID(N'dbo.InboundEndpoint') IS NULL
CREATE TABLE dbo.InboundEndpoint (
    Id                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_InboundEndpoint PRIMARY KEY,
    IntegrationId        int           NOT NULL CONSTRAINT FK_InboundEndpoint_Integration
                                           REFERENCES dbo.Integration(Id),
    Name                 nvarchar(200) NOT NULL,
    Slug                 varchar(100)  NOT NULL,
    IsActive             bit           NOT NULL CONSTRAINT DF_InboundEndpoint_IsActive DEFAULT 1,
    AuthType             tinyint       NOT NULL CONSTRAINT DF_InboundEndpoint_AuthType DEFAULT 0,
    -- JSON de configuración cifrado con AES-GCM. Nunca sale por la API.
    AuthConfigCipher     varbinary(max) NOT NULL CONSTRAINT DF_InboundEndpoint_Cipher DEFAULT 0x,
    AuthConfigKeyVersion int           NOT NULL CONSTRAINT DF_InboundEndpoint_KeyVer DEFAULT 0,
    DedupeStrategy       tinyint       NOT NULL CONSTRAINT DF_InboundEndpoint_Dedupe DEFAULT 0,
    DedupeSource         nvarchar(400) NULL,
    MaxBodyBytes         int           NOT NULL CONSTRAINT DF_InboundEndpoint_MaxBody DEFAULT 1048576,
    CreatedAt            datetime2(3)  NOT NULL CONSTRAINT DF_InboundEndpoint_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_InboundEndpoint_Slug UNIQUE (IntegrationId, Slug)
);
GO

IF OBJECT_ID(N'dbo.OutboundEndpoint') IS NULL
CREATE TABLE dbo.OutboundEndpoint (
    Id                      int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OutboundEndpoint PRIMARY KEY,
    IntegrationId           int            NOT NULL CONSTRAINT FK_OutboundEndpoint_Integration
                                                REFERENCES dbo.Integration(Id),
    Name                    nvarchar(200)  NOT NULL,
    TargetUrl               nvarchar(2000) NOT NULL,
    HttpMethod              varchar(10)    NOT NULL CONSTRAINT DF_Outbound_Method DEFAULT 'POST',
    IsActive                bit            NOT NULL CONSTRAINT DF_Outbound_IsActive DEFAULT 1,
    AuthType                tinyint        NOT NULL CONSTRAINT DF_Outbound_AuthType DEFAULT 0,
    AuthConfigCipher        varbinary(max) NOT NULL CONSTRAINT DF_Outbound_Cipher DEFAULT 0x,
    AuthConfigKeyVersion    int            NOT NULL CONSTRAINT DF_Outbound_KeyVer DEFAULT 0,
    CustomHeadersJson       nvarchar(max)  NULL,
    -- Control de velocidad: lo que impide que saturemos al destino.
    RateLimitPerMinute      int            NOT NULL CONSTRAINT DF_Outbound_Rate DEFAULT 600,
    MaxConcurrency          int            NOT NULL CONSTRAINT DF_Outbound_Concurrency DEFAULT 4,
    TimeoutSeconds          int            NOT NULL CONSTRAINT DF_Outbound_Timeout DEFAULT 30,
    -- Reintentos.
    MaxAttempts             int            NOT NULL CONSTRAINT DF_Outbound_MaxAttempts DEFAULT 8,
    DeliveryWindowHours     int            NOT NULL CONSTRAINT DF_Outbound_Window DEFAULT 72,
    BackoffLadderJson       nvarchar(500)  NULL,
    -- Circuit breaker.
    BreakerFailureThreshold int            NOT NULL CONSTRAINT DF_Outbound_BreakerN DEFAULT 5,
    BreakerOpenSeconds      int            NOT NULL CONSTRAINT DF_Outbound_BreakerSecs DEFAULT 60,
    CreatedAt               datetime2(3)   NOT NULL CONSTRAINT DF_Outbound_CreatedAt DEFAULT SYSUTCDATETIME()
);
GO

IF OBJECT_ID(N'dbo.Subscription') IS NULL
CREATE TABLE dbo.Subscription (
    Id                 int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Subscription PRIMARY KEY,
    InboundEndpointId  int          NOT NULL CONSTRAINT FK_Subscription_Inbound
                                        REFERENCES dbo.InboundEndpoint(Id),
    OutboundEndpointId int          NOT NULL CONSTRAINT FK_Subscription_Outbound
                                        REFERENCES dbo.OutboundEndpoint(Id),
    IsActive           bit          NOT NULL CONSTRAINT DF_Subscription_IsActive DEFAULT 1,
    FilterJson         nvarchar(max) NULL,
    CreatedAt          datetime2(3) NOT NULL CONSTRAINT DF_Subscription_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Subscription UNIQUE (InboundEndpointId, OutboundEndpointId)
);
GO

/* Índice del fanout: se consulta en cada recepción para saber qué entregas crear. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Subscription_Fanout')
CREATE NONCLUSTERED INDEX IX_Subscription_Fanout
    ON dbo.Subscription (InboundEndpointId)
    INCLUDE (OutboundEndpointId, FilterJson)
    WHERE IsActive = 1;
GO

/* ------------------------------------------------------------------------
   3. Deduplicación — pequeña, sin particionar, retención corta
   ------------------------------------------------------------------------
   Va aparte a propósito. Un índice único sobre la tabla grande cruzaría
   particiones e impediría el SWITCH de purga.
*/

IF OBJECT_ID(N'dbo.MessageDedupe') IS NULL
CREATE TABLE dbo.MessageDedupe (
    InboundEndpointId int          NOT NULL,
    DedupeKey         varchar(200) NOT NULL,
    MessageId         bigint       NOT NULL,
    ExpiresAt         datetime2(3) NOT NULL,
    CONSTRAINT PK_MessageDedupe PRIMARY KEY CLUSTERED (InboundEndpointId, DedupeKey)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MessageDedupe_Expiry')
CREATE NONCLUSTERED INDEX IX_MessageDedupe_Expiry ON dbo.MessageDedupe (ExpiresAt);
GO
GO

-- =====================================================================
-- 02-traffic-tables.sql
-- =====================================================================
PRINT '-> 02-traffic-tables.sql';
GO
/*
    WebhookGateway — tablas de tráfico. Idempotente.

    Todas particionadas por mes y comprimidas con PAGE.

    Nota sobre claves: en una tabla particionada la clave agrupada debe incluir la
    columna de partición para que los índices queden alineados y el SWITCH de purga
    funcione. Por eso se agrupa por (fecha, id) y no por id solo. La consecuencia es
    que no hay claves foráneas entre las tablas de tráfico: la integridad la garantiza
    la aplicación, que es lo habitual a esta escala y lo que mantiene la purga barata.
*/

SET NOCOUNT ON;
GO

/* ------------------------------------------------------------------------
   WebhookMessage — lo recibido. Inmutable.
   ------------------------------------------------------------------------ */

IF OBJECT_ID(N'dbo.WebhookMessage') IS NULL
BEGIN
    CREATE TABLE dbo.WebhookMessage (
        Id                bigint IDENTITY(1,1) NOT NULL,
        ReceivedAt        datetime2(3)  NOT NULL,   -- columna de partición
        InboundEndpointId int           NOT NULL,
        SourceIp          varchar(45)   NOT NULL,
        HttpMethod        varchar(10)   NOT NULL,
        HeadersJson       nvarchar(max) NOT NULL,   -- cabeceras de autorización ya enmascaradas
        QueryString       nvarchar(2000) NULL,
        BodySizeBytes     int           NOT NULL,
        BodyHash          binary(32)    NOT NULL,
        Status            tinyint       NOT NULL,
        CONSTRAINT PK_WebhookMessage PRIMARY KEY CLUSTERED (ReceivedAt, Id)
            WITH (DATA_COMPRESSION = PAGE) ON PS_Monthly(ReceivedAt)
    ) ON PS_Monthly(ReceivedAt);

    -- Búsqueda por id desde el panel.
    CREATE UNIQUE NONCLUSTERED INDEX UX_WebhookMessage_Id
        ON dbo.WebhookMessage (Id, ReceivedAt)
        WITH (DATA_COMPRESSION = PAGE) ON PS_Monthly(ReceivedAt);

    -- Listado del explorador: por endpoint, más reciente primero.
    CREATE NONCLUSTERED INDEX IX_WebhookMessage_Search
        ON dbo.WebhookMessage (InboundEndpointId, ReceivedAt DESC)
        INCLUDE (Id, Status, BodySizeBytes)
        WITH (DATA_COMPRESSION = PAGE) ON PS_Monthly(ReceivedAt);
END
GO

/* ------------------------------------------------------------------------
   WebhookPayload — el cuerpo, con retención propia más corta.
   ------------------------------------------------------------------------ */

IF OBJECT_ID(N'dbo.WebhookPayload') IS NULL
BEGIN
    CREATE TABLE dbo.WebhookPayload (
        MessageId  bigint         NOT NULL,
        ReceivedAt datetime2(3)   NOT NULL,   -- columna de partición
        Encoding   tinyint        NOT NULL,   -- 0 raw, 1 gzip
        SizeBytes  int            NOT NULL,
        Body       varbinary(max) NULL,       -- inline
        StorageRef nvarchar(500)  NULL,       -- o externo; exactamente uno de los dos
        CONSTRAINT PK_WebhookPayload PRIMARY KEY CLUSTERED (ReceivedAt, MessageId)
            WITH (DATA_COMPRESSION = PAGE) ON PS_Monthly(ReceivedAt),
        -- Exactamente una de las dos ubicaciones: o inline, o externa. Nunca ambas ni
        -- ninguna. T-SQL no admite expresiones booleanas como valores, así que se
        -- cuentan con CASE.
        CONSTRAINT CK_WebhookPayload_OneLocation CHECK (
            CASE WHEN Body       IS NULL THEN 0 ELSE 1 END
          + CASE WHEN StorageRef IS NULL THEN 0 ELSE 1 END = 1)
    ) ON PS_Monthly(ReceivedAt);
END
GO

/* ------------------------------------------------------------------------
   WebhookDelivery — la unidad de trabajo del despachador.
   ------------------------------------------------------------------------ */

IF OBJECT_ID(N'dbo.WebhookDelivery') IS NULL
BEGIN
    CREATE TABLE dbo.WebhookDelivery (
        Id                 bigint IDENTITY(1,1) NOT NULL,
        CreatedAt          datetime2(3) NOT NULL,   -- columna de partición
        MessageId          bigint       NOT NULL,
        OutboundEndpointId int          NOT NULL,
        Status             tinyint      NOT NULL,
        AttemptCount       smallint     NOT NULL CONSTRAINT DF_Delivery_Attempts DEFAULT 0,
        NextAttemptAt      datetime2(3) NOT NULL,
        ExpiresAt          datetime2(3) NOT NULL,
        LeaseUntil         datetime2(3) NULL,
        WorkerId           varchar(64)  NULL,
        LastStatusCode     smallint     NULL,
        LastError          nvarchar(1000) NULL,
        CompletedAt        datetime2(3) NULL,
        CONSTRAINT PK_WebhookDelivery PRIMARY KEY CLUSTERED (CreatedAt, Id)
            WITH (DATA_COMPRESSION = PAGE) ON PS_Monthly(CreatedAt)
    ) ON PS_Monthly(CreatedAt);

    /*
        El índice del despachador. Filtrado a lo que está en vuelo: en régimen normal
        contiene decenas de filas, así que el claim cuesta lo mismo tenga la tabla
        60 000 filas o 15 millones. Es la razón por la que no hace falta separar
        tablas caliente y fría.
    */
    CREATE NONCLUSTERED INDEX IX_Delivery_Dispatch
        ON dbo.WebhookDelivery (NextAttemptAt, OutboundEndpointId)
        INCLUDE (Id, MessageId, AttemptCount, ExpiresAt)
        WHERE Status IN (0, 2)   -- Pending, Retrying
        ON PS_Monthly(CreatedAt);

    /* Recuperación de leases huérfanos tras la caída de un worker. */
    CREATE NONCLUSTERED INDEX IX_Delivery_Lease
        ON dbo.WebhookDelivery (LeaseUntil)
        INCLUDE (Id)
        WHERE Status = 1         -- InFlight
        ON PS_Monthly(CreatedAt);

    /* Entregas de un mensaje concreto: la vista de detalle del panel. */
    CREATE NONCLUSTERED INDEX IX_Delivery_ByMessage
        ON dbo.WebhookDelivery (MessageId)
        INCLUDE (Id, OutboundEndpointId, Status, AttemptCount)
        WITH (DATA_COMPRESSION = PAGE) ON PS_Monthly(CreatedAt);

    /* Backlog por destino: alimenta el tablero y las alertas. */
    CREATE NONCLUSTERED INDEX IX_Delivery_Backlog
        ON dbo.WebhookDelivery (OutboundEndpointId, Status)
        INCLUDE (Id)
        ON PS_Monthly(CreatedAt);
END
GO

/* ------------------------------------------------------------------------
   DeliveryAttempt — un registro por intento HTTP. La tabla de mayor volumen.
   ------------------------------------------------------------------------ */

IF OBJECT_ID(N'dbo.DeliveryAttempt') IS NULL
BEGIN
    CREATE TABLE dbo.DeliveryAttempt (
        Id                  bigint IDENTITY(1,1) NOT NULL,
        StartedAt           datetime2(3)  NOT NULL,   -- columna de partición
        DeliveryId          bigint        NOT NULL,
        AttemptNumber       smallint      NOT NULL,
        DurationMs          int           NOT NULL,
        StatusCode          smallint      NULL,       -- nulo si no hubo respuesta
        ResponseHeadersJson nvarchar(4000) NULL,
        ResponseBody        nvarchar(4000) NULL,      -- truncado
        ErrorMessage        nvarchar(1000) NULL,
        WorkerId            varchar(64)   NULL,
        CONSTRAINT PK_DeliveryAttempt PRIMARY KEY CLUSTERED (StartedAt, Id)
            WITH (DATA_COMPRESSION = PAGE) ON PS_Monthly(StartedAt)
    ) ON PS_Monthly(StartedAt);

    /* Historial de una entrega: lo que se muestra al depurar. */
    CREATE NONCLUSTERED INDEX IX_Attempt_ByDelivery
        ON dbo.DeliveryAttempt (DeliveryId, AttemptNumber)
        INCLUDE (StatusCode, DurationMs, StartedAt)
        WITH (DATA_COMPRESSION = PAGE) ON PS_Monthly(StartedAt);
END
GO
GO

-- =====================================================================
-- 03-users-audit.sql
-- =====================================================================
PRINT '-> 03-users-audit.sql';
GO
/*
    WebhookGateway — usuarios del panel y auditoría. Idempotente.
    Single-tenant: son las pocas personas del equipo que administran integraciones.
*/

SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.AppUser') IS NULL
CREATE TABLE dbo.AppUser (
    Id               int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AppUser PRIMARY KEY,
    Email            nvarchar(320) NOT NULL,
    DisplayName      nvarchar(200) NOT NULL,
    PasswordHash     nvarchar(500) NOT NULL,   -- Argon2id, formato PHC
    IsActive         bit           NOT NULL CONSTRAINT DF_AppUser_IsActive DEFAULT 1,
    IsAdmin          bit           NOT NULL CONSTRAINT DF_AppUser_IsAdmin DEFAULT 0,
    CreatedAt        datetime2(3)  NOT NULL CONSTRAINT DF_AppUser_CreatedAt DEFAULT SYSUTCDATETIME(),
    LastLoginAt      datetime2(3)  NULL,
    FailedLoginCount int           NOT NULL CONSTRAINT DF_AppUser_Failed DEFAULT 0,
    LockedUntil      datetime2(3)  NULL,
    CONSTRAINT UQ_AppUser_Email UNIQUE (Email)
);
GO

/*
    Se guarda solo el hash del token de refresco: si alguien lee la tabla, no obtiene
    tokens usables.
*/
IF OBJECT_ID(N'dbo.RefreshToken') IS NULL
BEGIN
    CREATE TABLE dbo.RefreshToken (
        Id        bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_RefreshToken PRIMARY KEY,
        UserId    int          NOT NULL CONSTRAINT FK_RefreshToken_User REFERENCES dbo.AppUser(Id),
        TokenHash binary(32)   NOT NULL,
        ExpiresAt datetime2(3) NOT NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_RefreshToken_CreatedAt DEFAULT SYSUTCDATETIME(),
        RevokedAt datetime2(3) NULL
    );

    CREATE UNIQUE NONCLUSTERED INDEX UX_RefreshToken_Hash ON dbo.RefreshToken (TokenHash);
    CREATE NONCLUSTERED INDEX IX_RefreshToken_Expiry ON dbo.RefreshToken (ExpiresAt) WHERE RevokedAt IS NULL;
END
GO

/*
    Quién cambió qué configuración. En un gateway que custodia credenciales de terceros
    esto no es opcional. ChangesJson nunca contiene secretos, solo si cambiaron.
*/
IF OBJECT_ID(N'dbo.AuditLog') IS NULL
BEGIN
    CREATE TABLE dbo.AuditLog (
        Id          bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
        OccurredAt  datetime2(3)  NOT NULL CONSTRAINT DF_AuditLog_OccurredAt DEFAULT SYSUTCDATETIME(),
        UserId      int           NULL,
        Action      varchar(100)  NOT NULL,
        EntityType  varchar(100)  NOT NULL,
        EntityId    varchar(100)  NULL,
        ChangesJson nvarchar(max) NULL,
        SourceIp    varchar(45)   NULL
    );

    CREATE NONCLUSTERED INDEX IX_AuditLog_Recent ON dbo.AuditLog (OccurredAt DESC)
        INCLUDE (UserId, Action, EntityType, EntityId);
END
GO
GO

-- =====================================================================
-- 04-partition-maintenance.sql
-- =====================================================================
PRINT '-> 04-partition-maintenance.sql';
GO
/*
    WebhookGateway — mantenimiento de particiones. Idempotente.

    Dos procedimientos, pensados para correr desde un job nocturno:

      sp_Gateway_EnsureFuturePartitions   crea meses por delante
      sp_Gateway_PurgeExpiredPartitions   vacía los meses ya vencidos

    Aquí solo se purga lo particionado. Las tablas que no lo están tienen su propio procedimiento
    en 12-purge-unpartitioned.sql, porque se borran por lotes y no con TRUNCATE.

    La purga usa TRUNCATE TABLE ... WITH (PARTITIONS ...), disponible desde SQL Server
    2016. Es una operación mínimamente registrada y no necesita tablas de staging ni
    SWITCH, así que evita por completo el crecimiento del log que provocaría un DELETE
    masivo. En una instancia compartida esa diferencia es lo que separa una purga
    invisible de una llamada del DBA.
*/

SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.sp_Gateway_EnsureFuturePartitions
    @MonthsAhead int = 6
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @target date = DATEADD(MONTH, @MonthsAhead,
        DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1));

    DECLARE @last datetime2(3) = (
        SELECT MAX(CONVERT(datetime2(3), prv.value))
        FROM sys.partition_functions pf
        JOIN sys.partition_range_values prv ON prv.function_id = pf.function_id
        WHERE pf.name = N'PF_Monthly');

    DECLARE @next date = CONVERT(date, DATEADD(MONTH, 1, @last));

    WHILE @next <= @target
    BEGIN
        DECLARE @sql nvarchar(400) =
            N'ALTER PARTITION SCHEME PS_Monthly NEXT USED [PRIMARY];' +
            N'ALTER PARTITION FUNCTION PF_Monthly() SPLIT RANGE (''' +
            CONVERT(nvarchar(10), @next, 23) + N''');';
        EXEC sp_executesql @sql;

        SET @next = DATEADD(MONTH, 1, @next);
    END
END
GO

/*
    Los días salen de dbo.RetentionPolicy (17-retention-policy.sql), que se edita desde el panel. Los
    parámetros siguen existiendo para una ejecución a mano: si se pasan, mandan sobre la tabla. Sin
    tabla ni parámetros —el script 17 sin aplicar— se usan 180/30/30.
*/
CREATE OR ALTER PROCEDURE dbo.sp_Gateway_PurgeExpiredPartitions
    @MetadataRetentionDays int = NULL,
    @PayloadRetentionDays  int = NULL,
    @AttemptRetentionDays  int = NULL,
    @DryRun                bit = 1   -- por defecto no borra: primero se mira qué haría
AS
BEGIN
    SET NOCOUNT ON;

    IF OBJECT_ID(N'dbo.RetentionPolicy') IS NOT NULL
    BEGIN
        SELECT @MetadataRetentionDays = ISNULL(@MetadataRetentionDays, MetadataDays),
               @PayloadRetentionDays  = ISNULL(@PayloadRetentionDays, PayloadDays),
               @AttemptRetentionDays  = ISNULL(@AttemptRetentionDays, AttemptDays)
        FROM dbo.RetentionPolicy
        WHERE Id = 1;
    END

    SET @MetadataRetentionDays = ISNULL(@MetadataRetentionDays, 180);
    SET @PayloadRetentionDays  = ISNULL(@PayloadRetentionDays, 30);
    SET @AttemptRetentionDays  = ISNULL(@AttemptRetentionDays, 30);

    DECLARE @plan TABLE (
        TableName  sysname,
        Partitions varchar(200),
        Cutoff     date,
        Rows       bigint);

    /*
        Una partición se puede vaciar cuando su frontera SUPERIOR ya quedó por detrás
        del corte: solo entonces todas sus filas son más viejas que la retención.
    */
    DECLARE @tables TABLE (Name sysname, RetentionDays int);
    INSERT INTO @tables (Name, RetentionDays) VALUES
        (N'WebhookPayload',  @PayloadRetentionDays),
        (N'DeliveryAttempt', @AttemptRetentionDays),
        (N'WebhookDelivery', @MetadataRetentionDays),
        (N'WebhookMessage',  @MetadataRetentionDays);

    DECLARE @name sysname, @days int;
    DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT Name, RetentionDays FROM @tables;
    OPEN cur;
    FETCH NEXT FROM cur INTO @name, @days;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        DECLARE @cutoff date = CONVERT(date, DATEADD(DAY, -@days, SYSUTCDATETIME()));

        DECLARE @parts varchar(200) = NULL, @rows bigint = 0;

        SELECT
            @parts = STRING_AGG(CONVERT(varchar(10), p.partition_number), ','),
            @rows  = SUM(p.rows)
        FROM sys.partitions p
        JOIN sys.indexes i ON i.object_id = p.object_id AND i.index_id = p.index_id
        JOIN sys.partition_range_values prv
             ON prv.boundary_id = p.partition_number
        JOIN sys.partition_schemes ps ON ps.data_space_id = i.data_space_id
        JOIN sys.partition_functions pf
             ON pf.function_id = ps.function_id AND pf.function_id = prv.function_id
        WHERE p.object_id = OBJECT_ID(N'dbo.' + @name)
          AND i.index_id <= 1
          AND p.rows > 0
          AND CONVERT(date, CONVERT(datetime2(3), prv.value)) <= @cutoff;

        IF @parts IS NOT NULL
        BEGIN
            INSERT INTO @plan VALUES (@name, @parts, @cutoff, @rows);

            IF @DryRun = 0
            BEGIN
                DECLARE @truncate nvarchar(600) =
                    N'TRUNCATE TABLE dbo.' + QUOTENAME(@name) +
                    N' WITH (PARTITIONS (' + @parts + N'));';
                EXEC sp_executesql @truncate;
            END
        END

        FETCH NEXT FROM cur INTO @name, @days;
    END

    CLOSE cur;
    DEALLOCATE cur;

    /*
        Las tablas sin particionar —MessageDedupe, RefreshToken, NotificationLog, AuditLog y los
        muertos de NotificationOutbox— se purgan en sp_Gateway_PurgeUnpartitionedLogs
        (12-purge-unpartitioned.sql). Se borran fila a fila por lotes, que es otro mecanismo, y el
        job nocturno llama a los dos procedimientos.
    */
    SELECT TableName, Partitions, Cutoff, Rows, WouldDelete = @DryRun FROM @plan;
END
GO

-- =====================================================================
-- 06-delivery-by-id.sql
-- =====================================================================
PRINT '-> 06-delivery-by-id.sql';
GO
/*
    WebhookGateway — índice de búsqueda de una entrega por su id. Idempotente.

    Va en un script propio porque 02-traffic-tables.sql crea sus índices dentro del
    bloque que solo se ejecuta cuando la tabla no existe: añadirlo allí no llegaría
    nunca a una base de datos ya creada.

    Motivo: WebhookDelivery se agrupa por (CreatedAt, Id), como exige el particionado.
    Buscar por Id solo —lo que hace el reenvío manual del panel— recorrería la tabla
    entera. Es el mismo problema que UX_WebhookMessage_Id resuelve para los mensajes,
    y se resuelve igual.
*/

SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.WebhookDelivery') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'UX_Delivery_Id'
                     AND object_id = OBJECT_ID(N'dbo.WebhookDelivery'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_Delivery_Id
        ON dbo.WebhookDelivery (Id, CreatedAt)
        INCLUDE (MessageId, OutboundEndpointId, Status)
        WITH (DATA_COMPRESSION = PAGE) ON PS_Monthly(CreatedAt);
END
GO
GO

-- =====================================================================
-- 07-notifications.sql
-- =====================================================================
PRINT '-> 07-notifications.sql';
GO
/*
    WebhookGateway — sistema de notificaciones y outbox de alertas. Idempotente.

    Separa la cola de salida transitoria (NotificationOutbox) del registro inmutable
    de auditoría de envíos (NotificationLog).

    Diseñado para drenado concurrente no bloqueante mediante UPDLOCK y READPAST.
*/

SET NOCOUNT ON;
GO

/* ------------------------------------------------------------------------
   1. NotificationOutbox — bandeja de salida transitoria de notificaciones
   ------------------------------------------------------------------------ */

IF OBJECT_ID(N'dbo.NotificationOutbox') IS NULL
BEGIN
    CREATE TABLE dbo.NotificationOutbox (
        Id                 bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_NotificationOutbox PRIMARY KEY CLUSTERED,
        CreatedAt          datetime2(3)  NOT NULL CONSTRAINT DF_NotificationOutbox_CreatedAt DEFAULT SYSUTCDATETIME(),
        ChannelType        tinyint       NOT NULL CONSTRAINT DF_NotificationOutbox_Channel DEFAULT 0, -- 0=Email, 1=Webhook
        Recipient          nvarchar(320) NOT NULL,
        IntegrationId      int           NULL,
        OutboundEndpointId int           NULL,
        DeliveryId         bigint        NULL,
        AlertType          tinyint       NOT NULL, -- 1=DeadLetter, 2=EndpointDegraded, 3=EndpointDown, 4=EndpointRecovered, 5=InboundError, 6=VerificationOptIn
        Subject            nvarchar(300) NOT NULL,
        MetadataJson       nvarchar(max) NOT NULL, -- Payload JSON con detalles del incidente (sin datos sensibles)
        Status             tinyint       NOT NULL CONSTRAINT DF_NotificationOutbox_Status DEFAULT 0, -- 0=Pending, 1=InFlight, 2=Retrying, 3=DeadLetter
        AttemptCount       smallint      NOT NULL CONSTRAINT DF_NotificationOutbox_Attempts DEFAULT 0,
        NextAttemptAt      datetime2(3)  NOT NULL CONSTRAINT DF_NotificationOutbox_NextAttempt DEFAULT SYSUTCDATETIME(),
        LeaseUntil         datetime2(3)  NULL,
        WorkerId           varchar(64)   NULL,
        LastError          nvarchar(1000) NULL
    );

    /*
        Índice filtrado del worker drenador: solo busca lo que está listo para enviar.
        Mantiene el costo de consulta mínimo sin importar cuántas filas pasen por la tabla.
    */
    CREATE NONCLUSTERED INDEX IX_NotificationOutbox_Drain
        ON dbo.NotificationOutbox (NextAttemptAt)
        INCLUDE (Id, ChannelType, Recipient, IntegrationId, OutboundEndpointId, DeliveryId, AlertType, Subject, MetadataJson, AttemptCount)
        WHERE Status IN (0, 2);

    /* Recuperación de leases huérfanos si el worker se recicla */
    CREATE NONCLUSTERED INDEX IX_NotificationOutbox_Lease
        ON dbo.NotificationOutbox (LeaseUntil)
        INCLUDE (Id)
        WHERE Status = 1;
END
GO

/* ------------------------------------------------------------------------
   2. NotificationLog — auditoría histórica inmutable de notificaciones
   ------------------------------------------------------------------------ */

IF OBJECT_ID(N'dbo.NotificationLog') IS NULL
BEGIN
    CREATE TABLE dbo.NotificationLog (
        Id                 bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_NotificationLog PRIMARY KEY CLUSTERED,
        SentAt             datetime2(3)  NOT NULL CONSTRAINT DF_NotificationLog_SentAt DEFAULT SYSUTCDATETIME(),
        ChannelType        tinyint       NOT NULL,
        Recipient          nvarchar(320) NOT NULL,
        IntegrationId      int           NULL,
        OutboundEndpointId int           NULL,
        DeliveryId         bigint        NULL,
        AlertType          tinyint       NOT NULL,
        Subject            nvarchar(300) NOT NULL,
        SmtpMessageId      nvarchar(255) NULL, -- Message-ID devuelto por Turbo SMTP (<...@turbo-smtp.com>)
        Status             tinyint       NOT NULL, -- 0=Delivered, 1=BouncedOrRejected, 2=FailedPermanent
        ErrorMessage       nvarchar(1000) NULL
    );

    /* Búsqueda rápida de historial de alertas recientes */
    CREATE NONCLUSTERED INDEX IX_NotificationLog_SentAt
        ON dbo.NotificationLog (SentAt DESC)
        INCLUDE (Recipient, IntegrationId, AlertType, Status);

    /* Historial filtrado por integración para el panel */
    CREATE NONCLUSTERED INDEX IX_NotificationLog_Integration
        ON dbo.NotificationLog (IntegrationId, SentAt DESC)
        WHERE IntegrationId IS NOT NULL;
END
GO
GO

-- =====================================================================
-- 08-endpoint-health.sql
-- =====================================================================
PRINT '-> 08-endpoint-health.sql';
GO
/*
    WebhookGateway — estado de salud persistido de endpoints de destino. Idempotente.
    Gobierna la máquina de estados: Healthy (0), Degraded (1), Down (2), Recovered (3).
    Permite suprimir spam disparando alertas exclusivamente durante las transiciones de estado.
*/

SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.EndpointHealthState') IS NULL
BEGIN
    CREATE TABLE dbo.EndpointHealthState (
        OutboundEndpointId   int            NOT NULL CONSTRAINT PK_EndpointHealthState PRIMARY KEY CLUSTERED,
        HealthStatus         tinyint        NOT NULL CONSTRAINT DF_Health_Status DEFAULT 0, -- 0=Healthy, 1=Degraded, 2=Down, 3=Recovered
        ConsecutiveFailures  int            NOT NULL CONSTRAINT DF_Health_Failures DEFAULT 0,
        ConsecutiveSuccesses int            NOT NULL CONSTRAINT DF_Health_Successes DEFAULT 0,
        LastTransitionAt     datetime2(3)   NOT NULL CONSTRAINT DF_Health_Transition DEFAULT SYSUTCDATETIME(),
        LastAlertSentAt      datetime2(3)   NULL,
        LastErrorMessage     nvarchar(1000) NULL,
        LastStatusCode       smallint       NULL,
        CONSTRAINT FK_HealthState_Endpoint FOREIGN KEY (OutboundEndpointId) REFERENCES dbo.OutboundEndpoint(Id) ON DELETE CASCADE
    );
END
GO
GO

-- =====================================================================
-- 09-subscribers.sql
-- =====================================================================
PRINT '-> 09-subscribers.sql';
GO
/*
    WebhookGateway — suscriptores de correo por integración con doble opt-in y baja. Idempotente.
    Cumple con la Ley 1581 (Habeas Data) y estándares anti-spam (RFC 8058).
*/

SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.IntegrationEmailSubscriber') IS NULL
BEGIN
    CREATE TABLE dbo.IntegrationEmailSubscriber (
        Id                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_IntegrationEmailSubscriber PRIMARY KEY CLUSTERED,
        IntegrationId         int               NOT NULL,
        Email                 nvarchar(320)     NOT NULL,
        Status                tinyint           NOT NULL CONSTRAINT DF_Subscriber_Status DEFAULT 0, -- 0=PendingVerification, 1=Verified, 2=Silenced, 3=Unsubscribed
        VerificationToken     varchar(64)       NULL,
        VerificationExpiresAt datetime2(3)      NULL,
        UnsubscribeToken      varchar(64)       NOT NULL,
        CreatedAt             datetime2(3)      NOT NULL CONSTRAINT DF_Subscriber_CreatedAt DEFAULT SYSUTCDATETIME(),
        VerifiedAt            datetime2(3)      NULL,
        CreatedBy             nvarchar(200)     NOT NULL, -- Usuario del panel que lo registró
        CONSTRAINT FK_Subscriber_Integration FOREIGN KEY (IntegrationId) REFERENCES dbo.Integration(Id) ON DELETE CASCADE,
        CONSTRAINT UQ_Subscriber_Integration_Email UNIQUE (IntegrationId, Email)
    );

    CREATE UNIQUE NONCLUSTERED INDEX UX_Subscriber_VerificationToken
        ON dbo.IntegrationEmailSubscriber (VerificationToken)
        WHERE VerificationToken IS NOT NULL;

    CREATE UNIQUE NONCLUSTERED INDEX UX_Subscriber_UnsubscribeToken
        ON dbo.IntegrationEmailSubscriber (UnsubscribeToken);

    CREATE NONCLUSTERED INDEX IX_Subscriber_ActiveByIntegration
        ON dbo.IntegrationEmailSubscriber (IntegrationId, Status)
        INCLUDE (Email, UnsubscribeToken);
END
GO
GO

-- =====================================================================
-- 10-traffic-write-inbound.sql
-- =====================================================================
PRINT '-> 10-traffic-write-inbound.sql';
GO
/*
    WebhookGateway — Procedimiento de Ingesta Atómica de Tráfico.
    Idempotente. Inserta mensaje, payload, deduplicación y entregas en un solo viaje de red.
*/

SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.sp_Traffic_WriteInbound
    @InboundEndpointId    int,
    @ReceivedAt           datetime2(3),
    @SourceIp             varchar(45),
    @HttpMethod           varchar(10),
    @HeadersJson          nvarchar(max),
    @QueryString          nvarchar(2000),
    @BodySizeBytes        int,
    @BodyHash             binary(32),
    @DedupeKey            varchar(200),
    @DedupeExpiresAt      datetime2(3),
    @PayloadEncoding      tinyint,
    @PayloadSizeBytes     int,
    @PayloadBody          varbinary(max),
    @PayloadStorageRef    nvarchar(500)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @MessageId bigint;
    DECLARE @Status tinyint = 0; -- Received
    DECLARE @ExistingMessageId bigint = NULL;
    DECLARE @CreatedDeliveries TABLE (Id bigint);

    BEGIN TRANSACTION;

    /*
        1. Reservar la clave de deduplicación ANTES de tocar nada más.

        UPDLOCK, HOLDLOCK sobre la clave agrupada PK_MessageDedupe (InboundEndpointId, DedupeKey)
        toma un bloqueo de rango: si dos peticiones con la misma clave llegan a la vez, la segunda
        espera aquí, y cuando entra ya ve la fila de la primera y se marca como duplicada.

        No se usa TRY/CATCH para detectar el 2627/2601. Con SET XACT_ABORT ON la transacción queda
        inservible dentro del CATCH (error 3930), así que el duplicado simultáneo acababa en un 503
        en vez de en una respuesta idempotente. Aquí el bloqueo hace que la violación de clave no
        pueda ocurrir, en vez de recogerla después.

        El bloqueo se toma primero, antes de cualquier escritura, para que todas las transacciones
        pidan los recursos en el mismo orden y no haya inversión que acabe en deadlock.
    */
    IF @DedupeKey IS NOT NULL
    BEGIN
        SELECT @ExistingMessageId = MessageId
        FROM dbo.MessageDedupe WITH (UPDLOCK, HOLDLOCK)
        WHERE InboundEndpointId = @InboundEndpointId AND DedupeKey = @DedupeKey;

        IF @ExistingMessageId IS NOT NULL
        BEGIN
            SET @Status = 1; -- Duplicate
        END
    END

    -- 2. Insertar el mensaje. El duplicado también se registra: saber que llegó dos veces es parte
    --    de lo que se consulta al depurar.
    INSERT INTO dbo.WebhookMessage
        (ReceivedAt, InboundEndpointId, SourceIp, HttpMethod, HeadersJson, QueryString, BodySizeBytes, BodyHash, Status)
    VALUES
        (@ReceivedAt, @InboundEndpointId, @SourceIp, @HttpMethod, @HeadersJson, @QueryString, @BodySizeBytes, @BodyHash, @Status);

    SET @MessageId = SCOPE_IDENTITY();

    -- 3. Insertar el payload
    INSERT INTO dbo.WebhookPayload
        (MessageId, ReceivedAt, Encoding, SizeBytes, Body, StorageRef)
    VALUES
        (@MessageId, @ReceivedAt, @PayloadEncoding, @PayloadSizeBytes, @PayloadBody, @PayloadStorageRef);

    -- 4. Guardar la clave reservada en el paso 1. Con el rango bloqueado, nadie ha podido
    --    insertarla entre medias: el INSERT no necesita red de seguridad.
    IF @DedupeKey IS NOT NULL AND @Status <> 1
    BEGIN
        INSERT INTO dbo.MessageDedupe (InboundEndpointId, DedupeKey, MessageId, ExpiresAt)
        VALUES (@InboundEndpointId, @DedupeKey, @MessageId, @DedupeExpiresAt);
    END

    -- 5. Crear entregas solo si NO es duplicado
    IF @Status <> 1
    BEGIN
        INSERT INTO dbo.WebhookDelivery
            (CreatedAt, MessageId, OutboundEndpointId, Status, NextAttemptAt, ExpiresAt)
        OUTPUT INSERTED.Id INTO @CreatedDeliveries(Id)
        SELECT
            @ReceivedAt,
            @MessageId,
            s.OutboundEndpointId,
            0, -- Pending
            @ReceivedAt,
            DATEADD(HOUR, o.DeliveryWindowHours, @ReceivedAt)
        FROM dbo.Subscription s
        INNER JOIN dbo.OutboundEndpoint o ON o.Id = s.OutboundEndpointId
        WHERE s.InboundEndpointId = @InboundEndpointId
          AND s.IsActive = 1
          AND o.IsActive = 1;

        -- Si no hubo ninguna suscripción activa, marcar como NoSubscriptions (2)
        IF @@ROWCOUNT = 0
        BEGIN
            SET @Status = 2; -- NoSubscriptions
            UPDATE dbo.WebhookMessage
            SET Status = 2
            WHERE ReceivedAt = @ReceivedAt AND Id = @MessageId;
        END
    END

    COMMIT TRANSACTION;

    -- Resultado 1: Cabecera del resultado
    SELECT @MessageId AS MessageId, @Status AS Status, @ExistingMessageId AS ExistingMessageId;

    -- Resultado 2: Identificadores de entregas creadas
    SELECT Id FROM @CreatedDeliveries;
END
GO
GO

-- =====================================================================
-- 11-delivery-dispatch-by-endpoint.sql
-- =====================================================================
PRINT '-> 11-delivery-dispatch-by-endpoint.sql';
GO
/*
    WebhookGateway — índice del claim por destino. Idempotente.

    El despachador pasó de reclamar un lote global y repartirlo con ROW_NUMBER() a reclamar por
    destino, cada uno a su ritmo. Ese cambio necesita otro orden de claves.

    IX_Delivery_Dispatch va por (NextAttemptAt, OutboundEndpointId): servía al claim global, que
    arrancaba por fecha. Un claim de un solo destino con ese índice tiene que recorrer todo lo
    vencido y descartar lo que no es suyo, de modo que el destino con cinco entregas paga el
    backlog de los demás. Con (OutboundEndpointId, NextAttemptAt) es una búsqueda directa.

    El índice antiguo NO se borra aquí a propósito: mientras en producción siga corriendo el
    código anterior, es el que sostiene su claim. Se puede borrar después del despliegue de la
    fase 5, y está anotado como tarea en docs/plan-escala-400k.md.
*/

SET NOCOUNT ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Delivery_DispatchByEndpoint')
BEGIN
    /*
        Filtrado por Status IN (0, 2) —Pending y Retrying— para que el índice solo contenga lo
        que está pendiente: cuesta lo mismo con la tabla vacía que con quince millones de filas.

        Alineado con PS_Monthly, así que CreatedAt entra en el índice por ser la columna de
        partición. Eso es lo que permite que el claim devuelva la clave completa (CreatedAt, Id)
        sin ir a la tabla.
    */
    CREATE NONCLUSTERED INDEX IX_Delivery_DispatchByEndpoint
        ON dbo.WebhookDelivery (OutboundEndpointId, NextAttemptAt)
        INCLUDE (Id, MessageId, AttemptCount, ExpiresAt)
        WHERE Status IN (0, 2)
        WITH (DATA_COMPRESSION = PAGE)
        ON PS_Monthly(CreatedAt);
END
GO

-- =====================================================================
-- 12-purge-unpartitioned.sql
-- =====================================================================
PRINT '-> 12-purge-unpartitioned.sql';
GO
/*
    WebhookGateway — purga de las tablas que no están particionadas. Idempotente.

    Las de tráfico se vacían con TRUNCATE de particiones enteras (04-partition-maintenance.sql).
    Estas no: son pequeñas, no están particionadas y hay que borrarlas fila a fila, así que el
    mecanismo es otro —DELETE por lotes con un descanso entre lotes— y vive aparte.

    Aquí estaban sin purgar NotificationLog y AuditLog, que crecen para siempre, y los muertos de
    NotificationOutbox (Status = 3), que el worker deja ahí cuando agota los intentos de envío. Las
    de MessageDedupe y RefreshToken se movieron desde sp_Gateway_PurgeExpiredPartitions, porque son
    exactamente este mismo mecanismo y no tenían nada que hacer dentro de la purga de particiones.

    Por qué por lotes y con descanso: un DELETE de un millón de filas de golpe escala a bloqueo de
    tabla y hace crecer el log de transacciones. En una instancia compartida, eso es la diferencia
    entre una purga invisible y una llamada del DBA.
*/

SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.sp_Gateway_PurgeUnpartitionedLogs
    @NotificationRetentionDays int = 90,
    @AuditRetentionDays        int = 365,  -- el registro de auditoría se guarda más: es rastro de quién tocó qué
    @RefreshTokenGraceDays     int = 30,
    @BatchSize                 int = 10000,
    @DryRun                    bit = 1     -- por defecto no borra: primero se mira qué haría
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @now datetime2(3) = SYSUTCDATETIME();
    DECLARE @plan TABLE (TableName sysname, DateColumn sysname, Cutoff datetime2(3), Rows bigint);

    /*
        La lista es fija y está escrita aquí: ni los nombres de tabla ni el predicado extra vienen
        de un parámetro, así que el SQL dinámico de abajo no tiene por dónde inyectarse. Las fechas
        sí son parámetros, y van como parámetros de sp_executesql, no concatenadas.
    */
    DECLARE @targets TABLE (
        Seq        int IDENTITY(1,1),
        Name       sysname,
        DateColumn sysname,
        Cutoff     datetime2(3),
        Extra      nvarchar(200) NULL);

    INSERT INTO @targets (Name, DateColumn, Cutoff, Extra) VALUES
        -- Claves de deduplicación: ya vencidas, no hay nada que deduplicar con ellas.
        (N'MessageDedupe',      N'ExpiresAt',  @now,                                                      NULL),
        -- Tokens de refresco caducados, con margen para poder investigar un uso raro.
        (N'RefreshToken',       N'ExpiresAt',  DATEADD(DAY, -@RefreshTokenGraceDays, @now),               NULL),
        -- Historial de alertas enviadas.
        (N'NotificationLog',    N'SentAt',     DATEADD(DAY, -@NotificationRetentionDays, @now),           NULL),
        -- Alertas que nunca se pudieron enviar. Solo las muertas: las vivas las drena el worker.
        (N'NotificationOutbox', N'CreatedAt',  DATEADD(DAY, -@NotificationRetentionDays, @now),           N'AND Status = 3'),
        -- Auditoría del panel.
        (N'AuditLog',           N'OccurredAt', DATEADD(DAY, -@AuditRetentionDays, @now),                  NULL);

    DECLARE @seq int, @name sysname, @col sysname, @cutoff datetime2(3), @extra nvarchar(200);
    DECLARE @sql nvarchar(max), @rows bigint;

    DECLARE cur CURSOR LOCAL FAST_FORWARD FOR
        SELECT Seq, Name, DateColumn, Cutoff, Extra FROM @targets ORDER BY Seq;

    OPEN cur;
    FETCH NEXT FROM cur INTO @seq, @name, @col, @cutoff, @extra;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @rows = 0;

        IF OBJECT_ID(N'dbo.' + @name) IS NOT NULL
        BEGIN
            IF @DryRun = 1
            BEGIN
                -- En seco se cuenta, que es justo para lo que sirve la semana de @DryRun = 1.
                SET @sql = N'SELECT @out = COUNT_BIG(1) FROM dbo.' + QUOTENAME(@name) + N' WITH (NOLOCK)'
                         + N' WHERE ' + QUOTENAME(@col) + N' < @cutoff ' + ISNULL(@extra, N'') + N';';

                EXEC sp_executesql @sql,
                    N'@cutoff datetime2(3), @out bigint OUTPUT',
                    @cutoff = @cutoff, @out = @rows OUTPUT;
            END
            ELSE
            BEGIN
                /*
                    El bucle va dentro del SQL dinámico para que todo esto sea una sola llamada en
                    vez de una por lote. Devuelve lo que borró de verdad, no una estimación.
                */
                SET @sql = N'
DECLARE @deleted int = 1, @total bigint = 0;
WHILE @deleted > 0
BEGIN
    DELETE TOP (@batch) FROM dbo.' + QUOTENAME(@name) + N'
    WHERE ' + QUOTENAME(@col) + N' < @cutoff ' + ISNULL(@extra, N'') + N';

    SET @deleted = @@ROWCOUNT;
    SET @total += @deleted;

    IF @deleted > 0 WAITFOR DELAY ''00:00:00.050'';
END
SET @out = @total;';

                EXEC sp_executesql @sql,
                    N'@cutoff datetime2(3), @batch int, @out bigint OUTPUT',
                    @cutoff = @cutoff, @batch = @BatchSize, @out = @rows OUTPUT;
            END

            INSERT INTO @plan (TableName, DateColumn, Cutoff, Rows)
            VALUES (@name, @col, @cutoff, @rows);
        END

        FETCH NEXT FROM cur INTO @seq, @name, @col, @cutoff, @extra;
    END

    CLOSE cur;
    DEALLOCATE cur;

    SELECT TableName, DateColumn, Cutoff, Rows, WouldDelete = @DryRun
    FROM @plan
    ORDER BY TableName;
END
GO

-- =====================================================================
-- 13-watchdog.sql
-- =====================================================================
PRINT '-> 13-watchdog.sql';
GO
/*
    WebhookGateway — vigilante. Idempotente.

    Comprueba lo que, si pasa desapercibido, se convierte en pérdida de mensajes:

      1. Que no se acaben las particiones futuras. Si se acaban, los INSERT de tráfico empiezan a
         fallar y la recepción devuelve 503 a todo el mundo. Es el fallo más grave posible aquí y el
         más fácil de evitar, porque se ve venir con meses de antelación.
      2. Que el backlog de entregas no crezca sin parar. Un backlog que sube y no baja significa que
         el despachador no da el ritmo o que un destino está caído.
      3. Que ninguna entrega lleve esperando más de lo acordado (15 minutos por defecto). Es la
         cifra que de verdad dice si vamos al día: un backlog grande que se vacía a tiempo no es un
         problema, y uno pequeño que no avanza sí.
      4. Que no queden leases huérfanos.

    También define dbo.fn_Gateway_DeliveryLag, que el panel usa para mostrar el retraso de cada
    destino. Va aquí, antes del procedimiento, porque el vigilante la necesita.

    Cómo avisa: devuelve una fila por problema y, si hay alguno, lanza RAISERROR con severidad 16.
    Eso hace que el job de SQL Agent **falle**, y un job que falla sale en su historial y puede
    notificar a un operador. Se hace así a propósito, en vez de con sp_send_dbmail: no depende de
    que Database Mail esté configurado en esta instancia, que es compartida y no es nuestra.

    Conectar el aviso a un correo es el paso del DBA: definir un operador y marcar "notificar al
    fallar" en el job. Ver 14-sql-agent-jobs.sql.
*/

SET NOCOUNT ON;
GO

/*
    Retraso de cada destino: cuánto lleva esperando la entrega vencida más antigua que aún no se ha
    enviado. Solo salen los destinos que tienen alguna.

    Es MIN(NextAttemptAt) por destino, pero escrito así por una razón medida: el índice está partido
    por mes, y un MIN o un TOP (1) ORDER BY sobre todas las particiones obliga al motor a leer todo
    lo pendiente del destino —con 300.000 pendientes, 1.233 páginas—. Pedido partición por partición,
    cada una es una búsqueda que para en la primera fila: 80 páginas para el mismo resultado.
*/
CREATE OR ALTER FUNCTION dbo.fn_Gateway_DeliveryLag (@Now datetime2(3))
RETURNS TABLE
AS
RETURN
    SELECT
        e.Id                                    AS OutboundEndpointId,
        MIN(x.NextAttemptAt)                    AS OldestDueAt,
        DATEDIFF(SECOND, MIN(x.NextAttemptAt), @Now) AS LagSeconds
    FROM dbo.OutboundEndpoint AS e
    CROSS JOIN (
        SELECT p.partition_number
        FROM sys.partitions AS p
        WHERE p.object_id = OBJECT_ID(N'dbo.WebhookDelivery') AND p.index_id = 1
    ) AS pt
    CROSS APPLY (
        SELECT TOP (1) d.NextAttemptAt
        FROM dbo.WebhookDelivery AS d WITH (INDEX(IX_Delivery_DispatchByEndpoint))
        WHERE d.OutboundEndpointId = e.Id
          AND d.Status IN (0, 2)
          AND d.NextAttemptAt <= @Now
          AND d.ExpiresAt > @Now
          AND $PARTITION.PF_Monthly(d.CreatedAt) = pt.partition_number
        ORDER BY d.NextAttemptAt
    ) AS x
    GROUP BY e.Id;
GO

CREATE OR ALTER PROCEDURE dbo.sp_Gateway_Watchdog
    @MinMonthsOfPartitions int = 3,
    @MaxBacklog            int = 50000,
    @MaxLagMinutes         int = 15
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

    /* ---------- 3. Retraso de entrega ---------- */

    DECLARE @peorDestino nvarchar(200), @peorRetraso int;

    SELECT TOP (1) @peorDestino = o.Name, @peorRetraso = l.LagSeconds
    FROM dbo.fn_Gateway_DeliveryLag(SYSUTCDATETIME()) AS l
    JOIN dbo.OutboundEndpoint AS o ON o.Id = l.OutboundEndpointId
    ORDER BY l.LagSeconds DESC;

    IF @peorRetraso > @MaxLagMinutes * 60
    BEGIN
        INSERT INTO @problemas VALUES ('Retraso',
            N'El destino "' + @peorDestino + N'" tiene entregas esperando desde hace '
            + CONVERT(nvarchar(20), @peorRetraso / 60) + N' minutos, por encima del umbral de '
            + CONVERT(nvarchar(20), @MaxLagMinutes) + N'. O el destino no da abasto con su ritmo y '
            + N'concurrencia, o el despachador no está corriendo.');
    END

    /* ---------- 4. Leases huérfanos ---------- */

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
        PeorRetrasoMin  = @peorRetraso / 60,
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

-- =====================================================================
-- 16-inbound-transient-status.sql
-- =====================================================================
PRINT '-> 16-inbound-transient-status.sql';
GO
IF COL_LENGTH(N'dbo.InboundEndpoint', N'TransientFailureStatusCode') IS NULL
BEGIN
    ALTER TABLE dbo.InboundEndpoint ADD TransientFailureStatusCode smallint NULL;
END
GO

-- =====================================================================
-- 17-retention-policy.sql
-- =====================================================================
PRINT '-> 17-retention-policy.sql';
GO
/*
    WebhookGateway — retención editable desde el panel. Idempotente.

    Una sola fila con los días que se guarda cada cosa. La purga nocturna
    (sp_Gateway_PurgeExpiredPartitions, en 04) la lee en cada ejecución, así que cambiarla en el panel
    surte efecto la noche siguiente sin tocar el job.

    Es global y no por integración, y no por gusto: la purga vacía particiones mensuales enteras con
    TRUNCATE, y una partición lleva mezcladas todas las integraciones de ese mes. Retener una
    integración más que otra exigiría borrar fila a fila, que es justo lo que la purga por
    particiones evita en un servidor compartido.

    Valores iniciales: mensajes y entregas 180 días, cuerpos 30, intentos 30.

    Compatible con el código desplegado: tabla nueva que la versión anterior no conoce. Aplicar
    antes de publicar la versión que la usa.
*/

SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.RetentionPolicy') IS NULL
BEGIN
    CREATE TABLE dbo.RetentionPolicy (
        Id           tinyint       NOT NULL CONSTRAINT PK_RetentionPolicy PRIMARY KEY
                                            CONSTRAINT CK_RetentionPolicy_Single CHECK (Id = 1),
        MetadataDays int           NOT NULL,   -- WebhookMessage y WebhookDelivery
        PayloadDays  int           NOT NULL,   -- WebhookPayload
        AttemptDays  int           NOT NULL,   -- DeliveryAttempt
        UpdatedAt    datetime2(3)  NOT NULL CONSTRAINT DF_RetentionPolicy_UpdatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedBy    nvarchar(200) NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.RetentionPolicy)
BEGIN
    INSERT INTO dbo.RetentionPolicy (Id, MetadataDays, PayloadDays, AttemptDays)
    VALUES (1, 180, 30, 30);
END
GO

-- =====================================================================
-- 18-capacity-report.sql
-- =====================================================================
PRINT '-> 18-capacity-report.sql';
GO
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

-- =====================================================================
-- 14-sql-agent-jobs.sql NO se aplica aqui a proposito: crea jobs en msdb,
-- que son del servidor y no de la base, y necesita SQLAgentUserRole.
-- Se ejecuta aparte y a conciencia.
-- =====================================================================

PRINT 'Esquema aplicado. Comprobacion:';
SELECT [Base] = DB_NAME(),
       [Tablas] = (SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0),
       [Procedimientos] = (SELECT COUNT(*) FROM sys.procedures WHERE is_ms_shipped = 0),
       [RCSI] = is_read_committed_snapshot_on,
       [Recuperacion] = recovery_model_desc
FROM sys.databases WHERE name = DB_NAME();
GO
SET NOEXEC OFF;
GO
