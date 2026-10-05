# Prueba de carga

Lo que se usó el 2026-10-05 para la fase 6 del plan (`docs/plan-escala-400k.md`, §7.6). Todo
corre **en este equipo**: la API, LocalDB, el destino falso y el generador. Nada sale a la red.

> **Nunca contra el servidor compartido.** `preparar.ps1` vacía las tablas de tráfico y se niega
> a correr si la base no es `WebhookGateway_dev` en LocalDB. Una prueba contra `_dev` del servidor
> real es otra cosa y necesita sus propias condiciones (§5 del plan).

| Archivo | Qué hace |
|---|---|
| `preparar.ps1` | Deja la base local en cero (tráfico) y configura el endpoint `ghl/leads` como GHL y su destino apuntando al destino falso |
| `destino_falso.py` | Destino en `127.0.0.1:5999`. Latencia configurable, se puede tumbar, cuenta `webhookId` únicos para detectar duplicados y mide cuántas peticiones tiene en curso |
| `generador.py` | Manda `TOTAL` webhooks con forma de GHL a `RITMO`/s (0 = lo más rápido posible). Tumba el destino del minuto 6 al 8 |
| `curva.ps1` | Una línea cada 30 s en `curva.log`: recepción, cola, entregas, retraso. Para sola cuando la cola queda vacía |
| `vigilar.ps1` | Foto puntual de esperas y bloqueos en SQL durante la carga |
| `verificar.ps1` | Las cuentas finales: entregas por estado, duplicados, escalados de bloqueo |

## Cómo se corre

1. Base local: `WebhookGateway_dev` en `(localdb)\MSSQLLocalDB`, creada con `db/setup-dev-db.sql`.
2. `powershell -File tools/carga/preparar.ps1`
3. `python tools/carga/destino_falso.py` y fijar la latencia:
   `http://127.0.0.1:5999/_control?latencia=1.0` (también `?modo=caido` / `?modo=arriba`).
4. La API contra LocalDB, con el despachador encendido y sin log por petición:

   ```
   dotnet run --project src/WebhookGateway.Api --launch-profile http -- ^
     "--Gateway:Sql:ConnectionString=Server=(localdb)\MSSQLLocalDB;Database=WebhookGateway_dev;Integrated Security=True;TrustServerCertificate=True" ^
     --Gateway:Dispatcher:Enabled=true --Gateway:Reception:TransientFailureStatusCode=429 ^
     --Logging:LogLevel:Default=Warning --Logging:LogLevel:WebhookGateway=Information
   ```

5. `powershell -File tools/carga/curva.ps1` en otra consola.
6. `TOTAL=63000 RITMO=70 HILOS=24 python tools/carga/generador.py` (en PowerShell, con `$env:TOTAL` etc.).
7. Al acabar, `powershell -File tools/carga/verificar.ps1`.

El panel (`frontend/`, con `VITE_API_URL=http://localhost:5092` en `.env.local`) muestra lo mismo en
el monitor en vivo. Con cientos de miles de pendientes, refresco a 5 s o más: cada sondeo cuenta la cola.
