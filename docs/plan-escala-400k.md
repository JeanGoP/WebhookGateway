# Plan de escala a 300.000–400.000 mensajes/día — estado y traspaso

Documento de continuidad. Quien retome este trabajo (persona o Claude) debe leer primero
`CLAUDE.md`, después este archivo, y solo entonces tocar código.

Última actualización: 2026-10-05. **Fases 1 a 5 escritas, la revisión de §7.5 hecha y la prueba de
carga (fase 6) pasada en LocalDB: 300.000 webhooks a tope y 63.000 a 70/s sostenidos, sin una
pérdida ni un duplicado. La prueba destapó dos fallos del despachador —una carrera que podía
duplicar entregas y un techo de concurrencia por destino— que ya están arreglados (§7.6). Queda el
despliegue y, si se quiere, una prueba acotada contra `_dev`.**

> **Sin Docker, con LocalDB.** En este equipo no hay Docker, pero sí SQL Server LocalDB 17.0
> (`MSSQLLocalDB`). Desde el 2026-10-05 la fixture de integración lo usa sola cuando no hay Docker,
> y la prueba de carga también va contra él: local, sin tocar el servidor compartido. LocalDB está
> capado (4 núcleos, ~1,4 GB), así que sus números salen pesimistas; lo que aguante ahí lo aguanta
> producción.

> **Control de aplicaciones de Windows (Smart App Control) va y viene en este equipo.** Bloquea la
> DLL de pruebas recién compilada con `FileLoadException 0x800711C7` y entonces `dotnet test` dice
> "No hay ninguna prueba disponible". Es del equipo, no del código: `dotnet build` sigue limpio.
> Cuando pasa, ni limpiar `bin/`+`obj/`, ni renombrar el ensamblado, ni compilar en Release lo
> resuelven; se quita solo al cabo de un rato o de unas compilaciones. **Si `dotnet test` informa de
> 0 pruebas o de un "Catastrophic failure", mira esto antes de buscar un bug que no existe.**

---

## 1. Objetivo

Que el gateway reciba y reparta **300.000–400.000 mensajes diarios** sin caídas ni backlog
creciente, en el servidor actual: **IIS con 8 núcleos** utilizables para el proyecto.

`CLAUDE.md` todavía describe el diseño original (20.000 mensajes/día, "no hay problema de
rendimiento que resolver"). Esa premisa ya no vale para este trabajo; las cifras de abajo
mandan. Actualizar `CLAUDE.md` es la última tarea de la fase 6.

### Cifras medidas en producción (2026-10-01, últimos 30 días)

| Medida | Valor | Consecuencia |
|---|---|---|
| Destinos por mensaje | **1,0** | ~400.000 entregas/día = 4,6/s de media (no 800k como se supuso al principio) |
| Pico observado | 4.209 mensajes en 1 minuto (~70/s), 2026-09-15 15:20 | Parece una prueba de carga; la recepción lo guardó todo. El tráfico real es de 3–4/min |
| Bytes por fila (con índices) | Mensaje 125 · Cuerpo 231 · Entrega 81 · Intento 150 | ~590 B por mensaje en total |
| Tamaño estable con retención 90/14/30 | **~14 GB** | El almacenamiento deja de ser un problema |
| Tamaño estable con retención 365/30/90 | ~42 GB | Opción viable si se quiere un año de historial (decisión abierta) |
| Destino "CRM recibir leads impulsa" | 316 ms medios, 5 fallos de 10.555, límite 600/min | Su límite (10/s) es el techo real si recibe la mayoría del tráfico |
| Destino "AppHaku Recepcion Lead Aburra" | 408 ms medios, **23 fallos de 34** | Está fallando casi siempre; revisar `LastStatusCode`/`LastError` |
| Mensajes vs. cuerpos | 52.855 mensajes, 31.713 cuerpos | Pendiente aclarar si es purga normal o mensajes sin cuerpo (consulta en §6) |

La muestra es pequeña; si los cuerpos crecen, el tamaño crece en proporción.

---

## 2. Diagnóstico (por qué hace falta el plan)

**La recepción (`/in/*`) aguanta el volumen.** Un solo viaje a SQL por mensaje
(`sp_Traffic_WriteInbound`), configuración cacheada, nunca 2xx sin persistir.

**El despachador no.** Causas, por orden de gravedad:

1. **Paralelismo real = núcleos.** `Parallel.ForEachAsync` sin opciones usa
   `Environment.ProcessorCount` (8 aquí), no "sin límite" como dice el comentario de
   `DispatcherWorker.RunCycleAsync`.
2. **Bloqueo por el más lento.** Cada ciclo reclama 100 entregas y espera a que termine la
   más lenta. `EndpointThrottles.AcquireAsync` espera turno del limitador ocupando un hueco.
   Un destino de 60/min con backlog alarga cada ciclo ~15 s; uno con timeout de 30 s, ≥ 30 s.
   Todo el sistema cae a 3–7 entregas/s.
3. **Claim caro con backlog.** `DeliveryClaimer.ClaimSql` numera con `ROW_NUMBER()` todo lo
   pendiente en cada ciclo y el `UPDLOCK` bloquea todas las filas leídas. Riesgo de escalado a
   bloqueo de tabla en `WebhookDelivery`, que frenaría también la recepción.

**Errores que a volumen se vuelven graves:**

- `DispatcherWorker` solo captura `OperationCanceledException`. Un `SqlException` (timeout,
  deadlock) termina el `BackgroundService` y en .NET 6+ **detiene el proceso entero**,
  recepción incluida.
- `DeliveryRecorder.TruncateAttempt` corta `ErrorMessage` a 2000, pero la columna
  `DeliveryAttempt.ErrorMessage` es `nvarchar(1000)`: falla el insert del lote y, por el punto
  anterior, cae el proceso.
- `sp_Traffic_WriteInbound`: con `SET XACT_ABORT ON`, el `CATCH` del 2627/2601 no puede seguir
  usando la transacción (queda inservible, error 3930). Dos duplicados simultáneos → 503.
- Alertas: una por cada entrega fallida y por cada error de recepción, a cada destinatario.
  Un destino que devuelve 400 genera miles de correos.
- `IntegrationLiveStatusEndpoints`: cuenta todo el histórico de `WebhookDelivery` sin filtro
  de fecha (el panel permite sondear cada 1 s). Además etiqueta como "duplicados" el
  `Status = 2`, que es NoSubscriptions; Duplicate es 1.
- `InboundEndpointLookup` no cachea rutas inexistentes (cada una va a SQL) y la caché de
  5 min no se invalida al editar un endpoint.
- `EndpointThrottles`: `perSecond = ceil(rate/60)` convierte 90/min en 120/min.
- `MessageExplorer.SearchMessagesSql`: filtros opcionales `@X IS NULL OR …` sin
  `OPTION (RECOMPILE)` → planes malos según los primeros parámetros.

---

## 3. Decisiones tomadas

| Tema | Decisión |
|---|---|
| Retención | **Editable desde el panel (Configuración)**, global. Por defecto mensajes y entregas 180 días, cuerpos 30, intentos 30 (~20 GB para la integración de 400k). Respuesta del destino solo en intentos fallidos. Decidido el 2026-10-05 |
| Integración de 400k | Una sola integración nueva, de GHL (webhooks de **app del Marketplace**), con un destino. Las demás, 15k–30k/día. Retraso aceptable: **15 minutos**. El destino es lento y se mejorará después |
| Orden de entrega | **No se garantiza ni hace falta** (decidido el 2026-10-05). El claim no ordena |
| Rechazo cuando SQL falla | Código configurable por endpoint de entrada: GHL Marketplace **solo reintenta 429**; un 503 lo da por perdido |
| Resource Governor | **No se aplica.** Confirmado inactivo (`is_enabled = 0`). Revisar tras la prueba de carga |
| Mantenimiento | **Jobs de SQL Server Agent.** Agent confirmado en ejecución |
| `appsettings.json` | **No se mueve nada** ni se pasan secretos a variables de entorno. Mitigación: fuera de git y lectura solo para la cuenta del app pool |
| CORS | **Se deja abierto** (`*`). Riesgo bajo: el panel usa token Bearer, no cookies |
| Base de desarrollo | `WebhookGateway_dev`, en el mismo servidor que producción |
| Servidor | IIS, 8 núcleos. La CPU no es el límite |

---

## 4. Lo que ya está hecho

- [x] Base `WebhookGateway_dev` creada y con esquema aplicado: 16 tablas, 3 procedimientos,
      RCSI = 1, recuperación SIMPLE. Se aplicó con `db/setup-dev-db.sql` desde SSMS.
- [x] `db/setup-dev-db.sql` y `db/setup-dev-db.ps1`: preparan **solo** `WebhookGateway_dev`
      (nombre fijo, con protección `SET NOEXEC ON`). El `.ps1` falló por la versión de
      PowerShell/sqlcmd del equipo; el `.sql` en SSMS funciona.
- [x] `appsettings.Development.json` apunta a `WebhookGateway_dev`, despachador apagado,
      notificaciones apagadas y SMTP en `DryRun`.
- [x] SQL Server Agent confirmado en ejecución.
- [x] Particiones: última frontera de `PF_Monthly` = **2027-07-01** (~9 meses de margen).
- [x] Mediciones de producción (§1).
- [x] `WebhookGateway_dev` al día con el código de §7.5 y §7.6 (2026-10-05): aplicados 13, 16, 17 y 04,
      comprobados ejecutando la función de retraso, el vigilante y la purga en seco. El motor es
      SQL Server 2025 Enterprise (nivel de compatibilidad 160), y desde este equipo hay ~23 ms por
      viaje a SQL. Tiene dos destinos reales activos (AppHaku por un túnel de Cloudflare y el CRM en
      `development.sidecil.com`), sin entregas pendientes: desactivarlos antes de una prueba de carga.

---

## 5. Reglas para no afectar producción

Desarrollo y producción comparten servidor. Esto **no se negocia**:

1. **No ejecutar `db/00-database.sql` ni `db/run-all.ps1`.** El `00` tiene `WebhookGateway`
   escrito a mano: aunque se pase otra base, cambiaría producción y cortaría sus conexiones
   (`ROLLBACK IMMEDIATE`). Además `run-all.ps1` no incluye los scripts 06 ni 10.
2. Todo cambio de esquema se prueba primero en `WebhookGateway_dev`, y en producción solo se
   aplica si es **compatible con el código desplegado** (por ejemplo, el arreglo del SP).
3. Al arrancar en local, el log debe decir
   `SQL Server destino: 200.7.96.218 / WebhookGateway_dev (entorno Development)`.
   Si dice `WebhookGateway` sin `_dev`, detenerse.
4. **⚠ `appsettings.json` apunta hoy a `Database=WebhookGateway_dev`** (lo cambió el usuario
   el 2026-10-01). Antes de cualquier publicación a IIS debe volver a `Database=WebhookGateway`,
   o producción escribiría en la base de desarrollo.
5. No encender el despachador en local mientras apunte a una base con entregas reales.
6. **La prueba de carga no se hace en el servidor compartido.** Va en la máquina dedicada a Docker,
   con el SQL de `docker-compose.yml` y destinos falsos locales.
7. Antes de cada despliegue: copia de seguridad completa y horario de poco tráfico.
8. Jobs de purga: una semana en `@DryRun = 1` antes de activarlos.

---

## 6. Pendiente de la fase 0

- [ ] Aclarar mensajes sin cuerpo. Si solo salen días antiguos, es purga normal:
  ```sql
  SELECT CONVERT(date, m.ReceivedAt) AS dia, COUNT(*) AS sin_cuerpo
  FROM dbo.WebhookMessage m WITH (NOLOCK)
  LEFT JOIN dbo.WebhookPayload p WITH (NOLOCK)
         ON p.ReceivedAt = m.ReceivedAt AND p.MessageId = m.Id
  WHERE p.MessageId IS NULL
  GROUP BY CONVERT(date, m.ReceivedAt)
  ORDER BY dia DESC;
  ```
- [ ] Saber a qué destinos irán los 400k/día. Si van sobre todo al CRM, confirmar si acepta
      más de 600/min.
- [ ] Revisar por qué falla "AppHaku Recepcion Lead Aburra".
- [ ] Proteger `appsettings.json`: fuera de git (o repo privado), lectura solo para el app pool.
- [ ] Aclarar de dónde sale el historial de `WebhookGateway_dev`. Visto el 2026-10-01: **10.563
      mensajes y 10.566 entregas**, el último del 2026-09-30 18:05 UTC y nada en las 24 h
      siguientes, con 13–40 mensajes al día. No cuadra con una base recién creada en la fase 0, y
      tampoco con una copia de producción (que lleva ~1.760 al día), así que parece tráfico de
      pruebas anterior. Conviene confirmarlo antes de medir nada sobre esta base.
- [ ] Decidir si se mantiene la retención de 90 días o se vuelve a un año.

---

## 7. Fases restantes

Esfuerzo total: 13–19 días laborables (3–4 semanas) con una persona y en secuencia.

### Fase 1 — Estabilidad (1–2 días) · **escrita, unitarios en verde, integración pendiente**

- [x] `DispatcherWorker`: cada pasada del bucle va en `RunIterationAsync`, con su try/catch. Un
      fallo registra y espera según `CycleBackoff` (1 s → 5 s → 15 s → 30 s → 1 min, con jitter)
      y el primer ciclo bueno reinicia la escalera. Una `OperationCanceledException` del apagado
      sigue saliendo para que el cierre ordenado se ejecute. Para no pasar de 200 líneas, la
      espera de trabajo (el semáforo y el escuchador de la cola) salió a `WorkSignal`.
- [x] `DeliveryRecorder.TruncateAttempt`: `ErrorMessage` a 1000, con los tres topes como
      constantes y un comentario que dice que son las anchuras de las columnas.
- [x] `db/10-traffic-write-inbound.sql`: la clave se reserva al principio con
      `SELECT … WITH (UPDLOCK, HOLDLOCK)`, antes de cualquier escritura, así que la violación de
      clave ya no puede ocurrir y no hace falta `TRY/CATCH`. El mismo cambio está replicado en
      `db/setup-dev-db.sql`, que lleva su propia copia del procedimiento.
- [x] `InboundEndpointLookup`: el `null` se cachea 30 s y los aciertos 5 min. La invalidación va
      por marca de versión (`InboundConfigVersion`) dentro de la clave de caché: las mutaciones
      de integración, endpoint de entrada, destino y suscripción llaman a `Invalidate()` tras
      `SaveChangesAsync`. La marca es por proceso: con dos instancias vivas, la que no atendió la
      edición sigue con su caché hasta que venza el TTL.

**Verificado (2026-10-01).** `dotnet build` sin avisos y 129 unitarios en verde, incluidos los
nuevos de `CycleBackoff`.

El arreglo del procedimiento se aplicó a `WebhookGateway_dev` y se probó la carrera de duplicados
de forma determinista: una conexión A ejecuta el SP dentro de una transacción de cliente que se
deja abierta (así retiene el bloqueo de la clave) y una conexión B lanza el mismo SP con la misma
clave. Resultado: **B se bloqueó esperando la reserva, y al hacer A el `COMMIT` resolvió como
`Duplicate` apuntando al mensaje de A, sin error y sin 503.** Quedaron una fila de `MessageDedupe`,
los dos mensajes (el bueno y el duplicado) y cero entregas. La ruta de prueba se creó sin
suscripciones a propósito, para que el SP no generase entregas que algún despachador pudiera
intentar enviar a un destino real; todas las filas de la prueba se borraron después.

Notas de esa prueba, para quien la repita:

- La cadena de conexión lleva `MultipleActiveResultSets=True`, así que **no se puede dejar abierta
  una transacción con `BEGIN TRANSACTION` de T-SQL entre lotes** (SQL Server la deshace y avisa).
  Hay que usar una transacción de cliente (`SqlConnection.BeginTransaction`).
- La definición del SP que había antes quedó guardada antes de aplicar el cambio, por si hay que
  volver atrás sin pasar por git.

**Lo que falta para cerrar la fase:** los **tests de integración siguen omitidos, no hay Docker en
este equipo** — y eso incluye los que ya existían del claim concurrente, que CLAUDE.md marca como
obligatorios. `tests/…/TrafficWriteInboundTests.cs` cubre la carrera de duplicados, las claves
distintas que no se estorban y el caso sin clave. Se ejecutarán en la máquina dedicada a Docker.
Tampoco se comprobó que la versión *anterior* del SP falle ese mismo escenario: hacerlo
exigiría reinstalar el procedimiento defectuoso en el servidor compartido, y eso no se hace; el
test en contenedor lo demuestra sin tocar nada de nadie.

### Fase 2 — Nuevo despachador (4–6 días) · **escrita, unitarios en verde, integración pendiente**

- [x] Avance independiente por destino. `DispatcherWorker` ya no entrega nada: descubre qué
      destinos tienen trabajo vencido y pone en marcha el de cada uno. Quien entrega es
      `EndpointPump`, uno por destino, que reclama lo suyo y repite hasta vaciar su cola;
      `EndpointPumpSet` lleva la cuenta de quién está avanzando. La espera del limitador salió de
      `DeliveryDispatcher`: esperar ahí dentro era justo lo que bloqueaba a los demás.
- [x] Techo global configurable: `Gateway:Dispatcher:MaxGlobalConcurrency`, 128 por defecto, en
      lugar de los ocho núcleos que imponía `Parallel.ForEachAsync`. Se pide **después** del turno
      del destino, para que esperar un ritmo no ocupe capacidad que otro podría usar. Hay un
      segundo techo, `MaxEndpointsInParallel` (32), que no acota ritmo sino conexiones: cada bomba
      en marcha usa una para reclamar y otra para volcar. Con los destinos que hay hoy no se toca.
- [x] Claim por destino, sin `ROW_NUMBER()`: `ClaimForEndpointAsync` pide solo lo de un destino y
      `FindEndpointsWithWorkAsync` responde quién tiene trabajo. Necesita el índice nuevo de
      `db/11-delivery-dispatch-by-endpoint.sql` — (`OutboundEndpointId`, `NextAttemptAt`), filtrado
      por `Status IN (0, 2)`— para que sea una búsqueda directa y no un recorrido de todo lo
      vencido. El índice viejo `IX_Delivery_Dispatch` **no se borra**: mientras producción corra el
      código anterior, es el que sostiene su claim.
- [x] `DeliveryRecorder`: el volcado entero va en una transacción. Si falla, se propaga y esas
      entregas vuelven por vencimiento de lease, que es lo que se quiere: guardarlas para
      reintentar el volcado crece sin techo si el fallo no es pasajero.
- [x] Redondeo del limitador: `RateBudget` elige el periodo para que la cuenta salga exacta y,
      cuando no puede, redondea **a la baja**. Un destino a 90/min recibía 120/min.
- [x] Reglas del despachador de `CLAUDE.md`: el claim sigue siendo atómico con lease y `READPAST`;
      reprogramar sigue sin consumir intento; el breaker sigue contando solo fallos transitorios y
      ahora además se comprueba **antes de reclamar**, así que un destino caído con mil pendientes
      ya no son mil reclamaciones y mil reprogramaciones para no enviar nada.
- [x] Test del claim con N workers concurrentes: reescrito para el claim por destino, que concentra
      la contención (todos los workers peleando por las filas del mismo destino, el peor caso para
      `READPAST`/`UPDLOCK`). Más el descubrimiento, el tamaño de lote y el orden por vencimiento.
- [ ] **Falta el test del destino lento que no frena a los demás.** Es el único punto de la fase sin
      cubrir. Necesita Docker y destinos HTTP falsos: dos destinos con backlog, uno que responde en
      2 s y otro al instante, y comprobar que el rápido termina sin esperar al lento. Encaja con la
      prueba de carga de la fase 6, que ya contempla ese escenario.

**Verificado contra `WebhookGateway_dev` (2026-10-01).** Sin Docker no se puede ver el despachador
entero funcionando, pero el SQL nuevo y el índice sí se probaron contra el motor real, sembrando
entregas dentro de una transacción que luego se deshizo (quedaron 0 filas de prueba):

- `db/11` aplicado. Clave `(OutboundEndpointId, NextAttemptAt)`, con `CreatedAt` presente por
  alineación de particiones —`key_ordinal = 0`, `partition_ordinal = 1`—, igual que en el índice
  anterior. Es lo que permite que el claim devuelva la clave completa `(CreatedAt, Id)` sin ir a la
  tabla. Como el claim no filtra por `CreatedAt`, la búsqueda ocurre una vez por partición; eso ya
  pasaba con el índice viejo, así que no es un cambio a peor.
- Descubrimiento: con 40 pendientes vencidas en un destino, 3 en otro, 1 programada para más tarde y
  1 fuera de ventana, devolvió exactamente los dos primeros.
- Claim por destino: pidiendo al destino con 3, devolvió 3 y solo de ese destino; las 40 del
  saturado quedaron intactas. Pidiendo al saturado con tope 20, devolvió 20.
- `sys.dm_db_index_usage_stats` registró 2 *seeks* en el índice nuevo y 0 *scans*: los dos claims
  lo usaron como búsqueda, que es para lo que está.

Lo que esto **no** prueba: que las bombas por destino avancen de verdad en paralelo, ni el techo de
capacidad, ni que un destino lento no frene a los demás. Eso necesita la suite de integración.

**Cómo verificar el resto.** `dotnet build` sin avisos y los 176 unitarios en verde, incluidos
`RateBudgetTests` y `EndpointThrottlesTests`. Los 10 de integración siguen **omitidos** por no haber
Docker: son los que probarían el claim nuevo bajo concurrencia contra un motor real.

Por tanto, **el despachador nuevo no se ha visto funcionar en conjunto todavía**. Antes de darlo por
bueno, o se pasa la suite en la máquina de Docker, o se arranca en local contra `WebhookGateway_dev`
con destinos de prueba y se mira el log.

### Fase 3 — Datos y mantenimiento (2–3 días) · **escrita y verificada en `_dev`, jobs sin crear**

- [x] La respuesta del destino solo se guarda en intentos fallidos (`DeliverySender`). En el caso
      bueno ni se lee el cuerpo: el manejador drena lo que quede al liberar la respuesta, así que no
      cuesta la reutilización de la conexión. A 400.000 entregas al día, casi todas buenas, el cuerpo
      y las cabeceras de los 200 eran la mayor parte de lo que ocupaba `DeliveryAttempt`, la tabla de
      más volumen, y nadie los leía nunca. Cubierto por `DeliverySenderTests`.
- [x] Purga de las tablas sin particionar, en `sp_Gateway_PurgeUnpartitionedLogs`
      (`db/12-purge-unpartitioned.sql`): `NotificationLog`, `AuditLog` y los muertos de
      `NotificationOutbox` (`Status = 3`), que el worker deja ahí al agotar los intentos de envío y
      tampoco se purgaban. `MessageDedupe` y `RefreshToken` se movieron ahí desde
      `sp_Gateway_PurgeExpiredPartitions`, porque son este mismo mecanismo —DELETE por lotes con
      descanso— y no tenían nada que hacer dentro de la purga de particiones. En seco **cuenta** lo
      que borraría, que es lo que hace útil la semana de `@DryRun = 1`.
- [x] Vigilante, en `sp_Gateway_Watchdog` (`db/13-watchdog.sql`): avisa si quedan menos de 3 meses de
      particiones, si el backlog pasa del umbral o si hay leases vencidos hace más de 15 minutos sin
      liberar (lo que significa que el despachador no está corriendo). Avisa **fallando**: lanza
      `RAISERROR` con severidad 16, así que el job falla y queda en su historial. Se hace así para no
      depender de que Database Mail esté configurado en una instancia que no es nuestra.
- [x] Script de los cuatro jobs: `db/14-sql-agent-jobs.sql`. **Los nombres llevan el nombre de la
      base** (`DB_NAME()`), porque los jobs son del servidor y dev y producción comparten instancia:
      sin eso, el job creado desde una base apuntaría a la otra. La purga entra con `@DryRun = 1` en
      sus dos pasos.

  | Job | Cuándo | Qué ejecuta |
  |---|---|---|
  | `… - Particiones futuras` | Domingos 02:00 | `sp_Gateway_EnsureFuturePartitions @MonthsAhead = 6` |
  | `… - Purga` | Diario 03:00 | `sp_Gateway_PurgeExpiredPartitions` y `sp_Gateway_PurgeUnpartitionedLogs`, los dos en seco |
  | `… - Estadisticas` | Diario 03:30 | `UPDATE STATISTICS` de las cuatro tablas de tráfico |
  | `… - Vigilancia` | Cada 10 min | `sp_Gateway_Watchdog` |

- [ ] **Ejecutar `db/14` está sin hacer, y es decisión tuya.** Crea jobs en `msdb`, que es de la
      instancia compartida y no de la base: no es un cambio que se haga sin avisar. Necesita
      pertenecer a `SQLAgentUserRole` o más. Cuando se ejecute, hay que dejar la purga una semana en
      seco, mirar el historial del job y solo entonces poner `@DryRun = 0` en sus dos pasos.
- [ ] Conectar el aviso del vigilante a un correo: definir un operador y marcar "notificar al fallar"
      en el job. Es el paso del DBA.
- [ ] Decidir con el DBA la política de copias (`SIMPLE` actual o `FULL` con copias de log). No es
      una tarea de código.

**Verificado contra `WebhookGateway_dev` (2026-10-01).** Los procedimientos se aplicaron y se
probaron ahí: el dry-run es de solo lectura y el borrado real se probó dentro de una transacción que
luego se deshizo (quedaron 0 filas de prueba).

- `sp_Gateway_PurgeExpiredPartitions @DryRun = 1` sigue funcionando después de quitarle los borrados
  por lotes: con la retención de 90/14/30 no hay nada que vaciar en esa base, y lo dice.
- `sp_Gateway_PurgeUnpartitionedLogs @DryRun = 1` informa de las cinco tablas con su columna de
  fecha, su corte y cuántas filas borraría.
- Borrado real: de 2 auditorías viejas + 1 reciente borró las 2 viejas; de 2 alertas borró la vieja;
  de la bandeja de salida borró la muerta y **no** tocó la viva, que es la que el worker todavía
  tiene que enviar.
- `sp_Gateway_Watchdog` no devuelve nada cuando todo está bien, y con el umbral forzado lanza
  `RAISERROR` de severidad 16 y error 50000, que es lo que hará fallar al job. Ojo al comprobarlo a
  mano: el `RAISERROR` va después del `SELECT`, así que un cliente que solo lea el primer resultado
  no ve el error y parece que no lo lanza.

**Un bug encontrado de paso, y arreglado.** `ThrottleLease.Dispose()` lanzaba
`ObjectDisposedException` cuando el freno de su destino se había reemplazado mientras la entrega
estaba en vuelo, que es lo que pasa cada vez que alguien cambia el ritmo o la concurrencia de un
destino en el panel. El comentario del código decía que las entregas en vuelo "terminan con él", pero
el semáforo se desechaba de inmediato. Como el `using` del despachador cae dentro de su try/catch, el
síntoma no era una caída sino un error en el log por cada entrega en vuelo en ese momento. Lo
encontró `EndpointThrottlesTests`.

### Fase 4 — Alertas y panel (2 días) · **escrita y verificada en `_dev`**

- [x] Alertas agrupadas: una por destino cada `Gateway:Notifications:DeadLetterGroupingMinutes`
      minutos (15 por defecto, 0 desactiva). El que spameaba era el aviso de **entrega descartada**,
      que salía por cada entrega fallida y a cada destinatario: un destino que responde 400 a todo
      descarta una entrega por mensaje, así que un pico de 2.000 eran miles de correos. Dentro de la
      ventana no se calla y punto, se **cuenta**, y el siguiente aviso dice "y N más desde el último
      aviso", que es la cifra que da el tamaño del problema. La ventana la lleva `DeadLetterWindows`
      en memoria del proceso: con dos instancias vivas pueden salir dos avisos en vez de uno, y dos
      en lugar de miles es exactamente el objetivo. Cubierto por `DeadLetterWindowsTests`, incluido
      el caso concurrente: de 200 descartes simultáneos del mismo destino, exactamente uno avisa.

      Las alertas de **salud** del destino ya estaban agrupadas de antes: `EndpointHealthTracker`
      solo dispara en las transiciones de estado (Healthy → Degraded → Down → Recovered), así que un
      destino caído da un correo, no mil. No hacía falta tocarlas.

- [x] `live-status`: los conteos por estado iban **sin filtro de fecha**, contando todo el histórico
      de `WebhookDelivery` en cada sondeo, y el panel puede sondear cada segundo. Con quince millones
      de filas al año, eso es un recorrido completo por segundo.

      Y no se arreglaba con un filtro plano de 24 h: la ventana de entrega por defecto son 72 horas,
      así que una entrega legítimamente pendiente puede tener tres días, y con un corte de 24 h un
      destino caído desde anteayer habría mostrado **la cola vacía** — justo lo contrario de lo que un
      monitor tiene que decir. Así que va en dos consultas: la cola activa (0, 1, 2) completa, que sale
      de índices filtrados por estado y solo contiene lo pendiente; y lo terminal (3 a 6), que es lo
      que crece para siempre, acotado a 24 h. El DTO lleva ahora `TerminalWindowHours` para que el
      panel pueda etiquetarlo. El feed de entregas recientes también se acotó: sin fecha, una
      integración sin entregas recorría la tabla entera hacia atrás buscando diez filas inexistentes.

- [x] La columna de duplicados contaba `Status = 2`, que es `NoSubscriptions`; `Duplicate` es 1. El
      panel llamaba "duplicado" a un mensaje que había llegado bien pero no tenía ninguna suscripción
      activa que lo reclamara: dos problemas opuestos —un emisor que reenvía frente a una
      configuración a medias— mostrados como el mismo. Ahora son dos columnas,
      `DuplicateMessages` y `NoSubscriptionMessages`.

- [x] Buscador de mensajes con `OPTION (RECOMPILE)`. Los filtros opcionales van con el patrón
      `@X IS NULL OR columna = @X`, y sin RECOMPILE el motor cachea el plan del primer juego de
      parámetros que le toque y lo reutiliza para todos: el plan de "los últimos 50 de todo" no sirve
      para "los del endpoint 7 del martes pasado". Con RECOMPILE planifica con los valores en la mano
      y descarta los predicados que no aplican, de modo que puede usar `IX_WebhookMessage_Search`.
      El coste es compilar en cada búsqueda, que la lanza una persona mirando una pantalla.

- [x] Panel (`frontend/`, fuera de este repo de git): `types.ts` con los dos campos nuevos, y en
      `monitor.tsx` las tarjetas terminales dicen la ventana (`· 24 h`) y hay una tarjeta nueva, "Sin
      suscripción", separada de "Duplicados". `npx tsc --noEmit` limpio. **No se ha mirado en el
      navegador**: haría falta levantar la API y entrar al panel, y el cambio son etiquetas, una
      tarjeta y una clase de rejilla.

**Verificado contra `WebhookGateway_dev` (2026-10-01).** Las dos consultas son de solo lectura; los
datos de prueba se sembraron dentro de una transacción que luego se deshizo (quedaron 0 filas).

- Cola activa: con 2 pendientes —una de ellas **de hace tres días**— y 1 reintentando, devuelve
  `Pending = 2` y `Retrying = 1`. La vieja cuenta, que es el punto de todo el diseño.
- Terminales: de 2 entregadas, una reciente y una de hace tres días, cuenta 1. La vieja no.
- Tráfico: con 2 duplicados y 3 sin suscripción sembrados, devuelve exactamente eso, cada uno en su
  columna.
- Buscador: sin filtros devuelve la página completa; filtrando por `Status = 1` devuelve las 2
  duplicadas y nada más; por endpoint y rango de fechas, las 6 del endpoint; y la página siguiente con
  `AfterId` no repite ninguna.

### Fase 5 — Despliegue en IIS (1 día) · **escrita; ejecutarla es tuyo**

Todo lo de esta fase está en **`docs/despliegue-iis.md`**, que antes no existía: `despliegue.md`
habla de Render y no mencionaba IIS ni una vez.

- [x] Guía de despliegue en IIS: comprobaciones previas, Hosting Bundle, app pool, permisos del
      `appsettings.json`, publicado, verificación posterior y qué vigilar los primeros días.
- [x] Los cuatro ajustes del app pool, con el porqué de cada uno: `No Managed Code`,
      `AlwaysRunning`, **tiempo de inactividad a 0** (el de fábrica son 20 minutos, y pasado ese
      rato sin peticiones HTTP IIS mata el proceso y el despachador con él) y reciclaje periódico
      desactivado (el de fábrica son 29 h, así que el pool se recicla a una hora distinta cada día).
      Más `preloadEnabled` en el sitio, que es lo que hace que `AlwaysRunning` sirva de algo.
- [x] `db/15-drop-legacy-dispatch-index.sql`, para borrar `IX_Delivery_Dispatch` **después** del
      despliegue. Lleva un guardia que aborta si `IX_Delivery_DispatchByEndpoint` no existe:
      borrarlo antes dejaría a la versión en producción recorriendo la tabla en cada ciclo.
- [x] Valoración del despachador como Servicio de Windows aparte, en la guía. **Recomendación: no
      separarlo todavía.** El arreglo de la fase 1 ya quita el motivo más fuerte —antes una
      `SqlException` del despachador paraba el proceso entero, recepción incluida— y con los cuatro
      ajustes del pool bien puestos el riesgo que la separación evita queda cubierto. La guía dice
      en qué tres casos conviene volver a mirarlo.

**Tres cosas que aparecieron al mirar esto de cerca, y están arregladas.**

- **El apagado ordenado no estaba funcionando.** `HostOptions.ShutdownTimeout` vale **5 segundos**
  por defecto, y el apagado del despachador —esperar las entregas en vuelo, volcar resultados,
  liberar leases— se da 15. El host dejaba de esperar antes de que acabara, así que los leases se
  quedaban colgados y esas entregas no volvían a la cola hasta vencer: **tres minutos quietas en
  cada reciclaje del pool**. Ahora son 30 s en `Program.cs`, el margen del despachador es
  configurable (`Gateway:Dispatcher:ShutdownGraceSeconds`) y detrás de IIS hay un tercer plazo,
  `shutdownTimeLimit` en `web.config`, cuyo valor de fábrica son 10 s. Los tres van en escalera:
  **15 < 30 < 45**.
- **`web.config` propio en el proyecto**, con `shutdownTimeLimit="45"` y, sobre todo, con
  `ASPNETCORE_ENVIRONMENT=Production` explícito. Sin esa variable el entorno ya sería Production,
  pero dejarlo implícito significa que basta con que alguien la defina como Development en la
  máquina para que se cargue `appsettings.Development.json` y producción escriba en
  `WebhookGateway_dev`. Solo lo lee IIS, así que el despliegue en Render no se entera.
- **`appsettings.Development.json` ya no se publica.** Sí se publicaba, de modo que el servidor de
  producción acababa con un archivo con cadena de conexión y contraseña apuntando a `_dev`. Sigue
  copiándose al compilar, así que depurar en local no cambia.

**Verificado.** `dotnet build` sin avisos, 184 unitarios en verde, y un `dotnet publish -c Release`
a una carpeta de prueba confirma que el `web.config` sale con los tres atributos puestos y que
`appsettings.Development.json` no viaja. `db/15` se probó contra `WebhookGateway_dev` dentro de
transacciones que se deshicieron (el DDL en SQL Server es transaccional): sin el índice nuevo el
guardia aborta y **no** borra el viejo; con él presente borra el viejo y deja el nuevo; y una
segunda pasada no falla.

De paso, los guardias de `db/14` y `db/15` usaban `RAISERROR` con severidad 20, que **solo puede
usar un sysadmin**: en este servidor compartido habrían fallado con un error de permisos en vez de
con su mensaje. Ahora usan severidad 16 y `SET NOEXEC ON`, que es el patrón que ya usaba
`db/setup-dev-db.sql`.

- [ ] **Ejecutar el despliegue es tuyo.** Los pasos que no puedo dar yo: aplicar `db/11` a
      producción, devolver `appsettings.json` a `Database=WebhookGateway`, configurar el app pool y
      publicar. Y después, `db/15`.

### 7.5 Revisión del 2026-10-05 — lo que faltaba para los 400.000 · **escrita; SQL medido en LocalDB**

Una revisión del código —no del plan— encontró que el despachador nuevo seguía teniendo el problema
que la fase 2 vino a quitar, en el caso para el que existe el gateway: un destino caído con mucho
pendiente. La verificación de la fase 2 se hizo con 40 filas y no podía verlo. Cinco paquetes:

- [x] **A. Claim y descubrimiento que no cuestan según el backlog.** El `ORDER BY NextAttemptAt, Id`
      obligaba a leer y ordenar todo lo vencido del destino (el índice está partido por mes) y,
      con `UPDLOCK`, a bloquearlo. El descubrimiento era un `SELECT DISTINCT` sobre todo lo
      pendiente. Medido en LocalDB con 300.000 pendientes, antes → ahora:

      | Consulta | Antes | Ahora |
      |---|---|---|
      | Claim de 20 | 6.228 bloqueos de fila, **escala a bloqueo de tabla**, 1.600 páginas, 235 ms | ~300 bloqueos, 0 escalados, ~566 páginas (casi todas, mantener los índices de las 20 filas), 4 ms |
      | Descubrimiento | 1.026–2.056 páginas | **12 páginas**, 5 ms |

      El claim va sin `ORDER BY` y los dos nombran `IX_Delivery_DispatchByEndpoint`: sin nombrarlo,
      el optimizador elegía `IX_Delivery_Backlog` para el claim y recorría la tabla agrupada para el
      descubrimiento (el `TOP (1)` le hace suponer que la primera fila valdrá). Pruebas nuevas en
      `LargeBacklogTests`.
- [x] **B. Lotes que caben en su lease.** `ClaimSizing` reclama solo lo que se alcanza a enviar en el
      75 % del lease, en el peor caso (todo agota el timeout, el cubo de ritmo vacío): con los
      valores por defecto 16, con una petición a la vez 4, a 6/min 13. Y el volcado de resultados
      ya no pisa una entrega que otro worker tiene o que ya se cerró (`Status IN (1, 2)` y
      `WorkerId` propio o nulo); si pasa, lo registra. Probado en LocalDB: de 4 resultados guarda
      los 2 que debe.
- [x] **C. Retraso por destino.** `dbo.fn_Gateway_DeliveryLag` (en `db/13`): cuánto lleva esperando
      la entrega vencida más antigua de cada destino. Se pide partición por partición: un `MIN`
      directo leía 1.233 páginas; así, 80. El vigilante avisa por encima de `@MaxLagMinutes` (15) y
      el monitor del panel muestra pendientes y retraso por destino, en rojo sobre
      `Gateway:Monitoring:MaxLagMinutes`.
- [x] **D. Retención editable desde el panel.** Tabla `dbo.RetentionPolicy` (`db/17`), pantalla
      Configuración (solo administradores, auditada) y la purga la lee en cada ejecución. Reglas:
      los cuerpos duran al menos la ventana de entrega más larga, y al revés, un destino no puede
      tener una ventana mayor que la retención de los cuerpos. **De paso:** el formulario de
      integración pedía «retención por integración» y la guardaba, pero la purga nunca la usó (es
      por particiones, global). Se quitó del panel; las columnas siguen en la base sin uso.
- [x] **E. Código de rechazo por endpoint de entrada** (`db/16`). Con respaldo global
      (`Gateway:Reception:TransientFailureStatusCode`) y usando la última configuración conocida
      cuando SQL no responde. Probado de punta a punta contra LocalDB dejando la base fuera de
      línea: el endpoint de GHL responde 429 y el del global 503. **Encontró un fallo que ya
      existía:** un endpoint que no estaba en memoria respondía 500, porque la estrategia de
      reintentos de EF Core envuelve el `SqlException` en un `RetryLimitExceededException` que la
      recepción no reconocía. Arreglado; falta repetir esa prueba, que Smart App Control bloqueó.

También: `setup-dev-db.sql` llevaba una copia vieja de `db/04` (con los borrados por lotes que la
fase 3 movió al 12); ahora está al día, y aplicado entero a una base LocalDB funciona.

**Lo que falta de esto:** ejecutar las suites (unitaria e integración) cuando Smart App Control suelte
los DLL —el SQL de A–E ya se midió aparte, contra LocalDB— y repetir la prueba de la base fuera de
línea con el arreglo del 500.

### 7.6 Prueba de carga en LocalDB (2026-10-05) · **hecha**

Todo en este equipo: API contra LocalDB, destino falso local y generador. Las herramientas están en
`tools/carga/` con su README, para repetirla.

**Prueba cruda: 300.000 webhooks lo más rápido posible** (48 clientes, destino a 100 ms, caído del
minuto 6 al 8). Se paró a mitad del drenaje.

- Recepción: **300.000 de 300.000 con 202**, media 570/s y picos de 890/s; p50 53 ms, p95 209 ms,
  p99 614 ms. Saturó LocalDB: las esperas eran `PAGELATCH` (inserciones peleando por la última
  página de cada tabla), esperables a ese ritmo y con 48 clientes a la vez.
- 0 escalados de bloqueo y 19 esperas de fila con hasta 294.000 pendientes.
- 182.405 entregadas = 182.405 recibidas por el destino, 0 duplicadas.
- **Encontró dos fallos:**
  1. *Carrera en la salud del destino.* Cuando el destino cambia de estado, las entregas en vuelo
     lo notan a la vez: varias escribían la misma transición, el `MERGE` sin `HOLDLOCK` chocaba con
     la clave primaria, y la excepción cortaba la entrega **después** del HTTP y **antes** de apuntar
     su resultado. La entrega se reenviaba al vencer el lease: un duplicado. **Arreglado:** cada
     destino tiene un cerrojo para su estado de salud, el `MERGE` lleva `HOLDLOCK` (dos instancias
     vivas), y el resultado se apunta antes que la salud y los avisos, que ya no pueden tumbarlo.
     `EndpointHealthConcurrencyTests` reproduce el fallo con el código anterior y pasa con el nuevo.
  2. *Techo de concurrencia por destino.* Se reclamaban lotes de 20 y se esperaba a la más lenta
     antes de pedir más: un destino no pasaba de 20 peticiones a la vez aunque tuviera 32, y rendía
     80–160/s con 100 ms de latencia. **Arreglado:** `EndpointPump` mantiene llena la concurrencia
     del destino y repone cuando se libera una cuarta parte.
- Espacio: con cuerpos del tamaño de los de GHL (~1 KB) salieron **~1,7 KB por mensaje**, unas tres
  veces los 590 B medidos en producción con cuerpos pequeños. Con 180/30/30 eso puede ser 40–60 GB,
  no 20. Medir con los webhooks reales y ajustar en Configuración.

**Prueba realista: 63.000 webhooks a 70/s fijos durante 15 minutos**, ya con los dos arreglos.
Destino lento (1 s por petición) con 96 de concurrencia, caído del minuto 6 al 8.

| Criterio | Umbral | Resultado |
|---|---|---|
| Caídas del proceso | 0 | 0 |
| Errores en recepción | 0 | **0**: 63.000 de 63.000 con 202 |
| Latencia de recepción p95 | < 200 ms | **4 ms** (p50 2,7 ms, p99 26 ms) |
| Entregas perdidas o duplicadas | 0 | **0**: 63.001 entregadas = 63.001 únicas en el destino |
| Retraso con el destino sano | < 10 s | 0–1 s |
| Retraso tras 2 min de caída | < 15 min | Máximo 2 min; se pone al día a ~78/s frente a 70/s de entrada |
| Concurrencia usada | — | Hasta **92 en curso** (antes, como mucho 20) |
| Bloqueos SQL | Sin escalado | 0 escalados, 0 esperas de fila |
| Salud del destino | Caída y recuperación | Sano → Caído → Recuperado → Sano, sin errores en el log |

Lo que no se probó aquí: dos instancias vivas a la vez (el claim atómico lo cubre
`DeliveryClaimTests`), varios destinos con uno lento frenando a otro, y el comportamiento en el
servidor real.

**Sin explicar:** en la prueba cruda el despacho bajó un rato a 6–30/s (15:35–15:41) sin
crecimiento de archivos ni bloqueos que lo justifiquen. No se repitió en la realista.

### 7.7 Prueba contra `_dev` del servidor real (2026-10-05) · **hecha, abortada por el guardián**

La API en este equipo contra `WebhookGateway_dev` (servidor compartido, ~23 ms por viaje a SQL), con
el destino falso local, los dos destinos reales desactivados mientras duró y una integración propia
(`carga`, desactivada al acabar). Rampa de 2 min a 20/s y después 70/s, con un guardián
(`guardian-dev.ps1`) que abortaba si la ida y vuelta a SQL pasaba de 200 ms dos veces seguidas, si
producción dejaba de responder sano o si la recepción daba algo distinto de 202.

- **El guardián abortó en el webhook 47.233 de 60.000** por dos viajes a SQL de más de 200 ms
  (llegó a 658). Esos picos aparecieron también a 20/s y con la cola vacía y sin carga (329 ms a
  las 17:09:43): vienen de la red o de otra actividad del servidor, no de la prueba. Con más
  datos, el umbral del guardián puede pedir tres seguidas en vez de dos.
- **Recepción:** 47.233 de 47.233 con 202; p50 27 ms (casi todo red), p95 290 ms, p99 454 ms.
- **Servidor compartido:** CPU entre el 1 % y el 5 % durante toda la prueba. Producción respondió
  sano todo el tiempo (150–340 ms, lo mismo que antes de empezar).
- **Exactitud:** 47.234 entregadas = 47.234 recibidas por el destino, 0 duplicadas.
- **El despacho no llegó a 70/s:** 48–66/s, así que el retraso subió hasta ~2 min (por debajo de
  los 15 aceptados). En LocalDB, con el mismo destino, iba a 70–80/s. La diferencia es la red:
  cada vez que la bomba repone hace tres viajes a SQL (volcar, reclamar, cargar cuerpos), y con
  25 ms y picos de cientos de ms la concurrencia se vacía mientras espera. Si el IIS de producción
  está junto al SQL Server, esto no aplica; si no, conviene que la bomba reclame lo siguiente
  mientras envía, en vez de entre medias.

**Informe de capacidad (`db/18`, 2026-10-05).** `sp_Gateway_CapacityReport` responde «¿se queda corta
la configuración?» con lo que ya se guarda: la concurrencia media de cada destino sale de los
`DeliveryAttempt` por la ley de Little (suma de duraciones ÷ ventana) y se compara con su
`MaxConcurrency`, su ritmo y su retraso; en global, con `MaxGlobalConcurrency`,
`MaxEndpointsInParallel` y el pool. Probado con los datos de la prueba realista (70,5 de 96 en el
tramo estable, 78,1 recuperándose) y con casos simulados de despachador parado, concurrencia al
límite y cupo global lleno. Cuesta ~550 páginas por consulta.

### Fase 6 — Prueba de carga y salida (2–3 días) · **hecha en LocalDB (§7.6)**

**Prueba original:** 70 mensajes/s sostenidos durante 30 minutos (~126.000 mensajes), 1 destino por
mensaje, contra LocalDB, con destinos falsos: uno responde en 2 s, otro limitado a 60/min y otro
caído durante 5 minutos. Dos escenarios más, añadidos en la revisión de §7.5: un destino que se
recupera con 300.000 pendientes, y un destino lento al que le llega más de lo que su ritmo da
(el retraso tiene que verse crecer en el panel y el vigilante tiene que avisar).

| Criterio | Umbral |
|---|---|
| Caídas del proceso | 0 |
| 503 en recepción con SQL sano | 0 |
| Latencia de recepción p95 | < 200 ms |
| Entregas duplicadas con 2 instancias vivas | 0 |
| Destinos sanos durante la prueba | < 10 s de retraso aunque otro esté lento o caído |
| Backlog al terminar | Vuelve a 0 en < 15 min (salvo el destino limitado) |
| Alertas del destino caído | 1 de caída y 1 de recuperación |
| Bloqueos SQL | Sin escalado a bloqueo de tabla en `WebhookDelivery` |

- [x] Resultados en §7.6. Las reglas que salieron de ellos están en `CLAUDE.md` (despachador, 2 y 8).
- [ ] Opcional: prueba acotada contra `_dev` del servidor real, con las condiciones acordadas el
      2026-10-05: horario de poco tráfico y aviso al administrador, carga escalonada y corta
      (10/s, 30/s, pico de 70/s de 2–3 min), criterios para abortar, destinos solo locales, refresco
      del panel a 5–10 s, y los scripts 13, 16, 17 y 04 aplicados antes.

---

## 8. Preguntas abiertas

- ~~¿Qué destinos recibirán los 400k/día?~~ Una integración nueva de GHL con un destino lento; ver §3.
- ~~¿Retención de 90 días o un año?~~ Editable desde el panel; por defecto 180/30/30.
- ¿Qué pérdida de datos es aceptable ante una caída (`SIMPLE` o `FULL`)?
- ¿El repositorio donde está `appsettings.json` es privado? ¿Se subió alguna vez a uno público?
