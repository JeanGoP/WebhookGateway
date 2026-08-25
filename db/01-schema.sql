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
