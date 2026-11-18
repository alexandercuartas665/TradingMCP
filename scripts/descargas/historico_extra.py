"""Carga historico largo extra en la tabla candles (PostgreSQL de la app, contenedor traiding_postgres).

Desde 2026-11-18 la app hace lo mismo sola (API 8766 historico-externo con fuente Bitstamp, Binance o Yahoo,
selector "Fuente de velas" del simulador y mantenimiento nocturno). Este script queda para cargas sin la app.

Series (cada fuente con su propio valor de platform; nunca se mezclan con Deriv):
  dax     platform=Dukascopy  symbol=OTC_GDAXI  1h/4h/1d  (DEUIDXEUR; mismo formato que DukascopyDescarga.vb,
                                                          asi el mantenimiento nocturno de la app la sigue al dia)
  btc     platform=Bitstamp   symbol=cryBTCUSD  1h/4h/1d  (BTC/USD desde 2011; Bitcoin no existe antes de 2009)
          platform=Binance    symbol=cryBTCUSD  1h/4h/1d  (BTCUSDT desde 2017, del CSV local de D:\\traiding_ml)
  diario  platform=Yahoo      symbol=OTC_NDX y OTC_GDAXI  1d desde 2002 (indice de contado, no el CFD de Deriv)

Velas: hora UTC, 4h y 1d alineadas a las 00 UTC como Deriv; solo dias completos (hasta ayer).
Se puede repetir: ON CONFLICT actualiza la vela.

Uso:
  python -I scripts/descargas/historico_extra.py [dax] [btc] [diario]     (sin argumentos: todo)
"""
import csv
import datetime as dt
import io
import json
import lzma
import os
import struct
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from decimal import Decimal

CONFIG = r'C:\DesarrolloIA\Traiding\WpfDerivClientVB\bin\Debug\net48\db_config.json'
CONTENEDOR = 'traiding_postgres'
BINANCE_CSV = r'D:\traiding_ml\cripto\datos\Binance_BTCUSDT_1h.csv'
UA = 'Mozilla/5.0 (historico)'
DUKA = 'http://datafeed.dukascopy.com/datafeed/'   # el https se satura; el http responde bien
DESDE_DIARIO = dt.date(2002, 1, 1)


def log(msg):
    print(time.strftime('%H:%M:%S'), msg, flush=True)


def fecha(t):
    return dt.datetime.fromtimestamp(t, dt.timezone.utc).date()


def hoy_epoch():
    t = int(time.time())
    return t - t % 86400


def bajar(url, intentos=5, espera=5):
    """Bytes del recurso, None si no existe (404)."""
    for k in range(intentos):
        try:
            req = urllib.request.Request(url, headers={'User-Agent': UA})
            with urllib.request.urlopen(req, timeout=90) as r:
                return r.read()
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            if k == intentos - 1:
                raise
        except OSError:
            if k == intentos - 1:
                raise
        time.sleep(espera * (k + 1))


FALLIDOS = []


def bajar_duka(url):
    """Dukascopy se satura (503): reintenta mas despacio; si aun falla, lo anota y sigue (volver a correr lo completa)."""
    try:
        return bajar(url, intentos=8, espera=10)
    except OSError as e:
        FALLIDOS.append(url)
        log('  FALLO %s (%s)' % (url, e))
        return b''


# ---------------------------------------------------------------- base de datos

def guardar(platform, symbol, tf, velas, tanda=20000):
    """velas: [epoch, o, h, l, c] (Decimal). Upsert por (platform, symbol, timeframe, epoch)."""
    if not velas:
        return 0
    cfg = json.load(open(CONFIG, encoding='utf-8-sig'))
    for i in range(0, len(velas), tanda):
        buf = io.StringIO()
        for e, o, h, l, c in velas[i:i + tanda]:
            buf.write('%d,%s,%s,%s,%s\n' % (e, o, h, l, c))
        sql = ("CREATE TEMP TABLE t (epoch bigint, o numeric, h numeric, l numeric, c numeric);\n"
               "COPY t FROM STDIN WITH (FORMAT csv);\n" + buf.getvalue() + "\\.\n"
               "INSERT INTO candles (platform, symbol, timeframe, epoch, open_time, open_price, high_price, low_price, close_price, is_closed)\n"
               "SELECT '%s', '%s', '%s', epoch, to_timestamp(epoch), o, h, l, c, true FROM t\n"
               "ON CONFLICT (platform, symbol, timeframe, epoch) DO UPDATE SET open_time = EXCLUDED.open_time,\n"
               "  open_price = EXCLUDED.open_price, high_price = EXCLUDED.high_price, low_price = EXCLUDED.low_price,\n"
               "  close_price = EXCLUDED.close_price, is_closed = true;\n") % (platform, symbol, tf)
        r = subprocess.run(['docker', 'exec', '-i', '-e', 'PGPASSWORD=' + cfg['Password'], CONTENEDOR, 'psql', '-h', '127.0.0.1',
                            '-U', cfg['Username'], '-d', cfg['Database'], '-v', 'ON_ERROR_STOP=1', '-q', '-f', '-'],
                           input=sql, capture_output=True, text=True)
        if r.returncode != 0:
            raise RuntimeError('psql: ' + r.stderr[-800:])
    return len(velas)


def agregar(velas, seg):
    res = []
    for e, o, h, l, c in sorted(velas):
        b = e - e % seg
        if res and res[-1][0] == b:
            x = res[-1]
            x[2] = max(x[2], h)
            x[3] = min(x[3], l)
            x[4] = c
        else:
            res.append([b, o, h, l, c])
    return res


def guardar_hl(platform, symbol, velas_1h):
    """Guarda 1h y sus agregadas 4h y 1d (la lista debe tener la serie completa)."""
    velas_1h = sorted(velas_1h)
    for tf, seg in (('1h', 3600), ('4h', 14400), ('1d', 86400)):
        lote = velas_1h if seg == 3600 else agregar(velas_1h, seg)
        guardar(platform, symbol, tf, lote)
        log('  %s %s %s: %d velas %s -> %s' % (platform, symbol, tf, len(lote),
            fecha(lote[0][0]), fecha(lote[-1][0])))


def valida(o, h, l, c):
    return l > 0 and h >= l and l <= o <= h and l <= c <= h


# ---------------------------------------------------------------- Dukascopy (mismo formato que la app)

def leer_bi5(datos, epoch0, divisor):
    """Registros de 24 bytes big-endian: segundos, apertura, cierre, minimo, maximo (enteros) y volumen."""
    if not datos or len(datos) < 13:
        return []
    crudo = lzma.decompress(datos, format=lzma.FORMAT_ALONE)
    res = []
    for k in range(len(crudo) // 24):
        s, o, c, l, h, vol = struct.unpack('>5if', crudo[k * 24:(k + 1) * 24])
        if vol <= 0 or not valida(o, h, l, c):   # sin volumen = relleno de fin de semana/festivo
            continue
        res.append([epoch0 + s, Decimal(o) / divisor, Decimal(h) / divisor, Decimal(l) / divisor, Decimal(c) / divisor])
    return res


def dukascopy(symbol, sd, desde_anio=2003):
    divisor = Decimal(1000)   # indices
    fin = hoy_epoch()
    hoy = fecha(fin)
    mes_actual = hoy.replace(day=1)
    previo = (mes_actual - dt.timedelta(days=1)).replace(day=1)
    velas, mes = [], dt.date(desde_anio, 1, 1)
    while mes < mes_actual:
        datos = bajar_duka('%s%s/%04d/%02d/BID_candles_hour_1.bi5' % (DUKA, sd, mes.year, mes.month - 1))
        epoch0 = int(dt.datetime(mes.year, mes.month, 1, tzinfo=dt.timezone.utc).timestamp())
        if datos is None and mes == previo:
            velas += minutos_a_horas(sd, divisor, mes, mes_actual)
        else:
            v = leer_bi5(datos, epoch0, divisor)
            if v and not velas:
                log('  %s: primer mes con datos %s' % (sd, mes))
            velas += v
        mes = (mes + dt.timedelta(days=32)).replace(day=1)
        time.sleep(0.3)
    velas += minutos_a_horas(sd, divisor, mes_actual, hoy)   # el archivo mensual del mes en curso aun no existe
    velas = [v for v in velas if v[0] < fin]
    guardar_hl('Dukascopy', symbol, velas)
    if FALLIDOS:
        log('  %d archivo(s) de Dukascopy no se pudieron bajar: vuelve a correr "dax" para completarlos' % len(FALLIDOS))


def minutos_a_horas(sd, divisor, desde, hasta_ex):
    minutos, d = [], desde
    while d < hasta_ex:
        if d.weekday() != 5:
            datos = bajar_duka('%s%s/%04d/%02d/%02d/BID_candles_min_1.bi5' % (DUKA, sd, d.year, d.month - 1, d.day))
            minutos += leer_bi5(datos, int(dt.datetime(d.year, d.month, d.day, tzinfo=dt.timezone.utc).timestamp()), divisor)
            time.sleep(0.3)
        d += dt.timedelta(days=1)
    return agregar(minutos, 3600)


# ---------------------------------------------------------------- BTC

def bitstamp():
    fin, inicio = hoy_epoch(), 1313020800   # 2011-08-11
    velas, t = [], inicio
    while t < fin:
        j = json.loads(bajar('https://www.bitstamp.net/api/v2/ohlc/btcusd/?step=3600&limit=1000&start=%d' % t))
        filas = j['data']['ohlc']
        if not filas:
            t += 1000 * 3600
            continue
        for x in filas:
            e = int(x['timestamp'])
            o, h, l, c = (Decimal(x[k]) for k in ('open', 'high', 'low', 'close'))
            if e < fin and Decimal(x['volume']) > 0 and valida(o, h, l, c):
                velas.append([e, o, h, l, c])
        t = int(filas[-1]['timestamp']) + 3600
        time.sleep(0.3)
    guardar_hl('Bitstamp', 'cryBTCUSD', velas)


def binance():
    fin, velas = hoy_epoch(), []
    with open(BINANCE_CSV, newline='') as f:
        for r in csv.DictReader(f):
            e = int(r['epoch'])
            o, h, l, c = (Decimal(r[k]) for k in ('o', 'h', 'l', 'c'))
            if e < fin and Decimal(r['volumen']) > 0 and valida(o, h, l, c):
                velas.append([e, o, h, l, c])
    guardar_hl('Binance', 'cryBTCUSD', velas)


# ---------------------------------------------------------------- diario de Yahoo

def yahoo(ticker, symbol):
    p1 = int(dt.datetime(DESDE_DIARIO.year, DESDE_DIARIO.month, DESDE_DIARIO.day, tzinfo=dt.timezone.utc).timestamp())
    fin = hoy_epoch()
    url = 'https://query1.finance.yahoo.com/v8/finance/chart/%s?period1=%d&period2=%d&interval=1d' % (
        urllib.parse.quote(ticker), p1, fin)
    r = json.loads(bajar(url))['chart']['result'][0]
    q = r['indicators']['quote'][0]
    velas = []
    for i, t in enumerate(r['timestamp']):
        dia = t - t % 86400   # la marca es la apertura local; en UTC cae el mismo dia (NY y Frankfurt)
        o, h, l, c = (q[k][i] for k in ('open', 'high', 'low', 'close'))
        if dia >= fin or None in (o, h, l, c):
            continue
        o, h, l, c = (Decimal(str(round(x, 4))) for x in (o, h, l, c))
        if valida(o, h, l, c):
            velas.append([dia, o, h, l, c])
    guardar('Yahoo', symbol, '1d', velas)
    log('  Yahoo %s 1d: %d velas %s -> %s' % (symbol, len(velas), fecha(velas[0][0]),
                                               fecha(velas[-1][0])))


def main(que):
    que = set(que) or {'dax', 'btc', 'diario'}
    if 'diario' in que:
        log('Yahoo diario')
        yahoo('^NDX', 'OTC_NDX')
        yahoo('^GDAXI', 'OTC_GDAXI')
    if 'btc' in que:
        log('BTC Binance (CSV local)')
        binance()
        log('BTC Bitstamp')
        bitstamp()
    if 'dax' in que:
        log('DAX Dukascopy')
        dukascopy('OTC_GDAXI', 'DEUIDXEUR')
    log('listo')


if __name__ == '__main__':
    main(sys.argv[1:])
