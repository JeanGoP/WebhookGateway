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
| `07-notifications.sql` | Bandeja de salida e historial de alertas | Ídem |
| `08-endpoint-health.sql` | Estado de salud por destino | Ídem |
| `09-subscribers.sql` | Suscriptores de alertas por integración | Ídem |
| `10-traffic-write-inbound.sql` | `sp_Traffic_WriteInbound`: la ingesta en un solo viaje de red | Ídem |
| `11-delivery-dispatch-by-endpoint.sql` | Índice del claim por destino | Ídem |
| `12-purge-unpartitioned.sql` | `sp_Gateway_PurgeUnpartitionedLogs`: purga por lotes de las tablas sin particionar | Ídem |
| `13-watchdog.sql` | `sp_Gateway_Watchdog`: vigila particiones, backlog y leases | Ídem |
| `14-sql-agent-jobs.sql` | Los cuatro jobs del Agente SQL. **Nivel de instancia** | DBA, o quien tenga `SQLAgentUserRole` |
| `90-seed-demo.sql` | Datos de demostración. **Opcional, solo desarrollo** | Tú, en local |

**`run-all.ps1` está incompleto** (le faltan el `06` y el `10`) y ejecuta el `00`, que lleva
`WebhookGateway` escrito a mano. Contra un servidor compartido no se usa: ver la §5 de
`../docs/plan-escala-400k.md`.

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

Lo crea `14-sql-agent-jobs.sql`: cuatro jobs del Agente SQL. Los jobs son del servidor, no de
la base, así que **su nombre lleva el nombre de la base** y los de desarrollo y los de
producción conviven en la misma instancia sin pisarse.

| Job | Cuándo | Qué hace |
|---|---|---|
| `… - Particiones futuras` | Domingos 02:00 | `sp_Gateway_EnsureFuturePartitions @MonthsAhead = 6` |
| `… - Purga` | Diario 03:00 | `sp_Gateway_PurgeExpiredPartitions` y `sp_Gateway_PurgeUnpartitionedLogs` |
| `… - Estadisticas` | Diario 03:30 | `UPDATE STATISTICS` de las cuatro tablas de tráfico |
| `… - Vigilancia` | Cada 10 min | `sp_Gateway_Watchdog` |

**La purga entra en seco.** Los dos pasos del job llevan `@DryRun = 1`: cuentan lo que
borrarían y no borran nada. Hay que dejarla así una semana, mirar el resultado en el historial
del job y solo entonces editar los pasos y poner `@DryRun = 0`.

A mano, para probar:

```sql
-- Qué particiones se vaciarían, y cuántas filas hay en ellas.
EXEC dbo.sp_Gateway_PurgeExpiredPartitions @DryRun = 1;

-- Cuántas filas borraría de las tablas sin particionar.
EXEC dbo.sp_Gateway_PurgeUnpartitionedLogs @DryRun = 1;

-- Cómo está el gateway ahora mismo. Si devuelve filas, hay algo que mirar.
EXEC dbo.sp_Gateway_Watchdog;
```

`sp_Gateway_EnsureFuturePartitions` no es opcional: si se acaban las particiones futuras,
todo lo nuevo cae en la última, que crece sin límite y deja de poder purgarse. De eso avisa
`sp_Gateway_Watchdog` con tres meses de antelación.

**El vigilante avisa fallando.** Cuando encuentra algo lanza `RAISERROR` con severidad 16, de
modo que el job falla y queda en su historial. Se hace así para no depender de que Database
Mail esté configurado en una instancia que no es nuestra; conectarlo a un correo es definir un
operador y marcar "notificar al fallar" en el job.
