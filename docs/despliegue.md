# Despliegue en Render (solo backend)

Un servicio: la API (.NET en Docker, con el despachador dentro) como servicio web. La base
de datos es externa: el gateway apunta a un SQL Server tuyo por la cadena de conexión, Render
no la gestiona. **El panel (frontend) no se despliega aquí** y está excluido del repositorio
(`frontend/` en `.gitignore`); cuando quieras publicarlo, se hace aparte.

Los archivos que gobiernan esto son `Dockerfile`, `.dockerignore` y `render.yaml`, en la raíz
del repositorio.

---

## Antes de tocar Render

**1. Los secretos NO van en el repositorio.** Ya están fuera de `appsettings.json` (los tres
campos van vacíos) y se cargan por otro lado:

| Variable en Render | Local (user-secrets) | Qué es |
|---|---|---|
| `Gateway__Sql__ConnectionString` | `Gateway:Sql:ConnectionString` | Cadena de conexión a tu SQL Server |
| `Gateway__Secrets__Keys__1` | `Gateway:Secrets:Keys:1` | Clave AES de cifrado (32 bytes base64) |
| `Gateway__Jwt__Key` | `Gateway:Jwt:Key` | Clave de firma de los JWT |

> El doble guion bajo es cómo .NET mapea `Gateway:Sql:ConnectionString` a una variable de
> entorno. No es un error de tipografía.

**2. Tu SQL Server tiene que aceptar conexiones desde Render.** El gateway apunta a un host
externo (`sintesiserpcloud.webhop.org`). Render sale por un rango de IPs; ese host y su
firewall deben permitir la conexión entrante al puerto de SQL. Si solo escucha en la red
local, Render no llegará.

**3. El esquema tiene que estar aplicado en esa base.** Corre los scripts de `db/` una vez
contra la base de destino, incluido `06-delivery-by-id.sql`. Ver `db/README.md`.

---

## Orden de despliegue

1. **Empuja el repo a GitHub** (con los secretos ya fuera de `appsettings.json` y `frontend/`
   ignorado).
2. En Render, **New → Blueprint**, y apunta al repositorio. Render lee `render.yaml` y propone
   el servicio `webhookgateway-api`.
3. **Rellena los tres secretos** de la tabla de arriba en el panel de Render. Deja
   `Gateway__Cors__AllowedOrigins__0` sin definir: mientras no haya panel, la API no necesita
   aceptar navegadores.
4. **Deploy.** Render construye la imagen Docker (aquí se verifica, por fin, que la imagen
   compila y arranca sobre el runtime `aspnet` con ICU, como exige `CLAUDE.md`).
5. **Comprueba** que responde:
   - `https://<api>/health/live` → 200 (proceso vivo).
   - `https://<api>/health/ready` → 200 si la API alcanza el SQL; si da 503, es la conexión a
     la base (revisa cadena, firewall, esquema).
   - `https://<api>/` → `{ "service": "WebhookGateway", "status": "ok" }`.

---

## Notas

- **El plan es `starter`, no `free`.** El despachador tiene que estar siempre vivo para drenar
  la cola; el plan gratuito se suspende por inactividad y dejaría entregas sin enviar.
- **Región.** El blueprint usa `oregon`. Cada entrega y cada consulta es un viaje de ida y
  vuelta a tu SQL Server; elige la región de Render más cercana a ese servidor.
- **Los tests de integración no corren en el build de Render.** Necesitan un Docker donde
  levantar SQL Server (Testcontainers), que el build de Render no ofrece. Su sitio es un CI
  con Docker o una máquina con Docker. En Render se verifica que la imagen se construye y
  arranca.
- **El panel, más adelante.** Cuando lo despliegues (sitio estático en Render u otro sitio),
  hay que: construirlo con `VITE_API_URL` apuntando a la URL de esta API, y añadir su URL a
  `Gateway__Cors__AllowedOrigins__0` aquí para que el navegador pueda llamar a `/api/*`.
