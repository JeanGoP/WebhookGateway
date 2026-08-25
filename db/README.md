# Base de datos

Los scripts son idempotentes y **se ejecutan en orden**. Todos se pueden volver a correr
sin romper nada.

**La base la creas tú**, como prefieras, con el nombre `WebhookGateway`. Los scripts no
la crean.

| Script | Qué hace | Quién lo ejecuta |
|---|---|---|
| `00-database.sql` | Aplica los ajustes que las opciones por defecto no dan | Tú, tras crear la base |
| `01-schema.sql` | Particionado mensual y tablas de configuración | Cualquiera con permisos en la base |
| `02-traffic-tables.sql` | Tablas de tráfico particionadas e índices | Ídem |
| `03-users-audit.sql` | Usuarios del panel y auditoría | Ídem |
| `04-partition-maintenance.sql` | Procedimientos de mantenimiento | Ídem |
| `05-resource-governor.sql` | Topes de CPU e IOPS. **Opcional, solo producción** | DBA (nivel de instancia) |
| `06-delivery-by-id.sql` | Índice para buscar `WebhookDelivery` por `Id` (lo necesita el reenvío manual del panel) | Cualquiera con permisos en la base |
| `90-seed-demo.sql` | Datos de demostración. **Opcional, solo desarrollo** | Tú, en local |

El `00` aplica dos cosas que importan y que no vienen por defecto: **Read Committed
Snapshot**, para que el panel y el despachador no se bloqueen entre sí, y el **modelo de
recuperación**. Sobre lo segundo hay una decisión que tomar; el script la explica en un
comentario.

El `06` es un índice aparte, no dentro de `02`, porque los índices de `02-traffic-tables.sql`
viven en el bloque que solo se ejecuta cuando la tabla aún no existe. `WebhookDelivery` se
agrupa por `(CreatedAt, Id)`, así que buscar por `Id` a secas recorrería la tabla entera;
el reenvío manual (`POST /api/deliveries/{id}/retry`) necesita este índice. Es el mismo
problema que `UX_WebhookMessage_Id` resuelve para los mensajes.

## Ejecución

```powershell
sqlcmd -S TU_SERVIDOR -E -d WebhookGateway -i 00-database.sql
sqlcmd -S TU_SERVIDOR -E -d WebhookGateway -i 01-schema.sql
sqlcmd -S TU_SERVIDOR -E -d WebhookGateway -i 02-traffic-tables.sql
sqlcmd -S TU_SERVIDOR -E -d WebhookGateway -i 03-users-audit.sql
sqlcmd -S TU_SERVIDOR -E -d WebhookGateway -i 04-partition-maintenance.sql
sqlcmd -S TU_SERVIDOR -E -d WebhookGateway -i 06-delivery-by-id.sql
```

O todos de una con `run-all.ps1`.

**El `05` no hace falta para desarrollar.** Es de producción, requiere permisos de
servidor y lo aplica el DBA. Clasifica por `Application Name=WebhookGateway`, no por
login, para no depender de qué usuario autorice la empresa. **Cuidado**: si la instancia
ya tiene un clasificador de Resource Governor para otra carga, hay que *fusionar* las dos
funciones — solo puede haber uno activo por instancia.

**El `90` es opcional**: siembra datos de demostración para desarrollo local. No lo corras
contra una base con datos reales.

## Desarrollo local

```bash
docker compose up -d
```

Levanta SQL Server 2022 Developer Edition, que incluye las funciones de Enterprise
—particionado, compresión, Resource Governor— así que el comportamiento es el mismo que
en producción.

## Mantenimiento

Dos trabajos del Agente SQL, o un `sqlcmd` desde el planificador de tareas:

```sql
-- Diario: mantiene 6 meses de particiones por delante.
EXEC dbo.sp_Gateway_EnsureFuturePartitions @MonthsAhead = 6;

-- Diario: vacía lo vencido. Empieza SIEMPRE con @DryRun = 1 y mira qué haría.
EXEC dbo.sp_Gateway_PurgeExpiredPartitions @DryRun = 1;
```

`sp_Gateway_EnsureFuturePartitions` no es opcional: si se acaban las particiones futuras,
todo lo nuevo cae en la última, que crece sin límite y deja de poder purgarse.
