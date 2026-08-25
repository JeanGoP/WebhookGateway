# Estado de F4 — traspaso

Documento de continuidad. Está escrito para que alguien que llega sin ningún contexto
previo pueda seguir el trabajo leyendo solo `CLAUDE.md` y este archivo.

Última actualización: F4 prácticamente terminada. Backend de preparación **integrado y
compilando**; frontend **construido y funcional**. El frente abierto ya no es el panel,
sino los tests de integración y el endurecimiento para desplegar.

> Nota sobre verificación: los binarios de `bin/` son más recientes que el último cambio
> de código, así que la solución **compila**. Lo que **no** se ha verificado en esta
> revisión es `dotnet test` en verde ni el `npm run build` del frontend: conviene correr
> ambos antes de dar nada por cerrado.

---

## Dónde está el proyecto

| Fase | Estado |
|---|---|
| F0–F3 (recepción, persistencia, despacho, panel backend) | Terminado y compilando |
| F4 backend (OpenAPI, DTOs, CORS, endpoints del explorador) | Integrado y compilando |
| F4 frontend (panel React) | Construido: auth, integraciones, endpoints, suscripciones y explorador de mensajes |
| Tests de integración | **Sin escribir** (solo existe el `.csproj`) |
| Endurecimiento y despliegue | **Sin empezar** |

La API corre en `https://localhost:7004` con JWT. El certificado es autofirmado: `curl -k`.
El panel corre en `http://localhost:5173` (`cd frontend && npm run dev`).

---

## Lo que ya está hecho en F4

### Backend — todo integrado, ya no está "sin compilar"

Los cuatro cambios que preparaban F4 están en disco **y compilados**:

1. **OpenAPI declara respuestas.** Los objetos anónimos de `Panel/*` y `Auth/*` son ahora
   `record` nombrados, cada grupo en su DTO, y cada endpoint declara `.Produces<T>(status)`.
   Los errores tienen forma única `ErrorResponse` (`{ "error": "..." }`). La lógica de
   "aplicar un PUT" vive en métodos `ApplyTo`/`*Patch` dentro de los DTO para no pasar de
   200 líneas.
2. **Enums como texto en el borde HTTP.** `Program.cs` registra `JsonStringEnumConverter`.
   `MessageStatus`/`DeliveryStatus` se exponen como enum, no como `byte`. En lectura se
   siguen aceptando los valores numéricos.
3. **CORS**, política `panel`, orígenes desde `Gateway:Cors:AllowedOrigins` (hoy los dos
   de localhost). Sin `AllowCredentials` (la sesión va en `Authorization`). `UseCors` va
   antes de `UseAuthentication`. Se descartó el proxy de Vite: el frontend llama directo a
   `https://localhost:7004`.
4. **Dos endpoints del explorador:** `GET /api/messages/{id}/body` (seek sobre
   `(ReceivedAt, MessageId)`, corta a 256 KB, 404 si el cuerpo ya se purgó) y
   `POST /api/deliveries/{id}/retry` (reenvío manual: crea entrega **nueva**,
   `CreatedAt = ReceivedAt`, `ExpiresAt` desde ahora, 409 si sigue en curso / cuerpo
   purgado / destino desactivado). Requiere `db/06-delivery-by-id.sql`.

### La decisión abierta de la config de auth: **resuelta**

El bug de F1–F3 —`InboundAuthConfig`/`OutboundAuthConfig` eran records abstractos que
`System.Text.Json` no sabía deserializar— **ya está arreglado** con el enfoque que este
documento proponía. `InboundEndpointEndpoints` y `OutboundEndpointEndpoints` reciben
`authConfig` como `JsonElement?` y lo cifran vía `AuthConfigCodec.Encode(json, authType)`;
el `AuthType` del endpoint dice a qué tipo deserializar, sin discriminadores dentro del
JSON. Las pantallas de endpoints de entrada y salida ya no están bloqueadas.

### Frontend — construido

`frontend/`: Vite + React 19 + TypeScript, TanStack Query, shadcn/ui sobre Tailwind,
`react-router` 7. Estructura respetando "un archivo por componente, ninguno > 200 líneas".

- **Auth completo:** cliente HTTP con refresh (`api/client.ts`), contexto de sesión,
  rutas protegidas, pantallas de instalación (`/setup`) y login (`/login`).
- **CRUD de integraciones** y su detalle (`pages/integration-detail.tsx`).
- **Endpoints de entrada y salida** con formularios de campos de auth por tipo
  (`components/integrations/*-auth-fields.tsx`, `*-form.tsx`, `*-table.tsx`).
- **Suscripciones** (`subscription-panel.tsx`).
- **Explorador de mensajes** —la mitad del valor del producto—: tabla, filtros, detalle,
  cuerpo, entregas, intentos y **reenvío manual cableado** (botón "Reintentar", habilitado
  solo para entregas `Failed`/`Expired`, en `components/messages/delivery-detail.tsx`).

Navegación (`layout/app-shell.tsx`): Integraciones (índice) y Mensajes.

---

## Lo que falta, en orden de prioridad

1. **Verificar la base compilada:** `dotnet build` (debe seguir sin warnings) y
   `dotnet test`. Luego `cd frontend && npm run build` (`tsc -b && vite build`).
2. **Tests de integración — la brecha dura.** `WebhookGateway.IntegrationTests` solo tiene
   el `.csproj` (con `Testcontainers.MsSql` y `Mvc.Testing` referenciados); **no hay ni un
   archivo de test**. Falta el que `CLAUDE.md` marca como *obligatorio*: el **claim con N
   workers concurrentes**, el punto donde un bug se vuelve entregas duplicadas en
   producción. También faltan: recuperación de leases huérfanos, deduplicación y purga por
   particiones.
3. **Generar los tipos del frontend desde OpenAPI.** Hoy `src/api/types.ts` está escrito
   **a mano** (lo dice su propio encabezado), lo que contradice la regla de `CLAUDE.md`. El
   script `npm run gen:api` ya existe pero no se ha corrido: no hay `schema.d.ts`. Hay que
   generarlo contra el JSON de OpenAPI del backend y migrar los imports.
4. **Sacar los secretos de `appsettings.json` a variables de entorno** (ver deudas abajo).
5. **Despliegue (F5):** `Dockerfile` de la API sobre el runtime `aspnet` normal (no
   `-alpine` ni `-chiseled`, por ICU); sitio estático del panel en Render; poner la URL
   real del panel en `Gateway:Cors:AllowedOrigins`. Hoy `docker-compose.yml` solo levanta
   SQL Server local.
6. **Limpieza:** borrar `src/WebhookGateway.Dispatcher/Class1.cs` (plantilla vacía de 72 B).

---

## Decisiones abiertas

### El mapeo de `Panel/MessageDto.cs`

`MessageExplorer` (en `Data`) devuelve `MessageSummary`, `MessageDetail`, `DeliverySummary`
y `AttemptDetail`. `Panel/MessageDto.cs` las mapea a DTOs del borde HTTP para exponer los
estados como enum en vez de `byte`. Roza la regla que prohíbe "DTO por capa". El argumento
a favor: las clases de `Data` son la forma de la fila de Dapper, no un DTO, así que solo hay
una forma en el borde HTTP. Si se decide que es ceremonia, se revierte devolviendo las
clases de `Data` y `status` vuelve a viajar como número.

---

## Deudas conocidas

- **Secretos en claro y versionados.** `appsettings.json` lleva la contraseña de SQL de
  producción, la clave AES de cifrado y la clave JWT en texto plano. El propio archivo
  explica cómo pasarlas a variables de entorno (`Gateway__Sql__ConnectionString`,
  `Gateway__Secrets__Keys__1`, etc.). Si esto ya está en un repositorio, esas credenciales
  **deben rotarse**, no solo moverse.
- El test de concurrencia del claim (punto 2 de arriba) sigue pendiente; hasta que exista,
  la garantía de "sin entregas duplicadas durante un despliegue con dos instancias vivas"
  no está cubierta por pruebas.

---

## Verificación manual

```bash
TOKEN=$(curl -sk -X POST https://localhost:7004/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"...","password":"..."}' | jq -r .accessToken)

curl -sk https://localhost:7004/api/messages/1/body -H "Authorization: Bearer $TOKEN" | jq

curl -sk -X POST https://localhost:7004/api/deliveries/1/retry -H "Authorization: Bearer $TOKEN" -i

curl -sk -X OPTIONS https://localhost:7004/api/integrations \
  -H 'Origin: http://localhost:5173' \
  -H 'Access-Control-Request-Method: GET' -i | head -20
```

El cierre de sesión es `POST /api/auth/logout` (recibe `{ refreshToken }`, devuelve 204).
No existe `/api/auth/revoke`.
