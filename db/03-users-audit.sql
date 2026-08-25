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
