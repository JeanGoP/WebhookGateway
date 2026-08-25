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
