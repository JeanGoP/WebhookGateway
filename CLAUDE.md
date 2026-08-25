# WebhookGateway

Gateway de webhooks configurable. Recibe de múltiples sistemas, garantiza que nada se
pierde, y entrega a cada destino al ritmo que ese destino aguanta.

El plan de arquitectura completo está en `docs/arquitectura.html`. Este archivo es el
contrato de trabajo: **léelo entero antes de escribir código y no lo contradigas.**

En qué punto está el trabajo, qué falta y qué decisiones quedaron abiertas:
**`docs/estado-f4.md`**. Léelo también antes de continuar una fase a medias.

---

## Las cifras que gobiernan las decisiones

| Métrica | Valor |
|---|---|
| Promedio | 14 webhooks/min (0,23/s) |
| Pico | 2.000/min, en ráfagas de minutos |
| Volumen diario | 20.000 mensajes → ~40.000 entregas |
| Filas/año | ~7,3 M mensajes, ~15 M entregas |
| Base de datos | ~4 GB |

**No hay problema de rendimiento que resolver.** El valor del sistema es el suavizado:
absorber un pico de 2.000/min y drenarlo a un destino que solo tolera 60/min sin perder
nada. Si una optimización complica el código y no sirve a esa meta, no va.

---

## Reglas de arquitectura

### Lo que NO se usa

Prohibido salvo justificación explícita en el PR:

| Prohibido | Motivo | En su lugar |
|---|---|---|
| Patrón Repository | `DbContext` ya es repositorio + unit of work | `DbContext` directo |
| MediatR / CQRS | Un handler por operación, coste sin retorno | Servicios inyectados |
| AutoMapper | Fallos en runtime, mapeos invisibles | Métodos de extensión `ToDto()` |
| DTO por capa | Tres formas de lo mismo que sincronizar | Un DTO en el borde HTTP |
| Controllers | Ceremonia de clase y atributos | Minimal APIs por recurso |
| Excepciones para flujo esperado | Un webhook rechazado no es excepcional | `Result<T>` |

**El criterio general:** una abstracción con una sola implementación prevista es
ceremonia; una con varias se gana su lugar. `IOutboundAuthProvider` tiene seis
implementaciones y se queda. `IWebhookRepository` tendría una y no existe.

### Estructura

```
src/
├── WebhookGateway.Core        dominio, casos de uso, interfaces. Sin dependencias externas.
├── WebhookGateway.Data        EF Core + Dapper, migraciones, cifrado.
├── WebhookGateway.Dispatcher  worker: claim, reintentos, circuit breaker, rate limit.
└── WebhookGateway.Api         minimal APIs. /in/* recepción, /api/* panel. Aloja el worker.
tests/
├── WebhookGateway.UnitTests
└── WebhookGateway.IntegrationTests   Testcontainers con SQL Server
frontend/                             Vite + React + TypeScript
```

Referencias permitidas: `Data → Core`, `Dispatcher → Core, Data`, `Api → todos`.
**`Core` no referencia a nadie.** Si necesitas EF dentro de `Core`, la interfaz está
mal puesta.

`Dispatcher` es un proyecto aparte aunque hoy se aloje dentro de `Api`. Esa separación
compra poder desplegarlo por separado cambiando configuración. No la disuelvas.

---

## Acceso a datos

Dos rutas, deliberadamente:

- **EF Core** para el CRUD de configuración (integraciones, endpoints, suscripciones,
  usuarios). La productividad manda y el volumen es irrelevante.
- **Dapper** para el camino caliente: el claim con lease, los inserts en batch de
  intentos, la búsqueda del panel. Ahí se necesita SQL exacto y predecible.

El SQL de Dapper vive en constantes `const string` dentro de la clase que lo usa, no en
archivos sueltos ni en cadenas construidas por concatenación.

La frontera es limpia: **EF Core solo conoce las tablas de configuración.** Las de
tráfico —mensajes, entregas, intentos— no están en el `DbContext` en absoluto. Sus claves
incluyen la columna de partición, lo que complicaría el modelo de EF sin dar nada a
cambio, y todo lo que se hace con ellas necesita SQL exacto.

**El esquema es la fuente de verdad y no hay migraciones de EF.** Los scripts numerados
de `db/` son idempotentes y definen todo: particionado, índices filtrados, compresión y
Resource Governor. Una sola fuente, sin deriva posible entre dos.

Las fechas persistidas son `DateTime` en UTC sobre `datetime2(3)`. `DateTimeOffset` solo
se usa para interpretar cabeceras HTTP. Esto no es estilo: las columnas de partición son
fechas, y `datetime2` particiona sin conversores de por medio.

**La cadena de conexión debe incluir `Application Name=WebhookGateway`.** No es
cosmético: el clasificador de Resource Governor enruta por ese nombre, no por el login,
porque el login lo decide la empresa y puede cambiar. Sin ese valor, la carga del
gateway no queda capada en el servidor compartido.

**`WebhookDelivery.CreatedAt` es siempre el `ReceivedAt` de su mensaje.** No es una
casualidad de la implementación: es lo que permite localizar el cuerpo con un seek sobre
la clave agrupada `(ReceivedAt, MessageId)` en vez de recorrer todas las particiones. Si
el reenvío manual de F4 crea entregas nuevas, tiene que respetar esta invariante.

**`InvariantGlobalization` va en `false`.** `Microsoft.Data.SqlClient` necesita ICU para
abrir la conexión y lanza `NotSupportedException` si está activado. Consecuencia para el
despliegue: la imagen Docker debe basarse en el runtime `aspnet` normal, no en las
variantes `-alpine` ni `-chiseled`, que no traen ICU.

La purga se hace con `TRUNCATE TABLE … WITH (PARTITIONS (…))`, no con `SWITCH` ni con
`DELETE`. Está mínimamente registrada, no necesita tablas de staging, y en una instancia
compartida esa diferencia es lo que separa una purga invisible de una llamada del DBA.

---

## Convenciones de C#

- `net10.0`, nullable habilitado, `ImplicitUsings`, warnings como errores.
- Namespaces con ámbito de archivo (`namespace X;`). Un **concepto** por archivo: los
  tipos pequeños y estrechamente ligados (los enums, las variantes de una configuración)
  van juntos; todo lo que tenga comportamiento propio va aparte.
- **Ningún archivo por encima de 200 líneas.** Si crece, se parte. Esto no es estético:
  un archivo grande hay que leerlo entero para tocar una línea.
- `record` para datos inmutables (DTOs, configuraciones, resultados). `class` para lo
  que tiene comportamiento o identidad.
- Todo I/O es `async` y acepta `CancellationToken`. Nunca `async void`, nunca `.Result`
  ni `.Wait()`.
- `TimeProvider` inyectado para todo lo que consulte la hora. Nada de `DateTime.UtcNow`
  directo: rompe los tests del backoff y de los leases.
- Todas las fechas son UTC, `datetime2(3)`. Nunca `DateTime.Now`.
- `Result<T>` para fallos esperados. Excepciones solo para lo que de verdad es un bug.

### Nombres

- Estados y enums en inglés (coinciden con la base de datos): `Pending`, `Delivered`.
- Comentarios y mensajes al usuario en español.
- Los mensajes de error dicen qué pasó y cómo arreglarlo, sin disculpas.

---

## Reglas duras de seguridad

Estas no se negocian:

1. **El body entrante se lee como `byte[]` y no se deserializa antes de validar la
   firma.** Deserializar y reserializar rompe HMAC. Si ves `[FromBody]` en un endpoint
   de `/in/*`, está mal.
2. **Ningún secreto se registra jamás.** Ni en logs, ni en trazas, ni en mensajes de
   error. Tokens, claves y firmas se enmascaran siempre.
3. **`AuthConfigJson` se cifra entero** con AES-GCM antes de tocar la base de datos, con
   `KeyVersion` para rotación.
4. **La API nunca devuelve secretos.** `GET` responde con los campos no sensibles y
   `"secretSet": true`. En `PUT`, la ausencia del campo secreto significa conservar el
   actual; solo un valor nuevo lo reemplaza.
5. **HMAC valida tolerancia de timestamp.** Sin ella una firma capturada se reproduce
   indefinidamente.
6. **Si SQL no responde, se devuelve `503` con `Retry-After`.** Nunca `2xx` sin haber
   persistido. Mejor rechazar que aceptar y perder.

---

## Reglas del despachador

1. El claim es atómico con lease y `READPAST`. Durante un despliegue hay **dos
   instancias vivas**; sin claim atómico eso son entregas duplicadas.
2. El claim reparte por destino (`ROW_NUMBER() PARTITION BY OutboundEndpointId`). Un
   backlog de un destino no puede dejar sin servicio a los demás.
3. Toda entrega tiene `ExpiresAt`. Al vencer pasa a `Expired` y deja de reintentarse.
4. `4xx` no se reintenta, salvo `408` y `429`. `5xx`, timeout y errores de red sí.
5. El backoff lleva jitter aleatorio. Sin él, un pico de fallos genera un pico de
   reintentos sincronizados.
6. Los `DeliveryAttempt` se escriben en batch, nunca uno por uno.
7. El apagado es ordenado: dejar de reclamar, terminar lo que está en vuelo, volcar el
   batch, liberar leases.
8. Reprogramar **no** consume intento. Si el circuito está abierto o el limitador de ritmo
   no da turno, no hemos enviado nada: subir `AttemptCount` ahí gastaría la ventana de
   entrega en intentos que nunca ocurrieron.
9. El cortacircuitos solo cuenta fallos **transitorios**. Un `400` significa que el destino
   está vivo y que el problema es ese mensaje; abrir el circuito por eso pararía las
   entregas buenas de todos los demás.
10. No se siguen redirecciones. Un destino mal configurado no se arregla siguiéndolas, y
    hacerlo puede acabar mandando las credenciales a otro host.

---

## Frontend

Vite + React + TypeScript, TanStack Query para estado de servidor, shadcn/ui sobre
Tailwind. Sitio estático en Render.

- Tipos generados desde el OpenAPI del backend. No se escriben a mano ni se duplican.
- Un archivo por componente, ninguno por encima de 200 líneas.
- Sin estado global salvo la sesión. TanStack Query es la caché.

El explorador de mensajes es la mitad del valor del producto. Merece más cuidado que su
tamaño sugiere.

---

## Tests

- **Unitarios** para lo que tiene lógica: escalera de backoff, clasificación de
  respuestas, máquina de estados del breaker, token bucket, validadores de firma.
- **Integración con Testcontainers** para lo que toca SQL: el claim bajo concurrencia,
  la recuperación de leases huérfanos, la deduplicación, el `SWITCH PARTITION`.
- No se testean getters, mapeos triviales ni configuración de EF.
- Un test de concurrencia del claim con N workers simultáneos es obligatorio. Es el
  punto donde un bug se convierte en entregas duplicadas en producción.

---

## Comandos

```bash
dotnet build                       # warnings son errores: si compila, el estilo está bien
dotnet test
dotnet run --project src/WebhookGateway.Api

docker compose up -d               # SQL Server local
cd frontend && npm run dev
```

---

## Antes de dar algo por terminado

1. `dotnet build` sin warnings.
2. `dotnet test` en verde.
3. Ningún archivo nuevo por encima de 200 líneas.
4. Ningún secreto en logs.
5. Ninguna abstracción nueva con una sola implementación.
