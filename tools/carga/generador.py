"""
Generador de carga "crudo": manda TOTAL webhooks a la recepción del gateway tan rápido como dan
HILOS conexiones keep-alive, y apunta códigos y latencias. Todo contra localhost.

A los CAIDA_EN segundos tumba el destino falso durante CAIDA_DURA segundos, para ver el
cortacircuitos, los reintentos y la recuperación con el backlog lleno (CAIDA=0 la desactiva).

Si aparece un archivo ABORTAR junto al script, deja de enviar: es la salida de emergencia del
guardián cuando la prueba va contra un servidor compartido.

Progreso cada 5 s en progreso-generador.json; resumen final en resumen-generador.json.
"""
import http.client
import json
import os
import random
import statistics
import threading
import time
import urllib.request
import uuid

TOTAL = int(os.environ.get("TOTAL", 300_000))
HILOS = int(os.environ.get("HILOS", 48))
# Ritmo fijo en webhooks por segundo; 0 es "tan rápido como se pueda".
RITMO = float(os.environ.get("RITMO", 0))
# Sin barra inicial también vale: Git Bash convierte "/in/..." en una ruta de Windows al pasarla.
RUTA = "/" + os.environ.get("RUTA", "in/ghl/leads").lstrip("/")
CAIDA = os.environ.get("CAIDA", "1") != "0"
CAIDA_EN, CAIDA_DURA = 360, 120
# Rampa: los primeros RAMPA_SEGUNDOS a RAMPA_RITMO/s, y después a RITMO.
RAMPA_RITMO = float(os.environ.get("RAMPA_RITMO", 0))
RAMPA_SEGUNDOS = float(os.environ.get("RAMPA_SEGUNDOS", 0))

HERE = os.path.dirname(os.path.abspath(__file__))
ABORTAR = os.path.join(HERE, "ABORTAR")
abortado = {"si": False}
lock = threading.Lock()
counter = {"enviadas": 0}
codes = {}
latencies_all = []
latencies_window = []
inicio = time.time()

NOMBRES = ["Ana", "Luis", "Carlos", "María", "Sofía", "Andrés", "Valentina", "Juan", "Camila", "Diego"]


def body(i):
    # Forma parecida a un webhook de oportunidad de GHL: ~1 KB, así pasa por el gzip.
    return json.dumps({
        "type": "OpportunityCreate",
        "webhookId": str(uuid.uuid4()),
        "locationId": "0B26qBYbUr8qApdVoz3t",
        "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "id": f"opp-{i}",
        "name": f"Lead {i}",
        "pipelineId": "pl-motos", "pipelineStageId": "st-nuevo", "status": "open",
        "monetaryValue": random.randint(1, 50) * 100000,
        "assignedTo": None,
        "attributionSource": {"medium": random.choice(["facebook", "google", "organic", "whatsapp"]),
                              "campaign": "campana-octubre", "utmSource": "fb", "utmMedium": "cpc"},
        "contact": {"id": f"ct-{i}", "firstName": random.choice(NOMBRES), "lastName": "Pruebas",
                    "email": f"lead{i}@ejemplo.invalid", "phone": f"+5730{i:08d}",
                    "tags": ["carga", "octubre"], "customFields": [{"id": "tipo_servicio", "value": "moto"}]},
        "notes": "x" * 300,
    }).encode()


def momento(i):
    """Cuándo toca enviar el webhook i, con la rampa inicial si la hay."""
    en_rampa = RAMPA_RITMO * RAMPA_SEGUNDOS
    if RAMPA_RITMO > 0 and i <= en_rampa:
        return inicio + (i - 1) / RAMPA_RITMO
    if RITMO > 0:
        return inicio + (RAMPA_SEGUNDOS if RAMPA_RITMO > 0 else 0) + (i - 1 - en_rampa) / RITMO
    return None


def worker():
    conn = http.client.HTTPConnection("127.0.0.1", 5092, timeout=30)
    while True:
        with lock:
            if counter["enviadas"] >= TOTAL or abortado["si"]:
                break
            counter["enviadas"] += 1
            i = counter["enviadas"]
        payload = body(i)
        cuando = momento(i)
        if cuando is not None and cuando > time.time():
            time.sleep(cuando - time.time())
        t0 = time.perf_counter()
        try:
            conn.request("POST", RUTA, payload, {"Content-Type": "application/json"})
            resp = conn.getresponse()
            resp.read()
            code = resp.status
        except Exception as ex:  # conexión rota: se apunta y se reabre
            code = f"error:{type(ex).__name__}"
            conn.close()
            conn = http.client.HTTPConnection("127.0.0.1", 5092, timeout=30)
        ms = (time.perf_counter() - t0) * 1000
        with lock:
            codes[str(code)] = codes.get(str(code), 0) + 1
            latencies_all.append(ms)
            latencies_window.append(ms)
    conn.close()


def pct(values, p):
    if not values:
        return None
    values = sorted(values)
    return round(values[min(len(values) - 1, int(len(values) * p))], 1)


def control(modo):
    try:
        urllib.request.urlopen(f"http://127.0.0.1:5999/_control?modo={modo}", timeout=5).read()
        return True
    except Exception:
        return False


def monitor(threads):
    last_count, last_t = 0, time.time()
    caida_hecha = levantado = False
    while any(t.is_alive() for t in threads):
        time.sleep(5)
        elapsed = time.time() - inicio
        if os.path.exists(ABORTAR):
            abortado["si"] = True
        if CAIDA and not caida_hecha and elapsed >= CAIDA_EN:
            caida_hecha = control("caido")
        if caida_hecha and not levantado and elapsed >= CAIDA_EN + CAIDA_DURA:
            levantado = control("arriba")
        with lock:
            done = sum(codes.values())
            window = latencies_window[:]
            latencies_window.clear()
            snap_codes = dict(codes)
        now = time.time()
        progress = {
            "segundos": round(elapsed), "respondidas": done, "total": TOTAL,
            "ritmo_por_s": round((done - last_count) / (now - last_t), 1),
            "p50_ms": pct(window, 0.50), "p95_ms": pct(window, 0.95), "p99_ms": pct(window, 0.99),
            "codigos": snap_codes,
            "destino": "caido" if caida_hecha and not levantado else "arriba",
            "abortado": abortado["si"],
        }
        last_count, last_t = done, now
        with open(os.path.join(HERE, "progreso-generador.json"), "w", encoding="utf-8") as f:
            json.dump(progress, f)
    if caida_hecha and not levantado:
        control("arriba")


if __name__ == "__main__":
    threads = [threading.Thread(target=worker, daemon=True) for _ in range(HILOS)]
    for t in threads:
        t.start()
    monitor(threads)
    for t in threads:
        t.join()
    total_s = time.time() - inicio
    resumen = {
        "total": TOTAL, "enviadas": sum(codes.values()), "abortado": abortado["si"],
        "hilos": HILOS, "ritmo_objetivo": RITMO, "ruta": RUTA, "segundos": round(total_s), "ritmo_medio_por_s": round(sum(codes.values()) / total_s, 1),
        "codigos": codes,
        "p50_ms": pct(latencies_all, 0.50), "p95_ms": pct(latencies_all, 0.95),
        "p99_ms": pct(latencies_all, 0.99), "max_ms": round(max(latencies_all), 1),
        "media_ms": round(statistics.fmean(latencies_all), 1),
    }
    with open(os.path.join(HERE, "resumen-generador.json"), "w", encoding="utf-8") as f:
        json.dump(resumen, f, indent=2)
    print(json.dumps(resumen, indent=2), flush=True)
