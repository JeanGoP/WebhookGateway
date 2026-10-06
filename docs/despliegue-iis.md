# Despliegue en IIS

El destino real de producción: un IIS con 8 núcleos, con la API y el despachador en el mismo
proceso. Para el despliegue en Render —otro escenario, con Docker— ver `despliegue.md`.

Lo que hace este despliegue distinto de un sitio web normal: **dentro del proceso vive un worker
que tiene que estar siempre despierto**. Casi todo lo que hay abajo existe por eso.

---

## Antes de publicar: siete comprobaciones

**1. `appsettings.json` tiene que decir `Database=WebhookGateway`.**

```powershell
Select-String -Path .\src\WebhookGateway.Api\appsettings.json -Pattern 'Database=\w+'
```

Hoy apunta a `WebhookGateway_dev`. Si se publica así, **producción escribe en la base de
desarrollo** y nadie se entera hasta que falta algo. Es la regla 4 de la §5 de
`plan-escala-400k.md` y el error más fácil de cometer de todo el despliegue.

**2. El esquema de producción tiene que estar al día.** ✅ **Hecho el 2026-10-05** (19:28, hora del
servidor) con `db/despliegues/2026-10-05-produccion.sql`, que aplica 10, 11, 12, 13, 16, 17, 04 y 18
en ese orden y comprueba cada pieza. Verificado después: las nueve piezas, la retención 180/30/30 y
el índice nuevo creado con `ONLINE = ON`. Todo es compatible con el código que corre hoy. Si
hubiera que deshacerlo: `db/despliegues/2026-10-05-produccion-vuelta-atras.sql`, y **solo** con el
código anterior publicado.

`db/15-drop-legacy-dispatch-index.sql` va **después** de desplegar, no antes: mientras corra el
código viejo, el índice que borra es el que sostiene su claim. El script lleva un guardia que
aborta si el nuevo no existe.

**3. La cadena de conexión debería llevar `Application Name=WebhookGateway`.** Hoy no lo lleva.
No tiene consecuencia mientras Resource Governor siga inactivo (confirmado, `is_enabled = 0`),
pero su clasificador enruta por ese nombre y no por el login. Si algún día se activa, sin este
valor la carga del gateway no queda capada.

**4. Copia de seguridad completa y horario de poco tráfico.** Regla 7 de la §5 del plan.

**5. Los webhooks que lleguen durante el despliegue.** Mientras IIS publica, responde 503 por su
cuenta, y las apps del Marketplace de GHL **solo reintentan un 429**: lo que llegue en ese rato se
pierde. Hay tres formas de cubrirlo:

- Desplegar en la hora de menos tráfico.
- Después, reenviar los fallidos desde el panel de logs de webhooks de GHL, que lo permite a mano.
- Si el IIS tiene URL Rewrite, una regla temporal que responda 429 a `/in/*` durante el despliegue.

**6. El código de rechazo para GHL.** Cuando SQL no responde, el gateway rechaza para que el emisor
reintente, con el código que cada endpoint de entrada tenga configurado (panel → endpoint de entrada
→ «Código si no se puede guardar»). Para la integración de GHL, **429**. Y como respaldo para cuando
el proceso arranca con SQL ya caído y no sabe qué pidió cada endpoint, en `appsettings.json`, dentro
de `Gateway`:

```json
"Reception": { "TransientFailureStatusCode": 429 }
```

Sin él vale 503, que es lo estándar y lo que GHL da por perdido.

**7. Capacidad, logs y enlaces en `appsettings.json`.** Lo que hoy trae el archivo son los valores
de desarrollo. Para producción, dentro de lo que ya hay:

```json
"Logging": {
  "LogLevel": {
    "Default": "Warning",
    "Microsoft.AspNetCore": "Warning",
    "Microsoft.EntityFrameworkCore.Database.Command": "Warning",
    "System.Net.Http.HttpClient": "Warning"
  }
},
"Gateway": {
  "Dispatcher": {
    "MaxGlobalConcurrency": 256,
    "MaxEndpointsInParallel": 32,
    "MaxPerEndpointPerClaim": 40
  },
  "Reception":  { "TransientFailureStatusCode": 429 },
  "Monitoring": { "MaxLagMinutes": 15 },
  "Notifications": { "DashboardBaseUrl": "http://localhost:5174" }
}
```

- `Default: Warning` y `HttpClient: Warning`: con `Information`, cada entrega escribe varias líneas
  de log; a 400.000 al día son millones de líneas y disco que se llena solo.
- `MaxGlobalConcurrency` 256 y `MaxPerEndpointPerClaim` 40: con los de fábrica (128 y 20) un destino
  con `MaxConcurrency` alto no llega a llenarla. El informe de capacidad dice si se quedan cortos.
- **El panel no está publicado**: se ejecuta en local cuando hace falta, apuntando a la API de
  producción (`VITE_API_URL` en `frontend/.env`). Por eso `DashboardBaseUrl` —el enlace de los
  correos de alerta— se queda en `http://localhost:5174`, y CORS tiene que seguir admitiendo ese
  origen: `AllowedOrigins` en `*` como hoy, o `http://localhost:5174`.
- `MaxLagMinutes` tiene que coincidir con el `@MaxLagMinutes` del job de Vigilancia (15).

---

## Instalación en el servidor

**El Hosting Bundle de .NET 10.** Es lo que instala el módulo `AspNetCoreModuleV2`, sin el cual
IIS no sabe arrancar la aplicación. Se instala **después** de IIS; si se hace al revés, hay que
repararlo o ejecutar `iisreset` tras instalarlo.

**El módulo de inicialización de aplicaciones** (*Application Initialization*), que viene con
IIS pero no activado. Es lo que permite `preloadEnabled`, y sin él el proceso no arranca hasta
la primera petición HTTP: una instancia recién reciclada no despacharía nada hasta que alguien
entrase al panel.

`InvariantGlobalization` está en `false` y eso aquí no da problema: en Windows el ICU lo aporta
el propio sistema. Esa restricción es de la imagen Docker, no de IIS.

---

## El app pool: lo que de verdad importa

Un sitio web normal se puede dormir entre visitas. Este no: **si IIS duerme el proceso, el
despachador deja de entregar y la cola crece en silencio.** Cuatro ajustes, y los cuatro tienen
que estar:

| Ajuste | Valor | Por qué |
|---|---|---|
| **.NET CLR version** | `No Managed Code` | ASP.NET Core no usa el CLR de IIS. Con cualquier otro valor, el pool carga un runtime que no hace falta |
| **Start Mode** | `AlwaysRunning` | El proceso arranca al iniciar IIS, sin esperar la primera petición |
| **Idle Time-out** (minutos) | `0` | El valor de fábrica son **20 minutos**: pasado ese rato sin peticiones HTTP, IIS mata el proceso. El despachador moriría con él, aunque tuviera entregas pendientes. Este es el ajuste que más veces se olvida y el que más caro sale |
| **Regular Time Interval** (minutos) | `0` | El valor de fábrica son **1740 minutos** (29 h), así que el pool se recicla a una hora distinta cada día, imprevisible. Mejor desactivarlo y, si hace falta reciclar, programarlo a una hora concreta |

Y en el sitio, `preloadEnabled="true"`, que es lo que hace que `AlwaysRunning` sirva de algo.

```powershell
# El pool, con nombre a elegir.
Import-Module WebAdministration
$pool = 'WebhookGateway'

Set-ItemProperty "IIS:\AppPools\$pool" -Name managedRuntimeVersion   -Value ''
Set-ItemProperty "IIS:\AppPools\$pool" -Name startMode               -Value 'AlwaysRunning'
Set-ItemProperty "IIS:\AppPools\$pool" -Name processModel.idleTimeout -Value '00:00:00'
Set-ItemProperty "IIS:\AppPools\$pool" -Name recycling.periodicRestart.time -Value '00:00:00'

# El sitio: arrancar sin esperar la primera peticion.
Set-ItemProperty 'IIS:\Sites\WebhookGateway' -Name applicationDefaults.preloadEnabled -Value $true
```

**Dos instancias vivas durante el reciclaje es normal y está previsto.** IIS solapa el proceso
viejo y el nuevo, así que durante unos segundos hay dos despachadores reclamando de la misma
cola. El claim es atómico con lease y `READPAST` precisamente para esto: ninguna entrega sale
dos veces. No hace falta desactivar el solapamiento.

---

## Los secretos

La decisión tomada es que **se quedan en `appsettings.json`**, sin variables de entorno ni
user-secrets (§3 del plan). La mitigación es de permisos del sistema de archivos: que ese archivo
lo pueda leer la cuenta del pool y nadie más.

```powershell
$archivo = 'C:\inetpub\WebhookGateway\appsettings.json'
$cuenta  = 'IIS AppPool\WebhookGateway'   # la identidad del pool

icacls $archivo /inheritance:r
icacls $archivo /grant:r "${cuenta}:(R)"
icacls $archivo /grant:r 'Administrators:(F)'
```

Consecuencia que conviene saber: **`subir-backend.ps1` abortará** si se ejecuta con el
`appsettings.json` de producción en el árbol de trabajo. Lleva un guardia que busca `Password=`
en lo que se iba a subir. Eso es que el guardia funciona, no un fallo.

---

## Publicar

El perfil `FolderProfile.pubxml` publica a una carpeta; de ahí se copia al sitio de IIS.

```powershell
dotnet publish .\src\WebhookGateway.Api\WebhookGateway.Api.csproj -c Release -o .\publish\api
```

**`appsettings.Development.json` ya no se publica** (`CopyToPublishDirectory="Never"` en el
csproj). Sigue copiándose al compilar, así que depurar en local funciona igual. Antes sí viajaba,
y eso ponía en el servidor de producción un archivo con una cadena de conexión y su contraseña
apuntando a `WebhookGateway_dev`: inerte mientras el entorno sea `Production`, pero a una variable
de entorno de distancia de que producción escribiera en la base de desarrollo. Si el archivo no
viaja, esa puerta no existe.

El `web.config` del proyecto se copia tal cual al publicar. Lleva tres cosas que no vienen por
defecto:

- `shutdownTimeLimit="45"` — el plazo que IIS da al proceso para terminar solo. El de fábrica son
  10 segundos y no alcanza.
- `ASPNETCORE_ENVIRONMENT=Production` explícito, para que nadie pueda hacer que producción cargue
  `appsettings.Development.json` —que apunta a `WebhookGateway_dev`— definiendo una variable de
  entorno en la máquina.
- `hostingModel="inprocess"`.

**Los tres plazos del apagado van en escalera**, y romperla tiene consecuencias visibles:

```
Gateway:Dispatcher:ShutdownGraceSeconds (15)  <  HostOptions.ShutdownTimeout (30)  <  shutdownTimeLimit (45)
```

Si el proceso muere antes de terminar su apagado ordenado, las entregas que tenía reclamadas se
quedan con el lease colgado y no vuelven a la cola hasta que vence: **tres minutos quietas en cada
reciclaje**. El `ShutdownTimeout` del host valía 5 segundos por defecto, menos que el margen del
propio despachador, así que esto no estaba funcionando.

---

## Comprobar que quedó bien

```powershell
Invoke-RestMethod https://<host>/health/live    # proceso vivo, no toca SQL
Invoke-RestMethod https://<host>/health/ready   # 200 si alcanza SQL; 503 si no
Invoke-RestMethod https://<host>/
```

Y en el log de arranque, la línea que dice a qué base se conectó:

```
SQL Server destino: 200.7.96.218 / WebhookGateway (entorno Production)
```

**Si dice `WebhookGateway_dev`, para y revisa el `appsettings.json`.**

Luego, que el despachador esté realmente despachando: `EXEC dbo.sp_Gateway_Watchdog;`. Si
devuelve filas hablando de leases huérfanos, de backlog o de retraso, algo no arrancó. El retraso es
la cifra que importa: cuánto lleva esperando la entrega vencida más antigua de cada destino, con un
umbral de 15 minutos (`@MaxLagMinutes`). El monitor del panel muestra la misma cifra por destino; su
umbral es `Gateway:Monitoring:MaxLagMinutes`, y conviene que coincidan.

Y después de este despliegue, `db/15-drop-legacy-dispatch-index.sql`.

---

## El despachador como servicio aparte: cuándo merece la pena

`Gateway:Dispatcher:Enabled = false` deja la instancia solo recibiendo. Eso permite publicar la
API en IIS y el despachador como Servicio de Windows aparte, con el mismo binario y otra
configuración. **Hoy no hace falta**, y conviene saber por qué, para no hacerlo por inercia:

**A favor de separarlos**

- El despachador deja de depender del ciclo de vida del pool: ni tiempo de inactividad, ni
  reciclajes, ni que un error de la API se lleve por delante el despacho.
- Se puede reiniciar la API sin parar las entregas, y al revés.
- Un Servicio de Windows arranca con la máquina sin depender de `AlwaysRunning` ni de
  `preloadEnabled`.

**En contra**

- Dos despliegues, dos configuraciones y dos cosas que vigilar, donde hoy hay una.
- Dos `appsettings.json` con los mismos secretos en dos sitios, y por tanto dos juegos de
  permisos que mantener en su sitio.
- El arreglo de la fase 1 ya quita el motivo más fuerte para separarlos: antes, una
  `SqlException` del despachador paraba el proceso entero y con él la recepción. Eso ya no pasa.

**Recomendación: no separarlos todavía.** Con los cuatro ajustes del pool bien puestos, el riesgo
que la separación evita queda cubierto. Merece la pena volver a mirarlo si pasa alguna de estas:

- Los reciclajes del pool resultan ser más frecuentes o menos controlables de lo esperado.
- La prueba de carga de la fase 6 muestra que la recepción y el despacho se estorban por CPU o
  por conexiones a SQL.
- Hace falta escalar la recepción a más de una instancia: entonces conviene un solo despachador y
  varios receptores, que es justo lo que este interruptor permite.

---

## Cosas que vigilar los primeros días

- **Si la configuración se queda corta:** `EXEC dbo.sp_Gateway_CapacityReport;` (últimos 15 min).
  Por destino da la concurrencia media que usó frente a su `MaxConcurrency`, el ritmo frente a su
  `RateLimitPerMinute`, el retraso y un diagnóstico; y en global, el uso de `MaxGlobalConcurrency`,
  de `MaxEndpointsInParallel` y del pool de SQL. Los topes de `appsettings.json` se le pasan como
  parámetros si no son los recomendados (256, 32, 40 y 120).

- **El tamaño del pool de conexiones.** `MaxPoolSize` está en 120, y el despachador puede tener
  hasta `MaxEndpointsInParallel` (32) destinos avanzando, cada uno abriendo conexiones cortas para
  reclamar y volcar, además de las peticiones de la API. Si aparecen timeouts de "connection pool",
  ahí está el techo.
- **Los jobs del Agente SQL** (`db/14-sql-agent-jobs.sql`): ✅ creados en producción el 2026-10-05
  (detalle en `jobs-sql-agent.md`). El de vigilancia es el que avisaría de casi todo lo demás. El de purga ya no fija la retención: la lee
  de la tabla que se edita en el panel (Configuración).
- **La purga entra en seco.** Una semana mirando lo que *habría* borrado antes de dejarla borrar.
