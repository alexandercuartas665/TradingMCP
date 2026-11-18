"""Lee velas de la base de la app (tabla candles, contenedor traiding_postgres) eligiendo la fuente.

Las fuentes son las mismas que en la app: Deriv, Dukascopy, Bitstamp, Binance, Yahoo. Las externas
sirven para investigar con mas historia; lo que salga bien se confirma en Deriv (forward test).

Uso como modulo (desde scripts/ml):
    import velas_bd
    df = velas_bd.velas('Bitstamp', 'cryBTCUSD', '1d', desde='2011-01-01')   # epoch, o, h, l, c
    velas_bd.cobertura()                                                     # fuente, symbol, timeframe, velas, desde, hasta

Uso por consola:
    python -I scripts/ml/velas_bd.py cobertura
    python -I scripts/ml/velas_bd.py exportar Bitstamp cryBTCUSD 1h salida.csv [--desde 2013-01-01]
"""
import argparse
import datetime as dt
import io
import json
import subprocess

import pandas as pd

CONFIG = r'C:\DesarrolloIA\Traiding\WpfDerivClientVB\bin\Debug\net48\db_config.json'
CONTENEDOR = 'traiding_postgres'
FUENTES = ('Deriv', 'Dukascopy', 'Bitstamp', 'Binance', 'Yahoo')
TEMPORALIDADES = ('1m', '5m', '15m', '30m', '1h', '4h', '1d')


def _fuente(f):
    for x in FUENTES:
        if x.lower() == str(f).strip().lower():
            return x
    raise ValueError('Fuente desconocida: %s (use %s)' % (f, ', '.join(FUENTES)))


def _epoch(fecha):
    if fecha is None:
        return None
    if isinstance(fecha, (int, float)):
        return int(fecha)
    d = dt.date.fromisoformat(str(fecha))
    return int(dt.datetime(d.year, d.month, d.day, tzinfo=dt.timezone.utc).timestamp())


def _texto(valor):
    """Literal SQL seguro para los valores que se aceptan (sin comillas ni barras)."""
    v = str(valor)
    if any(c in v for c in "'\\;\"") or len(v) > 30:
        raise ValueError('Valor no permitido: %r' % v)
    return "'" + v + "'"


def _psql(sql):
    cfg = json.load(open(CONFIG, encoding='utf-8-sig'))
    r = subprocess.run(['docker', 'exec', '-i', '-e', 'PGPASSWORD=' + cfg['Password'], CONTENEDOR, 'psql', '-h', '127.0.0.1',
                        '-U', cfg['Username'], '-d', cfg['Database'], '-v', 'ON_ERROR_STOP=1', '-q', '-f', '-'],
                       input=sql, capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError('psql: ' + r.stderr[-800:])
    return r.stdout


def velas(fuente, symbol, timeframe='1h', desde=None, hasta=None):
    """DataFrame (epoch, o, h, l, c) ordenado, en [desde, hasta) (fechas YYYY-MM-DD o epoch)."""
    if timeframe not in TEMPORALIDADES:
        raise ValueError('Temporalidad no valida: %s' % timeframe)
    filtro = ['platform = ' + _texto(_fuente(fuente)), 'symbol = ' + _texto(symbol), 'timeframe = ' + _texto(timeframe)]
    if desde is not None:
        filtro.append('epoch >= %d' % _epoch(desde))
    if hasta is not None:
        filtro.append('epoch < %d' % _epoch(hasta))
    sql = ('COPY (SELECT epoch, open_price, high_price, low_price, close_price FROM public.candles WHERE %s ORDER BY epoch) '
           'TO STDOUT WITH (FORMAT csv);\n' % ' AND '.join(filtro))
    df = pd.read_csv(io.StringIO(_psql(sql)), names=['epoch', 'o', 'h', 'l', 'c'])
    return df.astype({'epoch': 'int64', 'o': float, 'h': float, 'l': float, 'c': float})


def cobertura():
    """DataFrame (fuente, symbol, timeframe, velas, desde, hasta) de toda la base."""
    sql = ("COPY (SELECT platform, symbol, timeframe, COUNT(*), MIN(epoch), MAX(epoch) FROM public.candles "
           "GROUP BY 1, 2, 3 ORDER BY 1, 2, 3) TO STDOUT WITH (FORMAT csv);\n")
    df = pd.read_csv(io.StringIO(_psql(sql)), names=['fuente', 'symbol', 'timeframe', 'velas', 'desde', 'hasta'])
    for k in ('desde', 'hasta'):
        df[k] = pd.to_datetime(df[k], unit='s', utc=True).dt.strftime('%Y-%m-%d')
    return df


def main():
    ap = argparse.ArgumentParser(description='Velas de la base de la app por fuente.')
    sub = ap.add_subparsers(dest='accion', required=True)
    sub.add_parser('cobertura')
    ex = sub.add_parser('exportar')
    ex.add_argument('fuente')
    ex.add_argument('symbol')
    ex.add_argument('timeframe')
    ex.add_argument('salida')
    ex.add_argument('--desde')
    ex.add_argument('--hasta')
    a = ap.parse_args()
    if a.accion == 'cobertura':
        print(cobertura().to_string(index=False))
    else:
        df = velas(a.fuente, a.symbol, a.timeframe, a.desde, a.hasta)
        df.to_csv(a.salida, index=False, header=False)
        print('%d velas -> %s' % (len(df), a.salida))


if __name__ == '__main__':
    main()
