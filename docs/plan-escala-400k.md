# Plan de escala a 300.000–400.000 mensajes/día — estado y traspaso

Documento de continuidad. Quien retome este trabajo (persona o Claude) debe leer primero
`CLAUDE.md`, después este archivo, y solo entonces tocar código.

Última actualización: 2026-10-01. **Fases 1, 2, 3 y 4 escritas. `dotnet build` sin avisos y los
184 unitarios en verde. El SQL de las cuatro fases está verificado contra `WebhookGateway_dev`. Lo
que no se ha visto funcionar es el despachador nuevo entero: eso necesita la suite de integración,
que pide Docker. Ver el final de cada fase.**

> **La verificación con Docker va a una máquina aparte.** En este equipo no hay Docker ni se va a
> poner: hay otra máquina dedicada a eso, y ahí se pasará la suite de integración más adelante
> (decidido el 2026-10-01). Hasta entonces, el avance independiente por destino y el techo de
> capacidad quedan escritos y compilando pero sin haberse visto funcionar en conjunto.

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
