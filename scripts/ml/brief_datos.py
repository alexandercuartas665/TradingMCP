"""Descarga velas de 1 minuto bid y ask de Dukascopy y las agrega a M15 (experimento del brief).

Plan: docs/validacion/brief_modelo_filtro_plan.md. Un archivo por activo y ano en D:\\traiding_ml\\brief:
<SIMBOLO>_<ANO>.csv.gz con epoch, bid o/h/l/c, ask o/h/l/c y volumen (minutos con volumen).
Lo ya bajado no se repite: un ano solo se escribe si todos sus dias respondieron.

Dukascopy limita las peticiones (429): por defecto 1 hilo, pausa entre archivos y esperas largas.

Uso (entorno de D:):
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/brief_datos.py spread     (enmienda 1: spread de junio por ano)
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/brief_datos.py minutos [--hilos 1]
"""
import argparse
import concurrent.futures as cf
import datetime as dt
import json
import lzma
import os
import struct
import sys
import time
import urllib.error
import urllib.request

import numpy as np
import pandas as pd

CARPETA = r'D:\traiding_ml\brief'
ACTIVOS = {'XAUUSD': 2003, 'USDJPY': 2003, 'USATECHIDXUSD': 2011}
HASTA = dt.date(2026, 11, 1)          # exclusivo (plan: hasta 2026-10-31)
DIVISOR = 1000.0                      # oro, pares con JPY e indices
URL = 'https://datafeed.dukascopy.com/datafeed/%s/%04d/%02d/%02d/%s_candles_min_1.bi5'
URL_H = 'https://datafeed.dukascopy.com/datafeed/%s/%04d/%02d/%s_candles_hour_1.bi5'
SIMBOLO_BD = {'XAUUSD': 'frxXAUUSD', 'USDJPY': 'frxUSDJPY', 'USATECHIDXUSD': 'OTC_NDX'}
PAUSA = 1.5


def log(msg):
    print(time.strftime('%H:%M:%S'), msg, flush=True)


def bajar(url, intentos=12):
    """Bytes del archivo (b'' si no existe). Ante 429 espera 1, 2, 4... hasta 15 minutos."""
    for k in range(intentos):
        try:
            req = urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0'})
            with urllib.request.urlopen(req, timeout=60) as r:
                datos = r.read()
            time.sleep(PAUSA)
            return datos
        except urllib.error.HTTPError as e:
            if e.code == 404:
                time.sleep(PAUSA)
                return b''
            espera = min(900, 60 * 2 ** k) if e.code == 429 else min(60, 5 * 2 ** k)
        except OSError:
            espera = min(60, 5 * 2 ** k)
        time.sleep(espera)
    raise RuntimeError('sin respuesta: ' + url)


def minutos(datos, epoch0):
    """Matriz [epoch, o, h, l, c, vol] de los minutos con volumen."""
    if not datos or len(datos) < 13:
        return np.empty((0, 6))
    crudo = lzma.decompress(datos, format=lzma.FORMAT_ALONE)
    n = len(crudo) // 24
    if n == 0:
        return np.empty((0, 6))
    a = np.frombuffer(crudo[:n * 24], dtype='>i4').reshape(n, 6)
    vol = np.frombuffer(crudo[:n * 24], dtype='>f4').reshape(n, 6)[:, 5]
    s, o, c, l, h = (a[:, i].astype(np.int64) for i in range(5))
    ok = (vol > 0) & (l > 0) & (h >= l) & (o >= l) & (o <= h) & (c >= l) & (c <= h)
    return np.column_stack([epoch0 + s[ok], o[ok] / DIVISOR, h[ok] / DIVISOR, l[ok] / DIVISOR, c[ok] / DIVISOR, vol[ok]])


def dia(sym, d):
    epoch0 = int(dt.datetime(d.year, d.month, d.day, tzinfo=dt.timezone.utc).timestamp())
    res = {}
    for lado in ('BID', 'ASK'):
        res[lado] = minutos(bajar(URL % (sym, d.year, d.month - 1, d.day, lado)), epoch0)
    return res


def a_m15(m, pref):
    if len(m) == 0:
        return pd.DataFrame(columns=['epoch', pref + 'o', pref + 'h', pref + 'l', pref + 'c', 'vol'])
    df = pd.DataFrame(m, columns=['epoch', 'o', 'h', 'l', 'c', 'vol'])
    df['epoch'] = (df['epoch'].astype(np.int64) // 900) * 900
    g = df.groupby('epoch').agg(o=('o', 'first'), h=('h', 'max'), l=('l', 'min'), c=('c', 'last'), vol=('vol', 'sum'))
    return g.rename(columns={k: pref + k for k in 'ohlc'}).reset_index()


def anio(sym, a, hilos):
    ruta = os.path.join(CARPETA, '%s_%d.csv.gz' % (sym, a))
    if os.path.exists(ruta):
        return 'ya estaba'
    dias = [dt.date(a, 1, 1) + dt.timedelta(days=i) for i in range(366)]
    dias = [d for d in dias if d.year == a and d < HASTA and d.weekday() != 5]
    if not dias:
        return 'sin dias'
    bid, ask = [], []
    with cf.ThreadPoolExecutor(hilos) as ex:
        for r in ex.map(lambda d: dia(sym, d), dias):
            bid.append(r['BID'])
            ask.append(r['ASK'])
    b = a_m15(np.vstack(bid), 'b')
    k = a_m15(np.vstack(ask), 'a').drop(columns='vol')
    df = b.merge(k, on='epoch', how='inner').sort_values('epoch')
    df = df[['epoch', 'bo', 'bh', 'bl', 'bc', 'ao', 'ah', 'al', 'ac', 'vol']]
    if len(df) == 0:
        return 'sin datos'
    df.to_csv(ruta + '.tmp', index=False, float_format='%.6g', compression='gzip')
    os.replace(ruta + '.tmp', ruta)
    return '%d velas M15' % len(df)


def spread_junio():
    """Enmienda 1: spread fijo por activo y ano = mediana de (cierre ask - cierre bid) de las velas H1 de
    junio (o del primer mes con datos de ese ano), ask del archivo horario de Dukascopy, bid de la base."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import velas_bd
    ruta = os.path.join(CARPETA, 'spread_h1.json')
    res = json.load(open(ruta)) if os.path.exists(ruta) else {}
    for sym, desde in ACTIVOS.items():
        bid = velas_bd.velas('Dukascopy', SIMBOLO_BD[sym], '1h').set_index('epoch')['c']
        for an in range(desde, HASTA.year + 1):
            clave = '%s_%d' % (sym, an)
            if clave in res:
                continue
            for mes in [6, 7, 8, 9, 10, 11, 12, 1, 2, 3, 4, 5]:
                if dt.date(an, mes, 1) >= HASTA:
                    continue
                datos = bajar(URL_H % (sym, an, mes - 1, 'ASK'))
                ask = minutos(datos, int(dt.datetime(an, mes, 1, tzinfo=dt.timezone.utc).timestamp()))
                if len(ask) == 0:
                    continue
                a = pd.Series(ask[:, 4], index=ask[:, 0].astype(np.int64))
                comun = a.index.intersection(bid.index)
                if len(comun) < 50:
                    continue
                sp = float(np.median(a[comun].to_numpy() - bid[comun].to_numpy()))
                res[clave] = {'mes': mes, 'horas': int(len(comun)), 'spread': round(sp, 6)}
                log('%s %d: spread %.5f (mes %d, %d horas)' % (sym, an, sp, mes, len(comun)))
                json.dump(res, open(ruta, 'w'), indent=1)
                break
    log('spread listo: %d activos-ano' % len(res))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('accion', choices=['spread', 'minutos'])
    ap.add_argument('--hilos', type=int, default=1)
    a = ap.parse_args()
    os.makedirs(CARPETA, exist_ok=True)
    if a.accion == 'spread':
        return spread_junio()
    for sym, desde in ACTIVOS.items():
        for an in range(desde, HASTA.year + 1):
            t0 = time.time()
            try:
                r = anio(sym, an, a.hilos)
            except Exception as e:  # se reintenta al volver a correr
                r = 'ERROR %s' % e
            log('%s %d: %s (%.0f s)' % (sym, an, r, time.time() - t0))
    log('listo')


if __name__ == '__main__':
    sys.exit(main())
