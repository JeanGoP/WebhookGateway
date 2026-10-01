# Plan de escala a 300.000–400.000 mensajes/día — estado y traspaso

Documento de continuidad. Quien retome este trabajo (persona o Claude) debe leer primero
`CLAUDE.md`, después este archivo, y solo entonces tocar código.

Última actualización: 2026-10-01. **Fase 0 prácticamente cerrada. Fase 1 escrita, compilando y con
el arreglo del procedimiento verificado contra `WebhookGateway_dev`. Fase 2 escrita y compilando,
pero el despachador nuevo no se ha visto funcionar: falta Docker para los tests de integración, y
Control de aplicaciones de Windows está bloqueando ahora mismo la DLL de los unitarios. Ver el final
de cada fase.**

> **La verificación pendiente va a una máquina aparte.** En este equipo no hay Docker ni se va a
> poner: hay otra máquina dedicada a eso, y ahí se pasará la suite de integración más adelante
> (decidido el 2026-10-01). Hasta entonces, el claim por destino, el índice nuevo y el avance
> independiente quedan escritos y compilando pero sin haberse visto funcionar. Esa máquina resuelve
> además el bloqueo de Control de aplicaciones de Windows que impide ejecutar aquí los unitarios.

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
| Retención | Mensajes y entregas 90 días, cuerpos 14, intentos 30. Respuesta del destino solo en intentos fallidos. *Con las cifras medidas, se puede reconsiderar un año de historial (~42 GB).* |
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

### Fase 1 — Estabilidad (1–2 días) · **escrita, pendiente de verificar**

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

### Fase 2 — Nuevo despachador (4–6 días) · **escrita, pendiente de verificar**

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

**Cómo verificar el resto.** `dotnet build` queda sin avisos. Los unitarios **no se pudieron
ejecutar**: Control de aplicaciones de Windows (Smart App Control, `VerifiedAndReputablePolicyState
= 1`) bloquea la DLL de test recién compilada con `0x800711C7`, en Debug y en Release. Antes de
añadir `EndpointThrottlesTests` sí corrieron: 129 verdes y los 30 de `RateBudget`. Lo que queda sin
pasar ni una vez es `EndpointThrottlesTests`. Y los de integración siguen omitidos por no haber
Docker, que es también lo que impide probar el claim nuevo y el índice contra un motor real.

Por tanto, **el despachador nuevo no se ha visto funcionar todavía**. Antes de darlo por bueno, o se
pasa la suite en la máquina de Docker, o se arranca en local contra `WebhookGateway_dev` con destinos
de prueba y se mira el log.

### Fase 3 — Datos y mantenimiento (2–3 días)

- [ ] Guardar cabeceras y cuerpo de la respuesta solo en intentos fallidos
      (`DeliverySender` / `DeliveryRecorder`).
- [ ] Añadir purga de `NotificationLog` y `AuditLog` (no están particionadas ni se purgan).
- [ ] Crear los jobs de SQL Agent:

  | Job | Frecuencia | Qué ejecuta |
  |---|---|---|
  | Particiones futuras | Semanal, domingo 2:00 | `EXEC dbo.sp_Gateway_EnsureFuturePartitions @MonthsAhead = 6;` |
  | Purga | Diaria, 3:00 | `EXEC dbo.sp_Gateway_PurgeExpiredPartitions @MetadataRetentionDays = 90, @PayloadRetentionDays = 14, @AttemptRetentionDays = 30, @DryRun = 0;` + purga por lotes de `NotificationLog`/`AuditLog` |
  | Estadísticas | Diaria, 3:30 | `UPDATE STATISTICS` de las cuatro tablas de tráfico |
  | Vigilancia | Cada 10 min | Avisar si quedan < 3 meses de particiones o si el backlog (`Status IN (0,2)`) supera un umbral |

  Notas: el SP de purga trae `@DryRun = 1` por defecto; la purga solo borra meses completos
  (14 días reales = 14–45); `TRUNCATE` toma un bloqueo exclusivo breve, por eso de madrugada.
- [ ] Decidir con el DBA la política de copias (`SIMPLE` actual o `FULL` con copias de log).

### Fase 4 — Alertas y panel (2 días)

- [ ] Agrupar alertas: una por destino cada X minutos.
- [ ] `live-status`: conteos limitados a las últimas 24 h y corregir la columna de duplicados.
- [ ] Buscador de mensajes con `OPTION (RECOMPILE)`.

### Fase 5 — Despliegue en IIS (1 día)

- [ ] Una vez desplegado el despachador nuevo, borrar el índice `IX_Delivery_Dispatch`: lo usaba el
      claim con `ROW_NUMBER()` y ya no lo usa nadie. Mientras producción corra el código anterior
      tiene que seguir ahí, porque es el que sostiene su claim.
- [ ] App pool con `AlwaysRunning`, sin tiempo de inactividad, reciclaje controlado.
- [ ] Valorar el despachador como Servicio de Windows aparte
      (`Gateway:Dispatcher:Enabled = false` en la API).

### Fase 6 — Prueba de carga y salida (2–3 días)

**Prueba:** 70 mensajes/s sostenidos durante 30 minutos (~126.000 mensajes), 1 destino por
mensaje, en SQL local de Docker, con destinos falsos: uno responde en 2 s, otro limitado a
60/min y otro caído durante 5 minutos.

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

- [ ] Actualizar las cifras de `CLAUDE.md` con los resultados finales.

---

## 8. Preguntas abiertas

- ¿Qué destinos recibirán los 400k/día y qué límite de ritmo aceptan?
- ¿Retención de 90 días o un año (~14 GB frente a ~42 GB)?
- ¿Qué pérdida de datos es aceptable ante una caída (`SIMPLE` o `FULL`)?
- ¿El repositorio donde está `appsettings.json` es privado? ¿Se subió alguna vez a uno público?
