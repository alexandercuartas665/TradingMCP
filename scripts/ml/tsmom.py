"""Momentum de serie temporal diversificado (TSMOM) con tamano por volatilidad.

Plan pre-registrado: docs/validacion/tsmom_plan.md (commit e453f72). Universo A (ya visto) solo para
desarrollar y depurar; universo B (59 instrumentos nunca vistos) solo despues de la enmienda 1 del plan
(hash de este script en main) y del visto bueno del lider. Enmienda 2 (2026-11-25): splits de ETF ajustados
en la serie de precios y corte de la serie en huecos de mas de 30 dias calendario. Enmienda 3 (2026-11-25):
swap 0 sin posicion y guarda contra NaN en los escenarios.

Uso (entorno de D:):
    python -I scripts/ml/tsmom.py bajar A
    python -I scripts/ml/tsmom.py correr A [--perm 0] [--salida RUTA]
    python -I scripts/ml/tsmom.py fuga A                  # comprobacion de fuga de futuro
    python -I scripts/ml/tsmom.py hora A                  # hora de cierre de la vela diaria vs la BD
    (B exige ademas --enmienda1-aprobada)
"""
import argparse
import datetime as dt
import json
import lzma
import os
import sys
import time
import urllib.error
import urllib.request

import numpy as np
import pandas as pd

AQUI = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(AQUI))
CARPETA = r'D:\traiding_ml\tsmom'
URL = 'https://datafeed.dukascopy.com/datafeed/%s/%04d/BID_candles_day_1.bi5'
URL_H = 'https://datafeed.dukascopy.com/datafeed/%s/%04d/%02d/BID_candles_hour_1.bi5'   # mes 0-11
URL_M = 'https://datafeed.dukascopy.com/datafeed/%s/%04d/%02d/%02d/BID_candles_min_1.bi5'   # mes 0-11, dia 1-31
ESPEC = os.path.join(REPO, 'docs', 'validacion', 'deriv_especificaciones_2026-11-22.json')
FIN = '2026-11-20'                 # ultimo cierre evaluado (plan)
PAUSA = 1.5

VOL_OBJ = 0.10                     # volatilidad objetivo por mercado (anual)
TOPE_APAL = 3.0                    # enmienda 1: 0,10/vol nunca mayor que 3
TOPE_RIESGO = 1.0 / 12             # enmienda 1: parte maxima del presupuesto de riesgo por mercado
BANDA = 0.25                       # enmienda 1: solo se opera si cambia el signo o |obj - actual| > 25% de |obj|
CAPITAL = 100000.0                 # enmienda 1: capital de referencia (USD) para comisiones minimas
N_VOL = 60
CALENT = 252 + N_VOL               # cierres validos para entrar
CORTE_DIAS = 30                    # enmienda 2: un hueco de mas de 30 dias calendario corta la serie
SPLIT_K = (2, 3, 4)                # enmienda 2: splits de ETF k:1 y sus inversos
SPLIT_TOL = 0.02                   # enmienda 2: tolerancia (2 puntos) alrededor de 1/k - 1 o k - 1
DURACIONES = (63, 126, 252)
PRINCIPAL = 252
RECARGO = 0.02                     # Deriv CFD (secundario): recargo anual sobre el nocional bruto
NW_REZAGOS = 5
TASAS = r'D:\traiding_ml\tsmom\tasas\cbpol_diario.csv'   # BIS WS_CBPOL, frecuencia diaria
AREA = {'AUD': 'AU', 'CAD': 'CA', 'CHF': 'CH', 'JPY': 'JP', 'NZD': 'NZ', 'EUR': 'XM', 'GBP': 'GB', 'USD': 'US',
        'MXN': 'MX', 'NOK': 'NO', 'SEK': 'SE', 'PLN': 'PL', 'ZAR': 'ZA', 'THB': 'TH', 'CNH': 'CN'}
METALES = ('XAU', 'XAG')           # sin tasa propia: rendimiento 0 (se ignora la tasa de arrendamiento)

# IBKR Pro (escenario principal). Fuentes leidas el 2026-11-22, ver enmienda 1 del plan.
IB_COMISION_FX = 0.00002           # 0,20 pb del valor operado (nivel I)
IB_MINIMO_FX = 2.0                 # USD por orden (nivel I)
IB_COMISION_CFD = 0.0001           # CFD de indices: 0,005%-0,01% (se toma 0,01%); tambien ETF (aproximacion)
IB_COMISION_MAT = 0.00015          # CFD de petroleo: 0,015%; tambien cobre, cacao, algodon y metales de A (aproximacion)
IB_RECARGO_CFD = 0.015             # CFD de indices: BM +/- 1,5% (se cobra el 1,5%)
# diferencial sobre la tasa de referencia (nivel I): (paga al tener la moneda, cobra al deberla)
IB_SPREAD_TASA = {'USD': (0.5, 1.5), 'EUR': (0.5, 1.5), 'GBP': (0.5, 1.5), 'JPY': (0.25, 1.5), 'CHF': (0.25, 1.5),
                  'CAD': (0.5, 1.5), 'AUD': (0.5, 1.5), 'NZD': (2.5, 1.5), 'SEK': (0.5, 1.5), 'NOK': (2.0, 1.5),
                  'PLN': (2.0, 3.0), 'MXN': (4.0, 3.0), 'ZAR': (1.0, 1.5), 'CNH': (None, 4.0)}
IB_COMISION_ETF = 0.0001           # acciones IBKR Pro fija: USD 0,005 por accion (~0,005-0,01% a 50-100 USD); se toma 0,01%
IB_MINIMO_ETF = 1.0                # USD por orden; maximo 1% del valor
IB_RECARGO_ETF = (0.015, 0.02)     # largo: margen BM + 1,5%; corto: supuesto fijo conservador 2,0% (prestamo sin cifra publicada)
CAPITAL_CHICO = 10000.0            # informativo
CAPITAL_MINI = 100.0               # capital declarado por el usuario (2026-11-22): pregunta 2
IB_SIN_DIVISA = ('THB',)           # IBKR no lista THB: USDTHB usa los costos de Deriv tambien en el principal

# (codigo Dukascopy, clase, desde, nombre en Deriv)
UNIVERSO_A = [
    ('EURUSD', 'divisas', '2003-05-04', 'EURUSD'), ('GBPUSD', 'divisas', '2003-05-04', 'GBPUSD'),
    ('USDJPY', 'divisas', '2003-05-04', 'USDJPY'), ('USDCHF', 'divisas', '2003-05-04', 'USDCHF'),
    ('GBPJPY', 'divisas', '2003-08-03', 'GBPJPY'),
    ('XAUUSD', 'materias', '2003-05-05', 'XAUUSD'), ('XAGUSD', 'materias', '2003-05-04', 'XAGUSD'),
    ('USATECHIDXUSD', 'acciones', '2011-09-18', 'US Tech 100'), ('USA500IDXUSD', 'acciones', '2011-09-18', 'US SP 500'),
    ('USA30IDXUSD', 'acciones', '2013-09-30', 'Wall Street 30'),
]
UNIVERSO_B = [
    ('AUDCAD', 'divisas', '2006-01-03', 'AUDCAD'), ('AUDCHF', 'divisas', '2006-03-01', 'AUDCHF'),
    ('AUDJPY', 'divisas', '2003-11-30', 'AUDJPY'), ('AUDNZD', 'divisas', '2006-12-12', 'AUDNZD'),
    ('AUDSGD', 'divisas', '2007-03-13', 'AUDSGD'), ('AUDUSD', 'divisas', '2003-08-03', 'AUDUSD'),
    ('CADCHF', 'divisas', '2006-01-02', 'CADCHF'), ('CADJPY', 'divisas', '2004-10-25', 'CADJPY'),
    ('EURAUD', 'divisas', '2005-10-07', 'EURAUD'), ('EURCAD', 'divisas', '2004-10-25', 'EURCAD'),
    ('EURMXN', 'divisas', '2007-03-13', 'EURMXN'), ('EURNOK', 'divisas', '2004-10-25', 'EURNOK'),
    ('EURNZD', 'divisas', '2006-01-02', 'EURNZD'), ('EURPLN', 'divisas', '2007-03-13', 'EURPLN'),
    ('EURSEK', 'divisas', '2004-10-27', 'EURSEK'), ('EURSGD', 'divisas', '2007-03-13', 'EURSGD'),
    ('EURZAR', 'divisas', '2007-03-13', 'EURZAR'), ('GBPAUD', 'divisas', '2006-03-22', 'GBPAUD'),
    ('GBPCAD', 'divisas', '2006-01-02', 'GBPCAD'), ('GBPNZD', 'divisas', '2006-01-02', 'GBPNZD'),
    ('NZDCAD', 'divisas', '2006-01-02', 'NZDCAD'), ('NZDCHF', 'divisas', '2006-01-02', 'NZDCHF'),
    ('NZDJPY', 'divisas', '2006-01-02', 'NZDJPY'), ('NZDSGD', 'divisas', '2007-03-13', 'NZDSGD'),
    ('NZDUSD', 'divisas', '2003-08-03', 'NZDUSD'), ('SGDJPY', 'divisas', '2007-03-13', 'SGDJPY'),
    ('USDCAD', 'divisas', '2003-08-03', 'USDCAD'), ('USDCNH', 'divisas', '2012-06-26', 'USDCNH'),
    ('USDMXN', 'divisas', '2007-03-13', 'USDMXN'), ('USDNOK', 'divisas', '2003-08-03', 'USDNOK'),
    ('USDPLN', 'divisas', '2007-03-13', 'USDPLN'), ('USDSEK', 'divisas', '2003-08-03', 'USDSEK'),
    ('USDSGD', 'divisas', '2004-11-16', 'USDSGD'), ('USDTHB', 'divisas', '2017-02-13', 'USDTHB'),
    ('USDZAR', 'divisas', '2003-08-03', 'USDZAR'),
    # (los pares con SGD salen por la regla de la enmienda 1: el BIS no publica tasa de politica de Singapur)
    ('BRENTCMDUSD', 'materias', '2010-12-02', 'UK Brent Oil'), ('COPPERCMDUSD', 'materias', '2012-03-02', 'XCUUSD'),
    ('COCOACMDUSD', 'materias', '2017-10-20', 'Cocoa'), ('COTTONCMDUSX', 'materias', '2017-10-20', 'Cotton'),
    ('AUSIDXAUD', 'acciones', '2014-06-01', 'Australia 200'), ('EUSIDXEUR', 'acciones', '2014-06-01', 'Europe 50'),
    ('FRAIDXEUR', 'acciones', '2011-09-18', 'France 40'), ('DEUIDXEUR', 'acciones', '2013-09-30', 'Germany 40'),
    ('HKGIDXHKD', 'acciones', '2013-06-03', 'Hong Kong 50'), ('JPNIDXJPY', 'acciones', '2011-09-18', 'Japan 225'),
    ('NLDIDXEUR', 'acciones', '2017-12-01', 'Netherlands 25'), ('ESPIDXEUR', 'acciones', '2014-06-01', 'Spain 35'),
    ('CHEIDXCHF', 'acciones', '2011-09-18', 'Swiss 20'), ('GBRIDXGBP', 'acciones', '2011-09-18', 'UK 100'),
    ('EEMUSUSD', 'acciones', '2017-01-26', 'EEM.US'), ('EFAUSUSD', 'acciones', '2017-01-26', 'EFA.US'),
    ('GDXUSUSD', 'acciones', '2017-01-26', 'GDX.US'), ('IJRUSUSD', 'acciones', '2017-05-11', 'IJR.US'),
    ('IWMUSUSD', 'acciones', '2017-01-26', 'IWM.US'), ('VEAUSUSD', 'acciones', '2017-11-15', 'VEA.US'),
    ('VNQUSUSD', 'acciones', '2017-11-15', 'VNQ.US'), ('XLEUSUSD', 'acciones', '2017-11-15', 'XLE.US'),
    ('XLFUSUSD', 'acciones', '2017-01-26', 'XLF.US'), ('XLKUSUSD', 'acciones', '2017-11-15', 'XLK.US'),
]
SIN_TASA = [x[0] for x in UNIVERSO_B if 'SGD' in x[0]]
UNIVERSO_B = [x for x in UNIVERSO_B if 'SGD' not in x[0]]     # enmienda 1: 59 - 5 = 54
UNIVERSOS = {'A': UNIVERSO_A, 'B': UNIVERSO_B}
MIN_ACTIVOS = {'A': 5, 'B': 12}


def log(msg):
    print('%s %s' % (time.strftime('%H:%M:%S'), msg), flush=True)


def es_divisa(cod):
    return len(cod) == 6 and cod.isalpha()


def divisor(cod):
    """Escala de precio de Dukascopy (solo importa para convertir swaps a %; los retornos no dependen)."""
    if es_divisa(cod):
        return 1000.0 if cod[3:] in ('JPY', 'THB') else 100000.0
    return 1000.0


# ---------------------------------------------------------------- datos

def bajar(url, intentos=10):
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


def ruta_crudo(cod, anio):
    return os.path.join(CARPETA, 'crudo', cod, '%04d.bi5' % anio)


def ruta_mes(cod, anio, mes):
    return os.path.join(CARPETA, 'crudo', cod, '%04d_%02d_h.bi5' % (anio, mes))


def ruta_dia(cod, fecha):
    return os.path.join(CARPETA, 'crudo', cod, '%04d_%02d_%02d_m.bi5' % (fecha.year, fecha.month - 1, fecha.day))


def dias_mes_fin():
    """Dias lunes a viernes del mes de FIN hasta FIN (tampoco se publica el archivo horario del mes en curso)."""
    fin = dt.date.fromisoformat(FIN)
    return [fin.replace(day=d) for d in range(1, fin.day + 1) if fin.replace(day=d).weekday() < 5]


def guardar(ruta, datos):
    os.makedirs(os.path.dirname(ruta), exist_ok=True)
    tmp = ruta + '.tmp'
    with open(tmp, 'wb') as f:
        f.write(datos)
    os.replace(tmp, ruta)


def bajar_universo(nombre):
    """Anos cerrados: archivo diario del ano. Ano de FIN: Dukascopy no publica el archivo diario del ano en
    curso, asi que se bajan los archivos mensuales de 1 hora (mes 0-11) y se agrupan en dias UTC 00-24."""
    faltan = 0
    fin = dt.date.fromisoformat(FIN)
    for cod, _, desde, _ in UNIVERSOS[nombre]:
        for anio in range(int(desde[:4]), fin.year):
            ruta = ruta_crudo(cod, anio)
            if os.path.exists(ruta):
                continue
            try:
                datos = bajar(URL % (cod, anio))
            except RuntimeError as e:
                log('%s %d: ERROR %s' % (cod, anio, e))
                faltan += 1
                continue
            guardar(ruta, datos)
            log('%s %d: %d bytes' % (cod, anio, len(datos)))
        for mes in range(fin.month - 1):
            ruta = ruta_mes(cod, fin.year, mes)
            if os.path.exists(ruta) and os.path.getsize(ruta) > 0:
                continue
            try:
                datos = bajar(URL_H % (cod, fin.year, mes))
            except RuntimeError as e:
                log('%s %d-%02d: ERROR %s' % (cod, fin.year, mes, e))
                faltan += 1
                continue
            guardar(ruta, datos)
            log('%s %d-%02d (1h): %d bytes' % (cod, fin.year, mes, len(datos)))
        for dia in dias_mes_fin():
            ruta = ruta_dia(cod, dia)
            if os.path.exists(ruta) and os.path.getsize(ruta) > 0:
                continue
            try:
                datos = bajar(URL_M % (cod, dia.year, dia.month - 1, dia.day))
            except RuntimeError as e:
                log('%s %s: ERROR %s' % (cod, dia, e))
                faltan += 1
                continue
            guardar(ruta, datos)
            log('%s %s (1m): %d bytes' % (cod, dia, len(datos)))
    return faltan


def decodificar(datos, epoch0, cod):
    if len(datos) < 13:
        return pd.DataFrame(columns=['epoch', 'o', 'h', 'l', 'c', 'v'])
    crudo = lzma.decompress(datos, format=lzma.FORMAT_ALONE)
    n = len(crudo) // 24
    a = np.frombuffer(crudo[:n * 24], dtype='>i4').reshape(n, 6)
    vol = np.frombuffer(crudo[:n * 24], dtype='>f4').reshape(n, 6)[:, 5]
    s, o, c, l, h = (a[:, i].astype(np.int64) for i in range(5))
    k = divisor(cod)
    return pd.DataFrame({'epoch': epoch0 + s, 'o': o / k, 'h': h / k, 'l': l / k, 'c': c / k, 'v': vol})


def leer_anio(cod, anio):
    fin = dt.date.fromisoformat(FIN)
    if anio < fin.year:
        ruta = ruta_crudo(cod, anio)
        if not os.path.exists(ruta):
            return None
        with open(ruta, 'rb') as f:
            return decodificar(f.read(), int(dt.datetime(anio, 1, 1, tzinfo=dt.timezone.utc).timestamp()), cod)
    partes = []
    for mes in range(fin.month - 1):
        ruta = ruta_mes(cod, anio, mes)
        if not os.path.exists(ruta):
            return None
        with open(ruta, 'rb') as f:
            e0 = int(dt.datetime(anio, mes + 1, 1, tzinfo=dt.timezone.utc).timestamp())
            partes.append(decodificar(f.read(), e0, cod))
    for dia in dias_mes_fin():
        ruta = ruta_dia(cod, dia)
        if not os.path.exists(ruta):
            return None
        with open(ruta, 'rb') as f:
            e0 = int(dt.datetime(dia.year, dia.month, dia.day, tzinfo=dt.timezone.utc).timestamp())
            partes.append(decodificar(f.read(), e0, cod))
    h = pd.concat(partes, ignore_index=True)
    h = h[h['v'] > 0]
    if h.empty:
        return h
    h['dia'] = h['epoch'] - h['epoch'] % 86400
    g = h.sort_values('epoch').groupby('dia')
    return pd.DataFrame({'epoch': g['epoch'].first().index, 'o': g['o'].first().values, 'h': g['h'].max().values,
                         'l': g['l'].min().values, 'c': g['c'].last().values, 'v': g['v'].sum().values})


def serie(cod, desde):
    """Cierres diarios validos (lunes a viernes, volumen > 0) desde la fecha del plan hasta FIN.
    Devuelve (Serie de cierres indexada por fecha, avisos)."""
    partes = []
    for anio in range(int(desde[:4]), int(FIN[:4]) + 1):
        df = leer_anio(cod, anio)
        if df is None:
            raise FileNotFoundError('falta %s' % ruta_crudo(cod, anio))
        partes.append(df)
    df = pd.concat(partes, ignore_index=True)
    df['fecha'] = pd.to_datetime(df['epoch'], unit='s', utc=True).dt.tz_localize(None).dt.normalize()
    df = df[(df['v'] > 0) & (df['fecha'].dt.day_of_week < 5)]
    df = df[(df['fecha'] >= pd.Timestamp(desde)) & (df['fecha'] <= pd.Timestamp(FIN))]
    df = df.drop_duplicates('fecha', keep='last').set_index('fecha').sort_index()
    avisos = []
    malos = int((df['c'] <= 0).sum())
    if malos:
        avisos.append('%d cierres <= 0' % malos)
    df = df[df['c'] > 0]
    r = df['c'].pct_change().abs()
    saltos = int((r > 0.5).sum())
    if saltos:
        avisos.append('%d retornos diarios > 50%%' % saltos)
    return df['c'], avisos


BD_A = {'EURUSD': 'frxEURUSD', 'GBPUSD': 'frxGBPUSD', 'USDJPY': 'frxUSDJPY', 'USDCHF': 'frxUSDCHF',
        'GBPJPY': 'frxGBPJPY', 'XAUUSD': 'frxXAUUSD', 'XAGUSD': 'frxXAGUSD', 'USATECHIDXUSD': 'OTC_NDX',
        'USA500IDXUSD': 'OTC_SPC', 'USA30IDXUSD': 'OTC_DJI'}


def serie_bd(cod, desde):
    """Solo para depurar con el universo A mientras baja el datafeed: vela 1d de la BD (Dukascopy, 00-24 UTC)."""
    sys.path.insert(0, AQUI)
    import velas_bd
    df = velas_bd.velas('Dukascopy', BD_A[cod], '1d', desde=desde)
    df['fecha'] = pd.to_datetime(df['epoch'], unit='s', utc=True).dt.tz_localize(None).dt.normalize()
    df = df[(df['fecha'].dt.day_of_week < 5) & (df['fecha'] <= pd.Timestamp(FIN)) & (df['c'] > 0)]
    return df.drop_duplicates('fecha', keep='last').set_index('fecha')['c'].sort_index(), []


FUENTE_BD = False


def es_etf(cod):
    return cod.endswith('USUSD')


def ajustar_splits(c):
    """Enmienda 2: en ETF, un retorno diario a menos de SPLIT_TOL de 1/k - 1 (split k:1) o de k - 1 (inverso),
    k en SPLIT_K, se toma como split: los precios anteriores se dividen por k (o se multiplican). El retorno
    de ese dia queda (1 + r) x k - 1 (o (1 + r) / k - 1). Devuelve (serie ajustada, lista de splits)."""
    v = c.values.astype(float)
    factor = np.ones(len(v))
    hechos = []
    for i in range(1, len(v)):
        r = v[i] / v[i - 1] - 1.0
        for k in SPLIT_K:
            if abs(r - (1.0 / k - 1.0)) < SPLIT_TOL:
                factor[:i] /= k
                hechos.append({'fecha': str(c.index[i].date()), 'retorno': r, 'k': k})
                break
            if abs(r - (k - 1.0)) < SPLIT_TOL:
                factor[:i] *= k
                hechos.append({'fecha': str(c.index[i].date()), 'retorno': r, 'k': 1.0 / k})
                break
    if not hechos:
        return c, hechos
    return pd.Series(v * factor, index=c.index, name=c.name), hechos


def tramos(c):
    """Enmienda 2: numero de tramo de cada cierre; un hueco de mas de CORTE_DIAS dias calendario entre dos
    cierres validos abre un tramo nuevo."""
    d = c.index.to_series().diff().dt.days.fillna(0)
    return (d > CORTE_DIAS).cumsum().values


def avisos_serie(c):
    """Retornos diarios > 50% dentro de cada tramo. Los tramos con menos de CALENT cierres nunca se activan
    (ni alimentan a otro tramo): sus avisos se informan aparte."""
    t = tramos(c)
    usados, descartados = 0, []
    for s in np.unique(t):
        sub = c[t == s]
        n = int((sub.pct_change().abs() > 0.5).sum())
        if len(sub) >= CALENT:
            usados += n
        elif n:
            descartados.append('tramo %s a %s (%d cierres, nunca activo): %d retornos diarios > 50%%'
                               % (sub.index[0].date(), sub.index[-1].date(), len(sub), n))
    return usados, descartados


def cargar(nombre, info=None):
    """info (dict opcional) recibe por mercado los splits ajustados, los cortes y los avisos de tramos
    que nunca se activan (enmienda 2)."""
    cierres, clases, avisos = {}, {}, {}
    for cod, clase, desde, _ in UNIVERSOS[nombre]:
        c, av = serie_bd(cod, desde) if (FUENTE_BD and nombre == 'A') else serie(cod, desde)
        splits = []
        if es_etf(cod):
            c, splits = ajustar_splits(c)
        av = [a for a in av if 'retornos diarios' not in a]
        usados, descartados = avisos_serie(c)
        if usados:
            av.append('%d retornos diarios > 50%%' % usados)
        t = tramos(c)
        cortes = [{'ultimo': str(c.index[i - 1].date()), 'vuelve': str(c.index[i].date()),
                   'dias': int((c.index[i] - c.index[i - 1]).days)} for i in np.nonzero(np.diff(t))[0] + 1]
        cierres[cod], clases[cod] = c, clase
        if av:
            avisos[cod] = av
        if info is not None and (splits or cortes or descartados):
            info[cod] = {'splits': splits, 'cortes': cortes, 'avisos_tramos_inactivos': descartados}
    return cierres, clases, avisos


# ---------------------------------------------------------------- costos y tasas

def tabla_deriv():
    with open(ESPEC, encoding='utf-8') as f:
        return {x['instrumento']: x for x in json.load(f)['filas']}


def costos_por_mercado(nombre):
    """Por mercado: medio spread (fraccion), swaps Deriv por unidad de precio (largo, corto) o None,
    y clase de costo en IBKR ('fx', 'cfd' o 'deriv' si IBKR no lista la divisa)."""
    t = tabla_deriv()
    medio, swaps, ib, com = {}, {}, {}, {}
    for cod, clase, _, deriv in UNIVERSOS[nombre]:
        com[cod] = IB_COMISION_MAT if clase == 'materias' else IB_COMISION_CFD
        fila = t[deriv]
        medio[cod] = fila['spread_objetivo_pct'] / 100.0 / 2.0
        divisa = es_divisa(cod) and cod[:3] not in METALES
        if divisa:
            # 1 punto = ultima cifra del precio; JPY y THB con 3 decimales, el resto con 5
            punto = 0.001 if cod[3:] in ('JPY', 'THB') else 0.00001
            swaps[cod] = (fila['swap_largo_pts'] * punto, fila['swap_corto_pts'] * punto)
            ib[cod] = 'deriv' if (cod[:3] in IB_SIN_DIVISA or cod[3:] in IB_SIN_DIVISA) else 'fx'
        else:
            swaps[cod] = None
            ib[cod] = 'etf' if cod.endswith('USUSD') else 'cfd'
    return {'medio': medio, 'swaps': swaps, 'ib': ib, 'com': com}


def tasas(idx):
    """Tasas de politica diarias (% anual) por moneda en las fechas de la cartera (ultimo dato conocido)."""
    d = pd.read_csv(TASAS)
    d['cod'] = d['area'].str[:2]
    d['fecha'] = pd.to_datetime(d['fecha'])
    tabla = d.pivot_table(index='fecha', columns='cod', values='valor', aggfunc='last').sort_index()
    todo = tabla.reindex(tabla.index.union(idx)).ffill().reindex(idx)
    out = {m: todo[a] for m, a in AREA.items() if a in todo.columns}
    for m in METALES:
        out[m] = pd.Series(0.0, index=idx)
    return pd.DataFrame(out)


def monedas(cod):
    return cod[:3], cod[3:]


# ---------------------------------------------------------------- cartera

def panel(cierres):
    fechas = sorted(set().union(*[set(c.index) for c in cierres.values()]))
    idx = pd.DatetimeIndex(fechas)
    return pd.DataFrame({k: c.reindex(idx).ffill() for k, c in cierres.items()})


def objetivos(cierres, clases, duracion):
    """Pesos objetivo diarios y mercados activos.
    Parte de riesgo = min(1/(clases activas x mercados activos de su clase), 1/12); lo que sobra no se
    redistribuye. Apalancamiento por mercado = min(0,10/vol, 3)."""
    precios = panel(cierres)
    idx = precios.index
    senal, vol, activo, cierre = {}, {}, {}, {}
    for k, c in cierres.items():
        # enmienda 2: senal, vol y conteo de cierres se calculan dentro de cada tramo; en el ultimo cierre
        # valido antes de un corte el mercado sale (conteo 0 hasta CALENT cierres del tramo nuevo)
        t = tramos(c)
        partes_s, partes_v, partes_n = [], [], []
        for s in np.unique(t):
            sub = c[t == s]
            r = sub.pct_change()
            partes_s.append(np.sign(sub / sub.shift(duracion) - 1.0))
            partes_v.append(r.rolling(N_VOL).std() * np.sqrt(252))
            n_s = pd.Series(np.arange(1, len(sub) + 1), index=sub.index)
            if s != t[-1]:
                n_s.iloc[-1] = 0
            partes_n.append(n_s)
        una = len(partes_s) == 1
        senal[k] = (partes_s[0] if una else pd.concat(partes_s)).reindex(idx).ffill()
        vol[k] = (partes_v[0] if una else pd.concat(partes_v)).reindex(idx).ffill()
        n = (partes_n[0] if una else pd.concat(partes_n)).reindex(idx).ffill().fillna(0)
        activo[k] = (n >= CALENT)
        ultimos = c.index[np.r_[np.diff(t) > 0, False]]
        cierre[k] = pd.Series(idx.isin(ultimos), index=idx)
    senal, vol, activo = pd.DataFrame(senal), pd.DataFrame(vol), pd.DataFrame(activo)
    cierre = pd.DataFrame(cierre)
    activo &= vol.gt(0) & senal.notna()
    cls = pd.Series(clases)
    n_clase = pd.DataFrame({c: activo[cls.index[cls == c]].sum(axis=1) for c in cls.unique()})
    clases_activas = (n_clase > 0).sum(axis=1).replace(0, np.nan)
    w = pd.DataFrame(0.0, index=idx, columns=precios.columns)
    for k in precios.columns:
        parte = np.minimum(1.0 / (clases_activas * n_clase[clases[k]].replace(0, np.nan)), TOPE_RIESGO)
        apal = np.minimum(VOL_OBJ / vol[k], TOPE_APAL)
        w[k] = np.where(activo[k], senal[k] * apal * parte, 0.0)
    return w.fillna(0.0), activo, precios, cierre


# ---------------------------------------------------------------- tamanos minimos (enmienda 1, pregunta 2)

# Deriv MT5, columna "Tamano minimo" x "Tamano del contrato" (deriv.com/es/trading-specifications, 2026-11-22).
# Divisas: con version micro, 0,1 lote x 1.000 = 100 unidades de la moneda base; sin micro, 0,01 x 100.000 = 1.000.
DERIV_MICRO = {'AUDCAD', 'AUDCHF', 'AUDJPY', 'AUDNZD', 'AUDUSD', 'EURAUD', 'EURCAD', 'EURCHF', 'EURGBP', 'EURJPY',
               'EURNZD', 'EURUSD', 'GBPCHF', 'GBPJPY', 'GBPUSD', 'NZDUSD', 'USDCAD', 'USDCHF', 'USDJPY'}
DERIV_MIN_OTROS = {'Australia 200': 0.1, 'Europe 50': 0.1, 'France 40': 0.1, 'Germany 40': 0.1, 'Hong Kong 50': 0.1,
                   'Japan 225': 10.0, 'Netherlands 25': 0.1, 'Spain 35': 1.0, 'Swiss 20': 0.1, 'UK 100': 0.1,
                   'US Tech 100': 0.1, 'US SP 500': 0.1, 'Wall Street 30': 0.1,
                   'XAUUSD': 0.1, 'XAGUSD': 0.5,              # versiones micro: 1 x 0,1 oz y 50 x 0,01 oz
                   'UK Brent Oil': 1.0, 'XCUUSD': 1.0, 'Cocoa': 1.0, 'Cotton': 1.0}
# IBKR: CFD de indices, minimo 1 unidad x nivel del indice (interactivebrokers.ie/en/pricing/commissions-cfd-index.php);
# divisas sin minimo, pero las ordenes < USD 25.000 son lote impar y se ejecutan 1-2 pb peor (FAQ "Odd lot fx
# transactions" de IBKR): se cobran 2 pb extra por lado. ETF: fracciones de accion, sin minimo.
IB_LOTE_IMPAR_USD = 25000.0
IB_LOTE_IMPAR_COSTO = 0.0002
HKD_POR_USD = 7.8                       # HKD fijo al dolar (banda 7,75-7,85)
DIVISA_INDICE = lambda cod: cod[-3:]    # AUSIDXAUD -> AUD; USATECHIDXUSD -> USD


def minimos(nombre, broker):
    """Unidades minimas por mercado (moneda base en divisas; unidades de indice, onzas, acciones o contratos)."""
    out = {}
    for cod, clase, _, deriv in UNIVERSOS[nombre]:
        if broker == 'deriv':
            if es_divisa(cod) and cod[:3] not in METALES:
                out[cod] = 100.0 if deriv in DERIV_MICRO else 1000.0
            elif cod.endswith('USUSD'):
                out[cod] = 1.0
            else:
                out[cod] = DERIV_MIN_OTROS[deriv]
        else:  # ibkr
            out[cod] = 1.0 if (clase == 'acciones' and not cod.endswith('USUSD')) else None
    return out


def escala_etf(c):
    """Regla mecanica declarada antes de ver B (la escala de precio de Dukascopy no esta documentada): si el
    ultimo cierre ya esta entre 5 y 2.000 USD no se toca; si no, se aplica la potencia de 10 de menor exponente
    absoluto que lo meta en el rango. Solo afecta al minimo de 1 accion en Deriv."""
    x = float(c.iloc[-1])
    k = 1.0
    while x * k > 2000:
        k /= 10
    while x * k < 5:
        k *= 10
    return k


def valor_unidad(precios, cierres_extra):
    """Valor en USD de una unidad minima de cada mercado, por fecha."""
    idx = precios.index
    def serie_conv(cod):
        s = precios[cod] if cod in precios.columns else cierres_extra.get(cod)
        if s is None:
            return None
        return s.reindex(idx).ffill().bfill()
    conv = {'USD': pd.Series(1.0, index=idx), 'HKD': pd.Series(1.0 / HKD_POR_USD, index=idx),
            'USX': pd.Series(0.01, index=idx)}
    for m, cod, inv in (('EUR', 'EURUSD', False), ('GBP', 'GBPUSD', False), ('AUD', 'AUDUSD', False),
                        ('NZD', 'NZDUSD', False), ('JPY', 'USDJPY', True), ('CHF', 'USDCHF', True),
                        ('CAD', 'USDCAD', True)):
        s = serie_conv(cod)
        if s is not None:
            conv[m] = 1.0 / s if inv else s
    vu = pd.DataFrame(index=idx)
    for k in precios.columns:
        if es_divisa(k) and k[:3] not in METALES:
            vu[k] = conv[k[:3]]
        elif k.endswith('USUSD'):
            vu[k] = precios[k] * escala_etf(precios[k].dropna())
        elif es_divisa(k):                       # metales (XAUUSD, XAGUSD): USD por onza
            vu[k] = precios[k]
        else:
            vu[k] = precios[k] * conv[DIVISA_INDICE(k)]
    return vu


def redondear(o, vu_fila, minimo, capital):
    """Peso realizable: unidades = |w| x capital / valor_unidad, redondeadas hacia abajo a multiplos del minimo."""
    if minimo is None or not np.isfinite(vu_fila) or vu_fila <= 0:
        return o
    unidades = np.floor(abs(o) * capital / vu_fila / minimo) * minimo
    return np.sign(o) * unidades * vu_fila / capital


def rebalanceo(w_obj, banda=BANDA, minimo=None, vu=None, capital=CAPITAL, cierre=None):
    """Pesos mantenidos: se revisan el ultimo dia habil de cada semana; un mercado solo se opera si cambia
    el signo, si el objetivo es 0 o si |objetivo - actual| > banda x |objetivo|. Con tamanos minimos, la
    posicion nueva se redondea hacia abajo a multiplos del minimo (0 si no alcanza). cierre (enmienda 2):
    en el ultimo cierre valido antes de un corte la posicion se cierra ese mismo dia, sea o no fin de semana."""
    semana = w_obj.index.to_period('W-SUN')
    es_ultimo = (pd.Series(w_obj.index, index=w_obj.index).groupby(semana).transform('max') == w_obj.index).values
    obj = w_obj.values
    cols = list(w_obj.columns)
    forzar = cierre.reindex(index=w_obj.index, columns=cols).fillna(False).values.astype(bool) if cierre is not None else None
    out = np.zeros_like(obj)
    actual = np.zeros(obj.shape[1])
    meta = np.zeros(obj.shape[1])          # objetivo vigente (para la banda)
    for i in range(len(obj)):
        if es_ultimo[i]:
            o = obj[i]
            cambia = (np.sign(o) != np.sign(meta)) | (o == 0) | (np.abs(o - meta) > banda * np.abs(o))
            for j in np.nonzero(cambia)[0]:
                meta[j] = o[j]
                if minimo is not None and vu is not None:
                    actual[j] = redondear(o[j], vu.iat[i, j], minimo[cols[j]], capital)
                else:
                    actual[j] = o[j]
        if forzar is not None and forzar[i].any():
            actual[forzar[i]] = 0.0
            meta[forzar[i]] = 0.0
        out[i] = actual
    return pd.DataFrame(out, index=w_obj.index, columns=w_obj.columns)


def _spread_tasa_ib(m, tiene, T):
    """Diferencial de IBKR sobre la tasa de referencia, como fraccion anual: al tener la moneda se cobra
    BM - e (se pierde e); al deberla se paga BM + p."""
    e, p = IB_SPREAD_TASA[m]
    if tiene:
        if e is None:                      # CNH: IBKR paga 0,50% fijo -> se pierde (tasa - 0,5)
            return np.maximum(T[m] - 0.5, 0.0) / 100.0
        return e / 100.0
    return p / 100.0


def resultado(w_obj, precios, costos, retraso=1, x_costo=1.0, banda=BANDA, capital=CAPITAL, minimo=None, vu=None,
              a_escala=False, cierre=None):
    """Retornos diarios de la cartera por escenario. retraso=1: el peso decidido al cierre t gana el
    retorno de t a t+1 (como E2). 2 = un dia extra; 0 = trampa (mira el futuro)."""
    medio, swaps, ib = costos['medio'], costos['swaps'], costos['ib']
    r = precios.pct_change().fillna(0.0)
    w = rebalanceo(w_obj, banda, minimo, vu, capital, cierre).shift(retraso).fillna(0.0)
    idx = w.index
    dias = pd.Series(idx, index=idx).diff().dt.days.fillna(1).clip(lower=1)
    f = dias / 365.0
    T = tasas(idx)
    precio_mkt = w * r
    giro = w.diff().abs().fillna(w.abs())
    c_spread = (giro * pd.Series(medio)).sum(axis=1)
    carry_mkt = pd.DataFrame(0.0, index=idx, columns=w.columns)   # w x (tasa base - tasa cotizada)
    swap_deriv = pd.Series(0.0, index=idx)                         # tabla de Deriv (ya incluye su carry)
    rec_ib = pd.Series(0.0, index=idx)
    com_ib = pd.Series(0.0, index=idx)
    ajuste_ib = pd.Series(0.0, index=idx)
    for k in w.columns:
        wk = w[k]
        if es_divisa(k):
            b, q = monedas(k)
            carry_mkt[k] = wk * ((T[b] - T[q]).fillna(0.0) / 100.0) * f
        if swaps.get(k) is None:
            s_k = wk.abs() * RECARGO * f
        else:
            largo, corto = swaps[k]
            s_k = -wk.abs() * (np.where(wk > 0, largo, corto) / precios[k]) * dias
        s_k = s_k.where(wk != 0, 0.0)          # enmienda 3: sin posicion no hay swap (precio NaN antes del primer cierre)
        swap_deriv += s_k
        if ib[k] == 'fx':
            b, q = monedas(k)
            largo_rec = _spread_tasa_ib(b, True, T) + _spread_tasa_ib(q, False, T)
            corto_rec = _spread_tasa_ib(q, True, T) + _spread_tasa_ib(b, False, T)
            rec_ib += wk.abs() * np.where(wk > 0, largo_rec, corto_rec) * f
            dw = giro[k]
            minimo_fx = 0.0 if a_escala else IB_MINIMO_FX / capital
            com_ib += np.where(dw > 0, np.maximum(dw * IB_COMISION_FX, minimo_fx), 0.0)
            impar = (dw > 0) if a_escala else ((dw > 0) & (dw * capital < IB_LOTE_IMPAR_USD))
            com_ib += np.where(impar, dw * IB_LOTE_IMPAR_COSTO, 0.0)
        elif ib[k] == 'etf':
            rec_ib += wk.abs() * np.where(wk > 0, IB_RECARGO_ETF[0], IB_RECARGO_ETF[1]) * f
            dw = giro[k]
            minimo_etf = 0.0 if a_escala else IB_MINIMO_ETF / capital
            com_ib += np.where(dw > 0, np.minimum(np.maximum(dw * IB_COMISION_ETF, minimo_etf), dw * 0.01), 0.0)
        elif ib[k] == 'cfd':
            rec_ib += wk.abs() * IB_RECARGO_CFD * f
            com_ib += giro[k] * costos['com'][k]
        else:
            # IBKR no lista la divisa: en el principal se usa la tabla de Deriv en lugar del carry BIS
            ajuste_ib += s_k + carry_mkt[k]
    carry = carry_mkt.sum(axis=1)
    precio = precio_mkt.sum(axis=1)
    x = x_costo
    return {
        'principal': precio + carry - x * (c_spread + com_ib + rec_ib) - ajuste_ib,
        'deriv_cfd': precio + carry - x * (c_spread + w.abs().sum(axis=1) * RECARGO * f),
        'swaps_hoy': precio - x * c_spread - swap_deriv,
        'bruto': precio + carry - x * c_spread,
        'solo_precio': precio,
        'carry': carry,
        'por_mercado': precio_mkt + carry_mkt,
        'giro': giro.sum(axis=1),
        'nocional': w.abs().sum(axis=1),
        'operaciones': (giro > 0).sum(axis=1),
        'pesos': w,
    }


def exigir_sin_nan(x, nombre):
    """Enmienda 3: guarda. Una serie de escenario con NaN dentro de la evaluacion aborta la corrida."""
    x = pd.Series(x)
    if x.isna().any():
        malos = x.index[x.isna()]
        sys.exit('ERROR: el escenario %s tiene %d dias NaN dentro de la evaluacion (%s a %s); no se imprime veredicto.'
                 % (nombre, len(malos), malos[0], malos[-1]))


def t_nw(x, rezagos=NW_REZAGOS):
    x = np.asarray(x, dtype=float)
    n = len(x)
    if n < 30:
        return float('nan')
    m = x.mean()
    e = x - m
    s = e @ e / n
    for j in range(1, rezagos + 1):
        s += 2 * (1 - j / (rezagos + 1)) * (e[j:] @ e[:-j]) / n
    return float(m / np.sqrt(s / n)) if s > 0 else float('nan')


def metricas(x):
    x = pd.Series(x)
    eq = (1 + x).cumprod()
    caida = float((1 - eq / eq.cummax()).max())
    return {'dias': int(len(x)), 'anual': float(x.mean() * 252), 'vol': float(x.std() * np.sqrt(252)),
            'sharpe': float(x.mean() / x.std() * np.sqrt(252)) if x.std() > 0 else float('nan'),
            't_nw': t_nw(x), 'caida_max': caida}


def ventana(activo, nombre):
    n = activo.sum(axis=1)
    ok = n[n >= MIN_ACTIVOS[nombre]]
    if ok.empty:
        raise RuntimeError('nunca hay %d mercados activos' % MIN_ACTIVOS[nombre])
    return ok.index[0]


CONVERSION = ('EURUSD', 'GBPUSD', 'USDJPY', 'USDCHF')     # de A, solo para pasar unidades minimas a USD en B


def extra_conversion(cierres):
    out = {}
    for cod in CONVERSION:
        if cod not in cierres:
            out[cod] = serie_bd(cod, '2003-05-04')[0] if FUENTE_BD else serie(cod, '2003-05-04')[0]
    return out


def correr_cartera(cierres, clases, costos, nombre, duracion=PRINCIPAL, retraso=1, x_costo=1.0, inicio=None,
                   capital=CAPITAL, modo=None, extra=None, a_escala=False):
    """modo: None (sin tamanos minimos), 'ibkr' o 'deriv' (aplica los minimos de ese broker con el capital dado)."""
    w, activo, precios, cierre = objetivos(cierres, clases, duracion)
    mn = vu = None
    if modo is not None:
        mn = minimos(nombre, modo)
        vu = valor_unidad(precios, extra if extra is not None else extra_conversion(cierres))
    res = resultado(w, precios, costos, retraso=retraso, x_costo=x_costo, capital=capital, minimo=mn, vu=vu,
                    a_escala=a_escala, cierre=cierre if cierre.values.any() else None)
    ini = inicio if inicio is not None else ventana(activo, nombre)
    corte = {k: (v[v.index >= ini] if isinstance(v, (pd.Series, pd.DataFrame)) else v) for k, v in res.items()}
    return corte, ini, activo


# ---------------------------------------------------------------- permutacion

def permutar(cierres, rng):
    """Baraja los retornos de cada mercado despues de su calentamiento con una clave comun por fecha."""
    fechas = pd.DatetimeIndex(sorted(set().union(*[set(c.index) for c in cierres.values()])))
    clave = pd.Series(rng.random(len(fechas)), index=fechas)
    nuevo = {}
    for k, c_todo in cierres.items():
        # enmienda 2: se baraja dentro de cada tramo (despues de su propio calentamiento)
        t = tramos(c_todo)
        partes = []
        for s in np.unique(t):
            c = c_todo[t == s] if t[-1] > 0 else c_todo
            r = c.pct_change().values
            if len(c) <= CALENT + 1:
                partes.append(c)
                continue
            cola = np.arange(CALENT, len(c))
            orden = cola[np.argsort(clave.reindex(c.index[cola]).values, kind='stable')]
            r2 = r.copy()
            r2[cola] = r[orden]
            p = np.empty(len(c))
            p[:CALENT] = c.values[:CALENT]
            p[CALENT - 1:] = c.values[CALENT - 1] * np.cumprod(np.r_[1.0, 1.0 + r2[CALENT:]])
            partes.append(pd.Series(p, index=c.index))
        nuevo[k] = partes[0] if len(partes) == 1 else pd.concat(partes)
    return nuevo


def ceros(res, sec):
    """Fraccion de dias-mercado con posicion teorica distinta de 0 que el tamano minimo deja en 0."""
    teo, real = sec['pesos'] != 0, res['pesos'] == 0
    n = int(teo.values.sum())
    return float((teo & real).values.sum()) / n if n else 0.0


def t_max(cierres, clases, costos, nombre, inicio, extra=None):
    ts = {}
    for d in DURACIONES:
        res, _, _ = correr_cartera(cierres, clases, costos, nombre, duracion=d, inicio=inicio, modo='ibkr', extra=extra)
        exigir_sin_nan(res['principal'], 'principal regla %d' % d)
        ts[d] = t_nw(res['principal'])
    return max(ts.values()), ts


# ---------------------------------------------------------------- comandos

def sin_cmd(cierres, clases, costos):
    """Enmienda 1: las series .CMD (sin ajustar en el cambio de contrato) salen del principal."""
    keep = [k for k in cierres if 'CMD' not in k]
    return ({k: cierres[k] for k in keep}, {k: clases[k] for k in keep},
            {kk: {k: v for k, v in d.items() if k in keep} for kk, d in costos.items()})


def exigir_b(nombre, args):
    if nombre == 'B' and not getattr(args, 'enmienda1_aprobada', False):
        sys.exit('El universo B solo se usa despues de la enmienda 1 del plan y del visto bueno del lider '
                 '(--enmienda1-aprobada).')


def cmd_hora(args):
    """Compara la vela diaria del datafeed con la 1d de la BD (00:00-24:00 UTC) en EURUSD (universo A)."""
    sys.path.insert(0, AQUI)
    import velas_bd
    c, _ = serie('EURUSD', '2020-01-01')
    bd = velas_bd.velas('Dukascopy', 'frxEURUSD', '1d', desde='2020-01-01')
    bd['fecha'] = pd.to_datetime(bd['epoch'], unit='s', utc=True).dt.tz_localize(None).dt.normalize()
    bd = bd.set_index('fecha')['c']
    comun = c.index.intersection(bd.index)
    for anio in (2020, 2024):
        df = leer_anio('EURUSD', anio)
        hh = pd.to_datetime(df['epoch'], unit='s', utc=True).dt.strftime('%H:%M').value_counts().head(3)
        print('%d horas de inicio de la vela diaria del datafeed (UTC): %s' % (anio, dict(hh)))
    for nombre, ref in (('mismo dia', bd), ('dia anterior', bd.shift(1)), ('dia siguiente', bd.shift(-1))):
        dif = (c.reindex(comun) / ref.reindex(comun) - 1).abs()
        print('cierre datafeed vs cierre BD 00-24 UTC del %-13s: dias %d, mediana %.6f, p95 %.6f'
              % (nombre, len(comun), dif.median(), dif.quantile(0.95)))


def cmd_bajar(args):
    exigir_b(args.universo, args)
    faltan = bajar_universo(args.universo)
    log('fin; archivos con error: %d' % faltan)


def cmd_fuga(args):
    exigir_b(args.universo, args)
    cierres, clases, _ = cargar(args.universo)
    cierres, clases, costos = sin_cmd(cierres, clases, costos_por_mercado(args.universo))
    extra = extra_conversion(cierres)
    _, ini, _ = correr_cartera(cierres, clases, costos, args.universo, modo='ibkr', extra=extra)
    print('FUGA DE FUTURO, universo %s, regla %d, escenario principal (IBKR), desde %s' % (args.universo, PRINCIPAL, ini.date()))
    for nombre, ret in (('normal (retraso 1)', 1), ('un dia extra (retraso 2)', 2), ('trampa (retraso 0)', 0)):
        res, _, _ = correr_cartera(cierres, clases, costos, args.universo, retraso=ret, inicio=ini, modo='ibkr', extra=extra)
        exigir_sin_nan(res['principal'], 'principal (%s)' % nombre)
        m = metricas(res['principal'])
        print('  %-26s t %6.2f  sharpe %5.2f  anual %6.2f%%' % (nombre, m['t_nw'], m['sharpe'], 100 * m['anual']))


def nivel_t(t):
    return 'EVIDENCIA FUERTE' if t >= 3.0 else ('CANDIDATA' if t >= 2.0 else 'SIN EVIDENCIA')


def cmd_correr(args):
    exigir_b(args.universo, args)
    t0 = time.time()
    nombre = args.universo
    info = {}
    todos, clases_t, avisos = cargar(nombre, info)
    costos_t = costos_por_mercado(nombre)
    cierres, clases, costos = sin_cmd(todos, clases_t, costos_t)
    extra = extra_conversion(todos)
    # Pregunta 1: IBKR Pro, USD 100.000, con sus tamanos minimos
    res, ini, activo = correr_cartera(cierres, clases, costos, nombre, modo='ibkr', extra=extra)
    # escenarios secundarios (sin tamanos minimos: miden la prima, no la cuenta)
    sec, _, _ = correr_cartera(cierres, clases, costos, nombre, inicio=ini)
    out = {'universo': nombre, 'inicio': str(ini.date()), 'fin': FIN, 'avisos_datos': avisos, 'enmienda2': info,
           'mercados': {k: {'desde': str(c.index[0].date()), 'cierres': int(len(c)), 'clase': clases[k]}
                        for k, c in cierres.items()}}
    lineas = ['TSMOM DIVERSIFICADO - plan pre-registrado en docs/validacion/tsmom_plan.md (con enmienda 1)',
              'Universo %s principal: %d mercados (sin .CMD); evaluacion %s a %s (desde el primer dia con >= %d activos).'
              % (nombre, len(cierres), ini.date(), FIN, MIN_ACTIVOS[nombre])]
    lineas.append('Enmienda 2: splits de ETF ajustados: %s' % (
        {k: [(x['fecha'], x['k']) for x in v['splits']] for k, v in info.items() if v['splits']} or 'ninguno'))
    lineas.append('Enmienda 2: cortes por hueco de mas de %d dias (ultimo cierre -> vuelve): %s' % (CORTE_DIAS, {
        k: ['%s->%s' % (x['ultimo'], x['vuelve']) for x in v['cortes']] for k, v in info.items() if v['cortes']} or 'ninguno'))
    lineas.append('')
    escen = {}

    def linea(clave, etiqueta, serie_ret):
        exigir_sin_nan(serie_ret, clave)
        escen[clave] = m = metricas(serie_ret)
        lineas.append('  %-40s t %6.2f | sharpe %5.2f | anual %6.2f%% | vol %5.2f%% | caida %5.1f%%'
                      % (etiqueta, m['t_nw'], m['sharpe'], 100 * m['anual'], 100 * m['vol'], 100 * m['caida_max']))

    lineas.append('PREGUNTA 1 (existe la prima neta a escala):')
    linea('principal', 'IBKR Pro USD 100.000 (veredicto)', res['principal'])
    x2, _, _ = correr_cartera(cierres, clases, costos, nombre, x_costo=2.0, inicio=ini, modo='ibkr', extra=extra)
    linea('costos_x2', 'IBKR USD 100.000, costos x2', x2['principal'])
    esc, _, _ = correr_cartera(cierres, clases, costos, nombre, inicio=ini, a_escala=True)
    linea('ibkr_a_escala', 'IBKR a escala (informativo, sin minimos)', esc['principal'])
    etf = [k for k in cierres if k.endswith('USUSD')]
    if etf:
        out['escala_etf'] = {k: escala_etf(cierres[k]) for k in etf}
        lineas.append('    factor de escala aplicado a cada ETF: %s' % out['escala_etf'])
    lineas.append('PREGUNTA 2 (la puede capturar la cuenta del usuario, USD 100):')
    ib1, _, _ = correr_cartera(cierres, clases, costos, nombre, inicio=ini, capital=CAPITAL_MINI, modo='ibkr', extra=extra)
    linea('ibkr_100', 'IBKR Pro USD 100', ib1['principal'])
    de1, _, _ = correr_cartera(cierres, clases, costos, nombre, inicio=ini, capital=CAPITAL_MINI, modo='deriv', extra=extra)
    linea('deriv_100', 'Deriv CFD USD 100', de1['deriv_cfd'])
    for clave, r_ in (('ibkr_100', ib1), ('deriv_100', de1)):
        operables = int((r_['pesos'] != 0).any().sum())
        escen[clave]['mercados_operables'] = operables
        lineas.append('    %s: mercados con alguna posicion distinta de 0 en todo el periodo: %d de %d; '
                      'posiciones teoricas que el minimo deja en 0: %.0f%%' % (clave, operables, len(cierres), 100 * ceros(r_, sec)))
    lineas.append('INFORMATIVO (USD 10.000):')
    ib10, _, _ = correr_cartera(cierres, clases, costos, nombre, inicio=ini, capital=CAPITAL_CHICO, modo='ibkr', extra=extra)
    linea('ibkr_10k', 'IBKR Pro USD 10.000', ib10['principal'])
    de10, _, _ = correr_cartera(cierres, clases, costos, nombre, inicio=ini, capital=CAPITAL_CHICO, modo='deriv', extra=extra)
    linea('deriv_10k', 'Deriv CFD USD 10.000', de10['deriv_cfd'])
    lineas.append('SECUNDARIOS (informativos, sin tamanos minimos):')
    linea('deriv_cfd', 'Deriv CFD, recargo 2%', sec['deriv_cfd'])
    linea('swaps_hoy', 'tabla de swaps Deriv (sin carry BIS)', sec['swaps_hoy'])
    linea('bruto', 'bruto (spread + carry)', sec['bruto'])
    linea('solo_precio', 'solo precio', sec['solo_precio'])
    if len(todos) > len(cierres):
        cm, _, _ = correr_cartera(todos, clases_t, costos_t, nombre, inicio=ini, modo='ibkr', extra=extra)
        linea('con_materias_sesgado', 'IBKR CON materias .CMD (SESGADO)', cm['principal'])
    lineas.append('  carry medio anual %.2f%%; operaciones por semana %.1f; nocional bruto medio %.2f; '
                  'posiciones en 0 por minimo: IBKR 100k %.0f%%, IBKR 10k %.0f%%, Deriv 10k %.0f%% de las semanas-mercado activas'
                  % (100 * res['carry'].mean() * 252, res['operaciones'].sum() / max(1, len(res['operaciones']) / 5),
                     res['nocional'].mean(), *[100 * x for x in (
                         ceros(res, sec), ceros(ib10, sec), ceros(de10, sec))]))
    pnl = res['por_mercado'].sum()
    total = float(pnl.sum())
    mayor = pnl.abs().idxmax()
    parte = float(pnl[mayor] / total) if total != 0 else float('nan')
    lineas.append('  P&L (precio + carry) del principal por mercado: mayor %s = %.1f%% del total' % (mayor, 100 * parte))
    out.update({'escenarios': escen, 'pnl_por_mercado': {k: float(v) for k, v in pnl.items()},
                'mayor_mercado': mayor, 'parte_mayor': parte,
                'activos_medios': float(activo[activo.index >= ini].sum(axis=1).mean())})
    if args.perm:
        rng = np.random.default_rng(1)
        t_real = escen['principal']['t_nw']
        _, ts_real = t_max(cierres, clases, costos, nombre, ini, extra)
        maximos = []
        for i in range(args.perm):
            m, _ = t_max(permutar(cierres, rng), clases, costos, nombre, ini, extra)
            maximos.append(m)
            if (i + 1) % 50 == 0:
                log('permutacion %d' % (i + 1))
        maximos = np.array(maximos)
        p = (1 + int((maximos >= t_real).sum())) / (1 + len(maximos))
        out['permutacion'] = {'n': len(maximos), 't_real_252': t_real, 't_reales_rejilla': ts_real,
                              'p': p, 'p95': float(np.percentile(maximos, 95)), 'p99': float(np.percentile(maximos, 99)),
                              'maximos': maximos.round(4).tolist()}
        lineas.append('  permutacion (%d): t real %.2f | maximos p95 %.2f p99 %.2f | p = %.4f'
                      % (len(maximos), t_real, out['permutacion']['p95'], out['permutacion']['p99'], p))
    t = escen['principal']['t_nw']
    pp = out.get('permutacion', {}).get('p')
    ok_extra = escen['costos_x2']['sharpe'] > 0 and abs(parte) <= 0.5
    if t < 2.0:
        nivel = 'SIN EVIDENCIA'
    elif pp is None:
        nivel = 'PENDIENTE DE PERMUTACION'
    elif t >= 3.0 and pp < 0.01 and ok_extra:
        nivel = 'EVIDENCIA FUERTE'
    elif pp < 0.05 and ok_extra:
        nivel = 'CANDIDATA'
    else:
        nivel = 'SIN EVIDENCIA'
    # un escenario de cuenta chica solo cuenta si deja operables al menos tantos mercados como el minimo de la evaluacion
    n2 = {k: (nivel_t(escen[k]['t_nw']) if escen[k]['mercados_operables'] >= MIN_ACTIVOS[nombre]
              else 'NO OPERABLE (%d mercados)' % escen[k]['mercados_operables']) for k in ('ibkr_100', 'deriv_100')}
    pasa2 = any(v in ('CANDIDATA', 'EVIDENCIA FUERTE') for v in n2.values())
    if nivel in ('CANDIDATA', 'EVIDENCIA FUERTE'):
        usuario = 'CANDIDATA PARA SU CUENTA (USD 100)' if pasa2 else 'LA PRIMA EXISTE PERO NO ES CAPTURABLE CON USD 100'
    else:
        usuario = 'SIN EVIDENCIA'
    out.update({'nivel': nivel, 'nivel_pregunta2': n2, 'veredicto_usuario': usuario})
    lineas += ['', '== PREGUNTA 1 (regla 252, IBKR USD 100.000): %s' % nivel,
               '== PREGUNTA 2 (USD 100): IBKR %s; Deriv %s' % (n2['ibkr_100'], n2['deriv_100']),
               '== VEREDICTO PARA EL USUARIO: %s' % usuario,
               '   (solo vale para el universo B; en A es informativo)', '',
               'Avisos de datos: %s' % (avisos or 'ninguno'),
               'Avisos en tramos que nunca se activan (sin efecto): %s' % (
                   {k: v['avisos_tramos_inactivos'] for k, v in info.items() if v['avisos_tramos_inactivos']} or 'ninguno'),
               'Tiempo: %.1f min' % ((time.time() - t0) / 60)]
    texto = '\n'.join(lineas)
    print(texto)
    if args.salida:
        with open(args.salida + '.txt', 'w', encoding='utf-8') as f:
            f.write(texto + '\n')
        with open(args.salida + '.json', 'w', encoding='utf-8') as f:
            json.dump(out, f, indent=1, default=str)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('comando', choices=['bajar', 'correr', 'fuga', 'hora'])
    ap.add_argument('universo', choices=['A', 'B'])
    ap.add_argument('--perm', type=int, default=0)
    ap.add_argument('--salida')
    ap.add_argument('--enmienda1-aprobada', action='store_true')
    ap.add_argument('--fuente-bd', action='store_true', help='solo universo A, para depurar')
    a = ap.parse_args()
    global FUENTE_BD
    FUENTE_BD = a.fuente_bd and a.universo == 'A'
    {'bajar': cmd_bajar, 'correr': cmd_correr, 'fuga': cmd_fuga, 'hora': cmd_hora}[a.comando](a)


if __name__ == '__main__':
    main()
