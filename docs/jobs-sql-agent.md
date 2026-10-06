# Jobs de SQL Server Agent — producción

Cuatro jobs mantienen viva la base `WebhookGateway`. Los crea un solo script,
`db/14-sql-agent-jobs.sql`, que es idempotente: crea el que falte y no toca el que ya esté.

| Job | Cuándo (hora del servidor) | Qué hace | Si no corre |
|---|---|---|---|
| `WebhookGateway - Particiones futuras` | domingos 02:00 | Crea las particiones mensuales de los próximos 6 meses | Al acabarse, los INSERT fallan y la recepción devuelve 503/429 |
| `WebhookGateway - Purga` | diario 03:00 | Vacía las particiones vencidas según la retención del panel y purga por lotes deduplicación y tokens vencidos, notificaciones (90 días) y auditoría (365) | La base crece sin parar (~1,7 KB por mensaje) |
| `WebhookGateway - Estadisticas` | diario 03:30 | `UPDATE STATISTICS` de las cuatro tablas de tráfico | El claim y el buscador del panel eligen planes malos poco a poco |
| `WebhookGateway - Vigilancia` | cada 10 min | Falla a propósito si quedan menos de 3 meses de particiones, más de 50.000 pendientes, alguna entrega con más de 15 min de retraso o leases huérfanos | Nadie se entera de lo anterior |

La purga **entra en seco** (`@DryRun = 1`): durante una semana solo cuenta lo que borraría.

---

## Antes de empezar

1. **El esquema de producción tiene que estar aplicado**: `db/despliegues/2026-10-05-produccion.sql`
   (instrucciones en su cabecera). El script de jobs se niega a seguir si no encuentra los
   procedimientos de 04, 12 y 13, que vienen en ese despliegue.
2. **Permisos**: tu login necesita `SQLAgentUserRole` (o más) en `msdb`.
3. **El dueño del job serás tú.** Los pasos se ejecutan con los permisos del dueño, así que ese
   login tiene que poder ejecutar los procedimientos, truncar particiones y actualizar
   estadísticas en `WebhookGateway` (con `db_owner` sobra). Si un día se desactiva ese login, los
   cuatro jobs empiezan a fallar.
4. **SQL Server Agent arrancado.** Se ve en el Explorador de objetos de SSMS: el nodo
   *SQL Server Agent* tiene que tener la flecha verde.

---

## Crearlos

1. En SSMS, conectarse a `200.7.96.218`.
2. Abrir `db/14-sql-agent-jobs.sql`.
3. **En el desplegable de bases de la barra de herramientas, elegir `WebhookGateway`** —la que no
   termina en `_dev`—. El script crea los jobs para la base en la que se ejecuta y les pone su
   nombre: ejecutado en `_dev` crearía `WebhookGateway_dev - …` apuntando a `_dev`.
4. F5.

Lo que tiene que salir en *Mensajes*:

```
Creado: WebhookGateway - Particiones futuras
Creado: WebhookGateway - Purga (EN SECO)
Creado: WebhookGateway - Estadisticas
Creado: WebhookGateway - Vigilancia
```

Y en *Resultados*, cuatro filas: los cuatro habilitados, la Purga con 2 pasos y el resto con 1.
Si sale `Ya existía: …` es que ese job ya estaba: no lo toca.

Dos avisos que no son errores:

- `AVISO: SQL Server Agent no aparece como en ejecución` — o el Agent está parado (los jobs se
  crean igual, pero no corren hasta que arranque) o tu login no puede ver el estado de los
  servicios. Mira el nodo del Agent en SSMS.
- Un error de permiso sobre `sys.dm_server_services` — es esa misma comprobación, que necesita
  `VIEW SERVER STATE`. El resto del script sigue y crea los jobs.

> El script tenía dos errores que impedían ejecutarlo (los nombres de los horarios y el id del job
> reutilizado, que acababa colgando todos los pasos de un único job). Están corregidos y probado
> en LocalDB el 2026-10-05: crea los cuatro jobs con sus pasos y horarios, y una segunda ejecución
> no duplica nada.

---

## Probarlos a mano

Los cuatro se pueden lanzar ya sin riesgo: la purga está en seco y el resto no borra nada.

```sql
EXEC msdb.dbo.sp_start_job @job_name = N'WebhookGateway - Particiones futuras';
EXEC msdb.dbo.sp_start_job @job_name = N'WebhookGateway - Vigilancia';
EXEC msdb.dbo.sp_start_job @job_name = N'WebhookGateway - Purga';
-- Estadisticas lee las tablas de tráfico enteras: mejor fuera de horas, o dejar que corra sola a las 03:30.
```

`sp_start_job` solo los arranca y vuelve enseguida. El resultado, unos segundos después:

```sql
SELECT j.name AS Job,
       CASE h.run_status WHEN 0 THEN 'FALLÓ' WHEN 1 THEN 'OK' WHEN 2 THEN 'Reintento'
                         WHEN 3 THEN 'Cancelado' WHEN 4 THEN 'En curso' END AS Resultado,
       msdb.dbo.agent_datetime(h.run_date, h.run_time) AS Cuando,
       h.message AS Mensaje
FROM msdb.dbo.sysjobs AS j
OUTER APPLY (SELECT TOP (1) * FROM msdb.dbo.sysjobhistory AS x
             WHERE x.job_id = j.job_id AND x.step_id = 0
             ORDER BY x.instance_id DESC) AS h
WHERE j.name LIKE N'WebhookGateway - %'
ORDER BY j.name;
```

(En SSMS: clic derecho sobre el job → *Ver historial* muestra lo mismo.)

**Si la Vigilancia sale como FALLÓ, lee el mensaje antes de nada**: fallar es su forma de avisar.
Empieza por `Vigilante del gateway:` y dice qué encontró (por ejemplo, `Retraso: …` con el destino
y los minutos). Solo es un problema del job si el mensaje habla de otra cosa (un permiso, un
objeto que no existe).

Ojo con el historial de la Vigilancia: SQL Agent guarda por defecto 100 filas por job y cada
ejecución usa dos, así que solo se ven las últimas ~8 horas.

---

## La semana en seco de la purga

Lo que borraría hoy se ve mejor ejecutándolo a mano que en el historial del job (que recorta la
salida). En la base `WebhookGateway`:

```sql
EXEC dbo.sp_Gateway_PurgeExpiredPartitions @DryRun = 1;   -- retención de dbo.RetentionPolicy
EXEC dbo.sp_Gateway_PurgeUnpartitionedLogs @NotificationRetentionDays = 90,
                                           @AuditRetentionDays = 365, @DryRun = 1;
```

El primero lista, por tabla, las particiones que vaciaría, la fecha de corte y las filas; el
segundo, por tabla, la fecha de corte y las filas que borraría. En los dos, `WouldDelete = 1`
quiere decir "en seco: no se borró nada". La retención de la primera se cambia en el panel, en
*Configuración* (por defecto: mensajes y entregas 180 días, cuerpos 30, intentos 30). Como solo
se vacían meses completos, "30 días" en la práctica son entre 30 y 61.

Lo que hay que comprobar durante esa semana: que las fechas de corte son las esperadas y que no
aparece ninguna partición del mes en curso ni del anterior para mensajes y entregas.

### Activarla (al cabo de la semana)

```sql
EXEC msdb.dbo.sp_update_jobstep @job_name = N'WebhookGateway - Purga', @step_id = 1,
     @command = N'EXEC dbo.sp_Gateway_PurgeExpiredPartitions
    @DryRun = 0;';

EXEC msdb.dbo.sp_update_jobstep @job_name = N'WebhookGateway - Purga', @step_id = 2,
     @command = N'EXEC dbo.sp_Gateway_PurgeUnpartitionedLogs
    @NotificationRetentionDays = 90,
    @AuditRetentionDays        = 365,
    @DryRun                    = 0;';

EXEC msdb.dbo.sp_update_job @job_name = N'WebhookGateway - Purga',
     @description = N'Vacía particiones vencidas y purga por lotes las tablas sin particionar. Borra de verdad.';
```

Comprobación de que quedó bien (los dos pasos tienen que decir `@DryRun = 0`):

```sql
SELECT s.step_id, s.step_name, s.command, s.on_success_action
FROM msdb.dbo.sysjobsteps AS s
JOIN msdb.dbo.sysjobs AS j ON j.job_id = s.job_id
WHERE j.name = N'WebhookGateway - Purga'
ORDER BY s.step_id;
```

`on_success_action` del paso 1 tiene que seguir en 3 (pasar al paso siguiente).

La primera purga real puede vaciar varios meses de golpe si producción tiene historia vieja.
`TRUNCATE` por partición es casi instantáneo y apenas genera log, pero mejor que esa primera vez
sea la de las 03:00 y no una lanzada a mano en horario de oficina.

---

## Que la Vigilancia avise por correo (opcional, con el DBA)

Por sí sola, la Vigilancia deja el fallo en su historial y en el registro de eventos de Windows
del servidor. Para que además llegue un correo hace falta **Database Mail configurado en la
instancia**, que es compartida: eso es del DBA. Con Database Mail listo:

1. SQL Server Agent → Propiedades → *Sistema de alertas* → *Habilitar perfil de correo* →
   elegir el perfil, y reiniciar el Agent.
2. Crear el operador y engancharlo al job:

```sql
EXEC msdb.dbo.sp_add_operator
     @name = N'WebhookGateway - Guardia',
     @email_address = N'correo-del-equipo@dominio';   -- poner el real

EXEC msdb.dbo.sp_update_job
     @job_name = N'WebhookGateway - Vigilancia',
     @notify_level_email = 2,                          -- al fallar
     @notify_email_operator_name = N'WebhookGateway - Guardia';
```

Lo mismo se puede poner en *Particiones futuras* y en *Purga*: si fallan, también interesa.

Ten en cuenta que, mientras dure un problema, la Vigilancia falla cada 10 minutos: un correo
cada 10 minutos hasta que se resuelva.

---

## Cambiar algo más adelante

- **Umbrales de la Vigilancia**: editar el paso (`@MaxBacklog`, `@MaxLagMinutes`…).
  `@MaxLagMinutes` tiene que coincidir con `Gateway:Monitoring:MaxLagMinutes` del
  `appsettings.json` (15), que es el que usa el monitor del panel.
- **Frecuencia u otra cosa**: el script no modifica jobs existentes. Se borra el job
  (`EXEC msdb.dbo.sp_delete_job @job_name = N'…';`) y se vuelve a ejecutar `db/14`.
- **Retención de mensajes, cuerpos e intentos**: desde el panel, no aquí.

## Y para `_dev`

Si se quiere, el mismo script ejecutado con `WebhookGateway_dev` en el desplegable crea sus
propios cuatro jobs (`WebhookGateway_dev - …`), que conviven con los de producción. El único que
de verdad hace falta allí es *Particiones futuras*; los demás se pueden borrar o deshabilitar.

## Lo que no es un job

`db/15-drop-legacy-dispatch-index.sql` borra el índice viejo del claim. Se ejecuta **una vez y a
mano**, después de publicar el código nuevo del gateway y comprobar que despacha. Ver
`despliegue-iis.md`.
