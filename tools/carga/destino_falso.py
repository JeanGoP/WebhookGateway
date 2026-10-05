"""
Destino falso para la prueba de carga del gateway. Solo escucha en localhost.

- POST /hook        -> responde 200 tras `latencia` segundos (o 503 si está "caído").
                       Cuenta los webhookId únicos para detectar entregas duplicadas.
- GET  /_control?modo=caido|arriba&latencia=0.2  -> cambia el comportamiento en caliente.
- GET  /_stats      -> cifras en JSON.

Cada 5 s escribe las cifras en stats-destino.json, junto al script.
"""
import json
import os
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

HERE = os.path.dirname(os.path.abspath(__file__))
STATS_FILE = os.path.join(HERE, "stats-destino.json")

lock = threading.Lock()
state = {"modo": "arriba", "latencia": 0.2}
stats = {"en_curso": 0, "en_curso_max": 0, "recibidas": 0, "unicas": 0, "duplicadas": 0, "rechazadas_503": 0, "sin_id": 0, "inicio": time.time()}
seen = set()
window = []  # marcas de tiempo de las últimas entregas aceptadas, para el ritmo


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):  # silencio: a cientos por segundo, el log sería el cuello de botella
        pass

    def _send(self, code, body=b"", ctype="application/json"):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if body:
            self.wfile.write(body)

    def do_GET(self):
        url = urlparse(self.path)
        if url.path == "/_control":
            q = parse_qs(url.query)
            with lock:
                if "modo" in q:
                    state["modo"] = q["modo"][0]
                if "latencia" in q:
                    state["latencia"] = float(q["latencia"][0])
                body = json.dumps(state).encode()
            self._send(200, body)
        elif url.path == "/_stats":
            self._send(200, json.dumps(snapshot()).encode())
        else:
            self._send(404)

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        raw = self.rfile.read(length) if length else b""

        with lock:
            modo, latencia = state["modo"], state["latencia"]

        if modo == "caido":
            with lock:
                stats["rechazadas_503"] += 1
            self._send(503, b'{"error":"caido a proposito"}')
            return

        with lock:
            stats["en_curso"] += 1
            stats["en_curso_max"] = max(stats["en_curso_max"], stats["en_curso"])
        time.sleep(latencia)
        with lock:
            stats["en_curso"] -= 1

        try:
            webhook_id = json.loads(raw).get("webhookId")
        except (ValueError, AttributeError):
            webhook_id = None

        with lock:
            stats["recibidas"] += 1
            window.append(time.time())
            if webhook_id is None:
                stats["sin_id"] += 1
            elif webhook_id in seen:
                stats["duplicadas"] += 1
            else:
                seen.add(webhook_id)
                stats["unicas"] += 1

        self._send(200, b'{"ok":true}')


def snapshot():
    with lock:
        now = time.time()
        while window and window[0] < now - 10:
            window.pop(0)
        snap = dict(stats)
        snap.update(state)
        snap["ritmo_ultimos_10s"] = round(len(window) / 10, 1)
        snap["segundos"] = round(now - stats["inicio"])
        return snap


def writer():
    while True:
        time.sleep(5)
        with open(STATS_FILE, "w", encoding="utf-8") as f:
            json.dump(snapshot(), f)


if __name__ == "__main__":
    threading.Thread(target=writer, daemon=True).start()
    server = ThreadingHTTPServer(("127.0.0.1", 5999), Handler)
    server.daemon_threads = True
    print("Destino falso escuchando en http://127.0.0.1:5999", flush=True)
    server.serve_forever()
