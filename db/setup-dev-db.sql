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

CREATE OR ALTER PROCEDURE dbo.sp_Gateway_PurgeExpiredPartitions
    @MetadataRetentionDays int = 365,
    @PayloadRetentionDays  int = 30,
    @AttemptRetentionDays  int = 90,
    @DryRun                bit = 1   -- por defecto no borra: primero se mira qué haría
AS
BEGIN
    SET NOCOUNT ON;

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

    /* Purga por lotes con descanso para evitar contención de candados en ráfagas nocturnas */
    IF @DryRun = 0
    BEGIN
        DECLARE @deletedDedupe int = 1;
        WHILE @deletedDedupe > 0
        BEGIN
            DELETE TOP (10000) FROM dbo.MessageDedupe WHERE ExpiresAt < SYSUTCDATETIME();
            SET @deletedDedupe = @@ROWCOUNT;
            IF @deletedDedupe > 0
                WAITFOR DELAY '00:00:00.050';
        END;

        DECLARE @deletedRefresh int = 1;
        WHILE @deletedRefresh > 0
        BEGIN
            DELETE TOP (10000) FROM dbo.RefreshToken WHERE ExpiresAt < DATEADD(DAY, -30, SYSUTCDATETIME());
            SET @deletedRefresh = @@ROWCOUNT;
            IF @deletedRefresh > 0
                WAITFOR DELAY '00:00:00.050';
        END;
    END

    SELECT TableName, Partitions, Cutoff, Rows, WouldDelete = @DryRun FROM @plan;
END
GO
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
