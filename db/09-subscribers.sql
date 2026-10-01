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
