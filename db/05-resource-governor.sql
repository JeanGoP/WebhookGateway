/*
    WebhookGateway — Resource Governor. OPCIONAL, y solo para producción.

    ============================================================================
    NO HACE FALTA PARA DESARROLLAR. Sáltate este script hasta que despliegues.
    ============================================================================

    Qué hace: encajona la carga del gateway para que ni en el peor pico pueda
    robarle CPU, memoria o E/S a las bases grandes de la instancia. Es el
    argumento que convierte la conversación con el DBA de «no me metas eso aquí»
    a «vale, si está capado».

    Requiere Enterprise Edition y permisos de servidor (CONTROL SERVER), así que
    normalmente lo aplica el DBA, no tú.

    ----------------------------------------------------------------------------
    Clasificación por NOMBRE DE APLICACIÓN, no por login
    ----------------------------------------------------------------------------
    La aplicación se conecta con el login que autorice la empresa, y ese login
    puede cambiar. Por eso el clasificador no mira quién se conecta sino QUÉ se
    conecta: la cadena de conexión incluye

        Application Name=WebhookGateway

    y cualquier conexión con ese nombre cae en el pool capado.

    Un detalle importante: el nombre de aplicación lo pone el cliente, así que es
    falsificable. No pasa nada — Resource Governor no es una frontera de
    seguridad, es un tope de recursos. Falsificarlo solo sirve para caparse a uno
    mismo.
*/

USE master;
GO
SET NOCOUNT ON;
GO

/* ------------------------------------------------------------------------
   1. Pool con los topes
   ------------------------------------------------------------------------
   Generosos para el volumen real (0,23 mensajes/segundo de media). Si el
   gateway llega a rozarlos, el problema está en el código, no en el tope.
*/

IF NOT EXISTS (SELECT 1 FROM sys.resource_governor_resource_pools WHERE name = N'GatewayPool')
BEGIN
    CREATE RESOURCE POOL GatewayPool
    WITH (
        MIN_CPU_PERCENT     = 0,
        MAX_CPU_PERCENT     = 15,
        MIN_MEMORY_PERCENT  = 0,
        MAX_MEMORY_PERCENT  = 10,
        MAX_IOPS_PER_VOLUME = 500
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.resource_governor_workload_groups WHERE name = N'GatewayGroup')
BEGIN
    CREATE WORKLOAD GROUP GatewayGroup
    WITH (
        IMPORTANCE = LOW,
        REQUEST_MAX_MEMORY_GRANT_PERCENT = 10,
        MAX_DOP = 1   -- nada de paralelismo: son consultas puntuales y cortas
    )
    USING GatewayPool;
END
GO

/* ------------------------------------------------------------------------
   2. Clasificador
   ------------------------------------------------------------------------
   Debe ser rápido y no fallar nunca: se ejecuta en CADA conexión nueva a la
   instancia, para todas las bases de datos. Un clasificador lento o con errores
   degrada el servidor entero. Por eso aquí solo hay una comparación de cadenas.
*/

CREATE OR ALTER FUNCTION dbo.fn_GatewayClassifier()
RETURNS sysname
WITH SCHEMABINDING
AS
BEGIN
    RETURN CASE
        WHEN APP_NAME() LIKE N'WebhookGateway%' THEN N'GatewayGroup'
        ELSE N'default'
    END;
END
GO

/* ------------------------------------------------------------------------
   3. Activar
   ------------------------------------------------------------------------
   CUIDADO: si la instancia ya tiene un clasificador configurado para otra
   carga, NO lo sustituyas. Hay que fusionar la lógica de ambos en una sola
   función, porque solo puede haber un clasificador activo por instancia.
   Sustituirlo deja la otra carga sin clasificar y nadie se entera hasta que
   algo va lento.
*/

DECLARE @actual int = (SELECT classifier_function_id FROM sys.resource_governor_configuration);

IF @actual IS NOT NULL AND @actual <> OBJECT_ID(N'dbo.fn_GatewayClassifier')
BEGIN
    RAISERROR('Ya hay otro clasificador activo (%s). Fusiona la lógica en lugar de sustituirlo.',
              16, 1, N'ver sys.resource_governor_configuration');
END
ELSE
BEGIN
    ALTER RESOURCE GOVERNOR WITH (CLASSIFIER_FUNCTION = dbo.fn_GatewayClassifier);
    ALTER RESOURCE GOVERNOR RECONFIGURE;
    PRINT 'Resource Governor activo. Clasifica por Application Name = WebhookGateway.';
END
GO

/* ------------------------------------------------------------------------
   4. Comprobación
   ------------------------------------------------------------------------
   El clasificador solo aplica a conexiones NUEVAS. Las abiertas siguen en el
   grupo en el que entraron.
*/

SELECT
    pool               = p.name,
    grupo              = g.name,
    max_cpu_percent    = p.max_cpu_percent,
    max_memory_percent = p.max_memory_percent,
    activo             = (SELECT is_enabled FROM sys.resource_governor_configuration)
FROM sys.resource_governor_resource_pools p
JOIN sys.resource_governor_workload_groups g ON g.pool_id = p.pool_id
WHERE p.name = N'GatewayPool';

/* Qué sesiones están cayendo realmente en el grupo. Útil tras desplegar. */
SELECT
    sesion      = s.session_id,
    login       = s.login_name,
    aplicacion  = s.program_name,
    grupo       = g.name
FROM sys.dm_exec_sessions s
JOIN sys.dm_resource_governor_workload_groups g ON g.group_id = s.group_id
WHERE g.name = N'GatewayGroup';
GO
