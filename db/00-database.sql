/*
    WebhookGateway — ajustes de la base de datos.

    Crea tú la base como prefieras (SSMS, CREATE DATABASE simple, lo que sea) con el
    nombre WebhookGateway. Este script solo aplica lo que las opciones por defecto no
    te dan y que el gateway sí necesita. Es idempotente.

    Solo hay dos ajustes que importan de verdad: RCSI y el modelo de recuperación.
    El resto es comprobación.
*/

USE master;
GO
SET NOCOUNT ON;
GO

IF DB_ID(N'WebhookGateway') IS NULL
BEGIN
    RAISERROR('No existe la base WebhookGateway. Créala primero.', 16, 1);
    RETURN;
END
GO

/* ------------------------------------------------------------------------
   1. Read Committed Snapshot  — este es el importante
   ------------------------------------------------------------------------
   Sin esto, las consultas de listado del panel y las escrituras del despachador
   se bloquean entre sí sobre las mismas tablas. Con esto, ni los lectores
   bloquean a los escritores ni al revés.

   Coste: usa tempdb, que es compartida en la instancia. A 0,23 mensajes por
   segundo el volumen de versiones es despreciable, pero conviene que el DBA
   lo sepa.

   Requiere que nadie más esté conectado a la base: por eso ROLLBACK IMMEDIATE.
*/
ALTER DATABASE WebhookGateway SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
GO

/* ------------------------------------------------------------------------
   2. Modelo de recuperación  — decisión consciente
   ------------------------------------------------------------------------
   Si creaste la base con las opciones por defecto, lo más probable es que haya
   quedado en FULL. Y una base en FULL sin copias de log periódicas es la forma
   clásica de llenar un disco: el log crece y nunca se recicla. En un servidor
   compartido eso es un problema de todos.

   SIMPLE  el log se recicla solo, cero mantenimiento. A cambio, ante una caída
           dura se pierde lo ocurrido desde la última copia completa. Es
           legítimo aquí: los webhooks de más de 3 días ya no son reenviables.

   FULL    restauración a un punto exacto en el tiempo. Elígelo SOLO si esta
           base entra en la rutina de copias de log que ya tiene el DBA.

   Se deja SIMPLE mientras nadie confirme lo contrario.
*/
ALTER DATABASE WebhookGateway SET RECOVERY SIMPLE;
GO

/* ------------------------------------------------------------------------
   3. Higiene — casi seguro ya están así, pero no cuesta nada asegurarlo
   ------------------------------------------------------------------------
   AUTO_SHRINK fragmenta los índices y dispara E/S en momentos impredecibles.
*/
ALTER DATABASE WebhookGateway SET AUTO_SHRINK OFF;
ALTER DATABASE WebhookGateway SET AUTO_CLOSE OFF;
GO

/* ------------------------------------------------------------------------
   4. Usuario de la aplicación — solo para producción
   ------------------------------------------------------------------------
   Para desarrollo, conéctate con tu usuario de Windows y sáltate esto.

   En producción hace falta un login propio por dos razones: permisos mínimos
   (nunca db_owner: la aplicación no crea ni altera objetos) y porque es lo que
   el clasificador de Resource Governor usa para encajonar la carga.

   Descomenta, pon una contraseña de verdad, y ejecútalo.
*/

/*
USE master;
GO
CREATE LOGIN [webhookgateway_app] WITH PASSWORD = N'PON_UNA_CLAVE_REAL', CHECK_POLICY = ON;
GO
USE WebhookGateway;
GO
CREATE USER [webhookgateway_app] FOR LOGIN [webhookgateway_app];
ALTER ROLE db_datareader ADD MEMBER [webhookgateway_app];
ALTER ROLE db_datawriter ADD MEMBER [webhookgateway_app];
GRANT EXECUTE ON SCHEMA::dbo TO [webhookgateway_app];
GO
*/

/* ------------------------------------------------------------------------
   5. Comprobación
   ------------------------------------------------------------------------
   rcsi debe ser 1 y recuperacion SIMPLE (o FULL si lo decidiste a conciencia).
   El crecimiento de los archivos debe estar en MB, no en porcentaje: un
   porcentaje sobre un archivo grande produce saltos enormes e impredecibles.
*/

SELECT
    base                 = name,
    recuperacion         = recovery_model_desc,
    rcsi                 = is_read_committed_snapshot_on,
    auto_shrink          = is_auto_shrink_on,
    nivel_compatibilidad = compatibility_level
FROM sys.databases
WHERE name = N'WebhookGateway';

SELECT
    archivo    = name,
    tipo       = type_desc,
    mb_actual  = size / 128,
    crecimiento = CASE is_percent_growth
                      WHEN 1 THEN CONVERT(varchar(20), growth) + ' %  <-- pásalo a MB'
                      ELSE CONVERT(varchar(20), growth / 128) + ' MB'
                  END
FROM sys.master_files
WHERE database_id = DB_ID(N'WebhookGateway');
GO
