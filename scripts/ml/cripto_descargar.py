"""Descarga velas de 1h de Binance spot desde los archivos publicos (data.binance.vision).

Guarda los zip en <carpeta>/cripto/binance_zip/<SIMBOLO>/ y un CSV unido por simbolo en
<carpeta>/cripto/datos/Binance_<SIMBOLO>_1h.csv con columnas:
  epoch,o,h,l,c,volumen,volumen_quote,operaciones,compra_agresiva
(epoch en segundos, apertura de la vela). Desde 2025 Binance publica los tiempos en
microsegundos: se normaliza. Los archivos descargados solo se leen como datos (CSV).

Uso: D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/cripto_descargar.py D:/traiding_ml
     ... D:/traiding_ml --deriv   (velas 1h de cryBTCUSD y cryETHUSD de Deriv por la app, API 8766)
"""
import json
import csv
import datetime as dt
import io
import os
import sys
import time
import urllib.error
import urllib.request
import zipfile

SIMBOLOS = ['BTCUSDT', 'ETHUSDT', 'BNBUSDT', 'LTCUSDT', 'XRPUSDT', 'ADAUSDT', 'LINKUSDT', 'DOGEUSDT', 'SOLUSDT']
BASE = 'https://data.binance.vision/data/spot'


def bajar(url, destino):
    if os.path.exists(destino) and os.path.getsize(destino) > 0:
        return True
    for intento in range(3):
        try:
            req = urllib.request.Request(url, headers={'User-Agent': 'traiding-research/1.0'})
            with urllib.request.urlopen(req, timeout=60) as r:
                datos = r.read()
            open(destino, 'wb').write(datos)
            return True
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return False
            time.sleep(2 + intento * 3)
        except Exception:
            time.sleep(2 + intento * 3)
    return False


def filas_zip(ruta):
    with zipfile.ZipFile(ruta) as z:
        for nombre in z.namelist():
            if not nombre.endswith('.csv'):
                continue
            for f in csv.reader(io.TextIOWrapper(z.open(nombre), encoding='utf-8')):
                if not f or not f[0].isdigit():
                    continue
                t = int(f[0])
                epoch = t // 1000000 if t > 10 ** 14 else t // 1000
                yield (epoch, f[1], f[2], f[3], f[4], f[5], f[7], f[8], f[9])


def main(carpeta):
    hoy = dt.date.today()
    raiz = os.path.join(carpeta, 'cripto')
    os.makedirs(os.path.join(raiz, 'datos'), exist_ok=True)
    for sym in SIMBOLOS:
        dz = os.path.join(raiz, 'binance_zip', sym)
        os.makedirs(dz, exist_ok=True)
        rutas = []
        a, m = 2017, 8
        while (a, m) < (hoy.year, hoy.month):
            n = '%s-1h-%04d-%02d.zip' % (sym, a, m)
            if bajar('%s/monthly/klines/%s/1h/%s' % (BASE, sym, n), os.path.join(dz, n)):
                rutas.append(os.path.join(dz, n))
            a, m = (a + 1, 1) if m == 12 else (a, m + 1)
        d = dt.date(hoy.year, hoy.month, 1)
        while d < hoy:
            n = '%s-1h-%s.zip' % (sym, d.isoformat())
            if bajar('%s/daily/klines/%s/1h/%s' % (BASE, sym, n), os.path.join(dz, n)):
                rutas.append(os.path.join(dz, n))
            d += dt.timedelta(days=1)
        vistas = {}
        for r in rutas:
            for fila in filas_zip(r):
                vistas[fila[0]] = fila
        salida = os.path.join(raiz, 'datos', 'Binance_%s_1h.csv' % sym)
        with open(salida, 'w', newline='', encoding='utf-8') as f:
            w = csv.writer(f)
            w.writerow(['epoch', 'o', 'h', 'l', 'c', 'volumen', 'volumen_quote', 'operaciones', 'compra_agresiva'])
            for k in sorted(vistas):
                w.writerow(vistas[k])
        ep = sorted(vistas)
        print('%s: %d archivos, %d velas, %s a %s' % (sym, len(rutas), len(ep),
              dt.datetime.utcfromtimestamp(ep[0]).date() if ep else '-', dt.datetime.utcfromtimestamp(ep[-1]) if ep else '-'), flush=True)


def deriv(carpeta):
    """Velas 1h de Deriv (cryBTCUSD, cryETHUSD) paginando hacia atras de 5000 en 5000."""
    tok = open(os.path.join(os.environ.get('LOCALAPPDATA', ''), 'WpfDerivClientVB', 'agent_token.txt'), encoding='utf-8').read().strip()

    def consulta(cuerpo):
        req = urllib.request.Request('http://localhost:8766/api/heavy/trading/consulta', data=json.dumps(cuerpo).encode(),
                                     headers={'X-Agent-Token': tok, 'Content-Type': 'application/json'}, method='POST')
        with urllib.request.urlopen(req, timeout=90) as r:
            return json.loads(r.read().decode())

    for sym in ('cryBTCUSD', 'cryETHUSD'):
        vistas, fin = {}, 'latest'
        while True:
            r = consulta({'ticks_history': sym, 'style': 'candles', 'granularity': 3600, 'count': 5000, 'end': fin})
            velas = r.get('candles') or []
            nuevas = [v for v in velas if v['epoch'] not in vistas]
            for v in velas:
                vistas[v['epoch']] = (v['epoch'], v['open'], v['high'], v['low'], v['close'])
            if len(nuevas) < 10:
                break
            fin = min(v['epoch'] for v in velas) - 1
            time.sleep(1)
        salida = os.path.join(carpeta, 'cripto', 'datos', 'Deriv_%s_1h.csv' % sym)
        with open(salida, 'w', newline='', encoding='utf-8') as f:
            w = csv.writer(f)
            w.writerow(['epoch', 'o', 'h', 'l', 'c'])
            for k in sorted(vistas):
                w.writerow(vistas[k])
        ep = sorted(vistas)
        print('%s: %d velas, %s a %s' % (sym, len(ep), dt.datetime.utcfromtimestamp(ep[0]), dt.datetime.utcfromtimestamp(ep[-1])), flush=True)


if __name__ == '__main__':
    if '--deriv' in sys.argv:
        deriv(sys.argv[1])
    else:
        main(sys.argv[1])
