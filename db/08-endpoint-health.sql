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
