"""Experimento del brief: modelo-filtro sobre estrategias base en M15 (oro, Nasdaq, USDJPY).

Plan pre-registrado: docs/validacion/brief_modelo_filtro_plan.md (commit 3646261). Las reglas
(estrategias, salida, costos, variables, modelo y criterios) son las del plan y no se ajustan.
Datos: D:\\traiding_ml\\brief\\<SIMBOLO>_<ANO>.csv.gz (scripts/ml/brief_datos.py).

Version H1 (enmienda 1 del plan): velas H1 bid de la base + spread fijo por activo y ano medido en junio
(D:\\traiding_ml\\brief\\spread_h1.json). Version M15: el plan original con minutos bid/ask.

Uso (entorno de D:):
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/brief_experimento.py h1 docs/validacion/brief_modelo_filtro_h1_2026-11-18
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/brief_experimento.py m15 docs/validacion/brief_modelo_filtro_m15_AAAA-MM-DD
"""
import datetime as dt
import glob
import json
import math
import os
import sys
import time

import lightgbm as lgb
import numpy as np
import pandas as pd

CARPETA = r'D:\traiding_ml\brief'
ACTIVOS = [('Oro', 'XAUUSD'), ('Nasdaq', 'USATECHIDXUSD'), ('USDJPY', 'USDJPY')]
ESTRES = (2008, 2020, 2022)
ULTIMOS = range(2020, 2026)
N_AZAR = 1000
PCT_AZAR, PCT_BONF = 95.0, 100 - 5.0 / 3
VELA = 900            # segundos de la vela de senal (900 = M15; 3600 = H1, enmienda 1)
HORIZONTE = 16        # velas de la salida por tiempo (4 horas)
SIMBOLO_BD = {'XAUUSD': 'frxXAUUSD', 'USDJPY': 'frxUSDJPY', 'USATECHIDXUSD': 'OTC_NDX'}
PARAMS = dict(objective='binary', n_estimators=200, learning_rate=0.03, num_leaves=15, min_child_samples=50,
              subsample=0.8, subsample_freq=1, colsample_bytree=0.8, random_state=1, verbose=-1)
VARIABLES = ['d_ema200_h1', 'd_ema50_d1', 'sar_h1_lado', 'rsi14', 'wpr14', 'adx14', 'di_dif', 'atr_pct100', 'bb_ancho',
             'd_max_dia', 'd_min_dia', 'pos_canal10', 'interior', 'hora', 'dia', 'min_sesion', 'adx_h1_pct100',
             'volreal_pct100', 'spread_atr', 'dir', 'familia']


def log(msg):
    print(time.strftime('%H:%M:%S'), msg, flush=True)


def ep(a, m=1, d=1):
    return int(dt.datetime(a, m, d, tzinfo=dt.timezone.utc).timestamp())


# ------------------------------------------------------------------ indicadores

def wilder(x, n):
    return x.ewm(alpha=1.0 / n, adjust=False).mean()


def rsi(c, n=14):
    d = c.diff()
    sube, baja = wilder(d.clip(lower=0), n), wilder((-d).clip(lower=0), n)
    return 100 - 100 / (1 + sube / baja.replace(0, np.nan))


def atr_adx(h, l, c, n=14):
    pc = c.shift(1)
    tr = pd.concat([h - l, (h - pc).abs(), (l - pc).abs()], axis=1).max(axis=1)
    up, dn = h.diff(), -l.diff()
    pdm = pd.Series(np.where((up > dn) & (up > 0), up, 0.0), index=c.index)
    ndm = pd.Series(np.where((dn > up) & (dn > 0), dn, 0.0), index=c.index)
    atr = wilder(tr, n)
    pdi, ndi = 100 * wilder(pdm, n) / atr, 100 * wilder(ndm, n) / atr
    dx = 100 * (pdi - ndi).abs() / (pdi + ndi).replace(0, np.nan)
    return atr, wilder(dx, n), pdi, ndi


def sar(h, l, paso=0.02, maximo=0.2):
    h, l = h.to_numpy(), l.to_numpy()
    n = len(h)
    out = np.full(n, np.nan)
    if n < 2:
        return out
    alcista, ep_, af, s = True, h[0], paso, l[0]
    for i in range(1, n):
        s = s + af * (ep_ - s)
        if alcista:
            s = min(s, l[i - 1], l[i - 2] if i > 1 else l[i - 1])
            if l[i] < s:
                alcista, s, ep_, af = False, ep_, l[i], paso
            elif h[i] > ep_:
                ep_, af = h[i], min(maximo, af + paso)
        else:
            s = max(s, h[i - 1], h[i - 2] if i > 1 else h[i - 1])
            if h[i] > s:
                alcista, s, ep_, af = True, ep_, h[i], paso
            elif l[i] < ep_:
                ep_, af = l[i], min(maximo, af + paso)
        out[i] = s
    return out


def pct_rodante(x, ventana):
    return x.rolling(ventana, min_periods=ventana // 2).rank(pct=True)


# ------------------------------------------------------------------ datos y variables

def cargar(sym):
    if VELA == 3600:
        return cargar_h1(sym)
    partes = [pd.read_csv(f) for f in sorted(glob.glob(os.path.join(CARPETA, '%s_*.csv.gz' % sym)))]
    df = pd.concat(partes).drop_duplicates('epoch').sort_values('epoch').reset_index(drop=True)
    return df[df['epoch'] < ep(2026, 11)].reset_index(drop=True)


def cargar_h1(sym):
    """Enmienda 1: H1 bid de la base; ask = bid + spread fijo del ano (mediana de junio)."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import velas_bd
    v = velas_bd.velas('Dukascopy', SIMBOLO_BD[sym], '1h', hasta='2026-11-01')
    df = v.rename(columns={'o': 'bo', 'h': 'bh', 'l': 'bl', 'c': 'bc'})
    sp = json.load(open(os.path.join(CARPETA, 'spread_h1.json')))
    anios = pd.to_datetime(df['epoch'], unit='s', utc=True).dt.year
    tabla = {int(k.rsplit('_', 1)[1]): x['spread'] for k, x in sp.items() if k.rsplit('_', 1)[0] == sym}
    if not tabla:
        raise RuntimeError('sin spread medido para ' + sym)
    conocidos = sorted(tabla)
    spread = anios.map(lambda a: tabla.get(a, tabla[min(conocidos, key=lambda k: abs(k - a))])).to_numpy()
    for k in 'ohlc':
        df['a' + k] = df['b' + k] + spread
    return df.reset_index(drop=True)


def agregar(df, seg, minimo=1):
    g = df.assign(b=df['epoch'] // seg * seg).groupby('b').agg(
        o=('bo', 'first'), h=('bh', 'max'), l=('bl', 'min'), c=('bc', 'last'), n=('bc', 'size'))
    return g[g['n'] >= minimo]


def asof(df, serie, desfase):
    """Valor de la ultima vela mayor CERRADA al cierre de cada vela M15 (clave = cierre - duracion)."""
    s = serie.dropna().sort_index()
    clave = df['epoch'].to_numpy() + VELA - desfase
    pos = np.searchsorted(s.index.to_numpy(), clave, side='right') - 1
    v = s.to_numpy()[np.clip(pos, 0, None)]
    return np.where(pos >= 0, v, np.nan)


def preparar(sym):
    df = cargar(sym)
    c, h, l = df['bc'], df['bh'], df['bl']
    df['ema21'], df['ema12'], df['ema26'] = (c.ewm(span=n, adjust=False).mean() for n in (21, 12, 26))
    df['rsi14'] = rsi(c)
    df['atr'], df['adx14'], pdi, ndi = atr_adx(h, l, c)
    df['di'] = pdi - ndi
    df['wpr14'] = -100 * (h.rolling(14).max() - c) / (h.rolling(14).max() - l.rolling(14).min()).replace(0, np.nan)
    m20, s20 = c.rolling(20).mean(), c.rolling(20).std(ddof=0)
    df['bb_sup'], df['bb_inf'] = m20 + 2 * s20, m20 - 2 * s20
    df['bb_ancho'] = (4 * s20) / df['atr']
    df['max10'], df['min10'] = h.shift(1).rolling(10).max(), l.shift(1).rolling(10).min()
    df['sar'] = sar(h, l)
    cien_dias, un_dia = 100 * 86400 // VELA, 86400 // VELA
    df['atr_pct100'] = pct_rodante(df['atr'], cien_dias)
    lr = np.log(c).diff()
    df['volreal_pct100'] = pct_rodante(lr.rolling(un_dia).std(), cien_dias)
    df['spread'] = df['ao'] - df['bo']
    # H1 y D1 armadas con las mismas velas; se usa la ultima CERRADA
    H = agregar(df, 3600)
    _, adxh, _, _ = atr_adx(H['h'], H['l'], H['c'])
    df['ema200_h1'] = asof(df, H['c'].ewm(span=200, adjust=False).mean(), 3600)
    df['sar_h1'] = asof(df, pd.Series(sar(H['h'], H['l']), index=H.index), 3600)
    df['adx_h1_pct100'] = asof(df, pct_rodante(adxh, 2400), 3600)
    D = agregar(df, 86400, minimo=5 * 3600 // VELA)   # dias con al menos 5 horas (sin el tramo del domingo)
    df['ema50_d1'] = asof(df, D['c'].ewm(span=50, adjust=False).mean(), 86400)
    df['max_dia'], df['min_dia'] = asof(df, D['h'], 86400), asof(df, D['l'], 86400)
    e = df['epoch'].to_numpy()
    df['hora'] = (e % 86400) // 3600
    df['dia'] = ((e // 86400) + 3) % 7         # 0 = lunes
    df['dia_utc'] = e // 86400
    inicio = np.select([df['hora'] >= 21, df['hora'] >= 13, df['hora'] >= 7], [21, 13, 7], 0)
    df['min_sesion'] = (e % 86400) // 60 - inicio * 60
    df['interior'] = ((h <= h.shift(1)) & (l >= l.shift(1))).astype(float)
    df['pos_canal10'] = (c - df['min10']) / (df['max10'] - df['min10']).replace(0, np.nan)
    df['anio'] = pd.to_datetime(e, unit='s', utc=True).year
    return df


# ------------------------------------------------------------------ resultado de cada entrada posible

def resultados(df, stop_atr=1.5, obj_r=1.5):
    """R neto de entrar comprado y vendido en la apertura de t+1 para cada vela t, y la vela de salida."""
    n = len(df)
    bo, bh, bl, bc = (df[k].to_numpy() for k in ('bo', 'bh', 'bl', 'bc'))
    ao, ah, al, ac = (df[k].to_numpy() for k in ('ao', 'ah', 'al', 'ac'))
    atr = df['atr'].to_numpy()
    idx = np.arange(n)
    nxt = np.minimum(idx + 1, n - 1)
    sp = np.maximum(ao[nxt] - bo[nxt], 0.0)
    slip = 0.5 * sp
    riesgo = stop_atr * atr
    out = {}
    for d in (1, -1):
        entrada = ao[nxt] + slip if d == 1 else bo[nxt] - slip
        sl = entrada - d * riesgo
        tp = entrada + d * obj_r * riesgo
        salida = np.full(n, np.nan)
        vela = np.full(n, -1, dtype=np.int64)
        vivo = np.ones(n, dtype=bool)
        for j in range(1, HORIZONTE + 1):
            m = np.minimum(idx + j, n - 1)
            if d == 1:
                ab, mn, mx, ci = bo[m], bl[m], bh[m], bc[m]
                gap = (ab <= sl) & (j > 1)
                toca_sl, toca_tp = mn <= sl, mx >= tp
            else:
                ab, mn, mx, ci = ao[m], al[m], ah[m], ac[m]
                gap = (ab >= sl) & (j > 1)
                toca_sl, toca_tp = mx >= sl, mn <= tp
            for cond, precio in ((gap, ab), (~gap & toca_sl, sl), (~gap & ~toca_sl & toca_tp, tp)):
                k = vivo & cond
                salida[k], vela[k] = precio[k] if np.ndim(precio) else precio, m[k]
                vivo &= ~k
            if j == HORIZONTE:
                salida[vivo], vela[vivo] = ci[vivo], m[vivo]
        salida = salida - d * slip
        with np.errstate(divide='ignore', invalid='ignore'):
            r = d * (salida - entrada) / riesgo
        valido = (idx + HORIZONTE < n) & (atr > 0) & np.isfinite(atr)
        # entrada solo si la vela siguiente llega dentro de 1 hora (no tras un fin de semana)
        e = df['epoch'].to_numpy()
        valido &= (e[nxt] - e) <= (3600 if VELA == 900 else 7200)
        with np.errstate(divide='ignore', invalid='ignore'):
            out[d] = (np.where(valido, r, np.nan), vela, 2 * sp / np.where(riesgo > 0, riesgo, np.nan))
    return out


# ------------------------------------------------------------------ estrategias base

def senales(df, activo):
    c, h, l = df['bc'], df['bh'], df['bl']
    p = c.shift(1)
    hora = df['hora']
    s = {}
    if activo == 'Oro':
        ses = hora.between(7, 9)
        ya_arriba = (c > df['max_dia']).astype(int).groupby(df['dia_utc']).cumsum()
        ya_abajo = (c < df['min_dia']).astype(int).groupby(df['dia_utc']).cumsum()
        compra = ses & (c > df['max_dia']) & (ya_arriba == 1) & (c > df['ema200_h1']) & (df['sar_h1'] < c)
        venta = ses & (c < df['min_dia']) & (ya_abajo == 1) & (c < df['ema200_h1']) & (df['sar_h1'] > c)
        s['A_ruptura_dia'] = (compra, venta)
        compra = ses & (c > df['ema200_h1']) & (l <= df['ema21']) & (df['rsi14'] < 40) & (c > df['ema21'])
        venta = ses & (c < df['ema200_h1']) & (h >= df['ema21']) & (df['rsi14'] > 60) & (c < df['ema21'])
        s['D_pullback'] = (compra, venta)
    elif activo == 'Nasdaq':
        ses = hora.between(13, 20)
        s['A_canal10'] = (ses & (c > df['max10']), ses & (c < df['min10']))
        cruce_arriba = (df['ema12'] > df['ema26']) & (df['ema12'].shift(1) <= df['ema26'].shift(1))
        cruce_abajo = (df['ema12'] < df['ema26']) & (df['ema12'].shift(1) >= df['ema26'].shift(1))
        s['B_ema_adx'] = (ses & cruce_arriba & (df['adx14'] > 30) & (c > df['ema200_h1']),
                          ses & cruce_abajo & (df['adx14'] > 30) & (c < df['ema200_h1']))
    else:
        s['A_sar'] = ((c > df['max10']) & (df['sar'] < c) & (c > df['ema200_h1']),
                      (c < df['min10']) & (df['sar'] > c) & (c < df['ema200_h1']))
        s['C_bollinger'] = ((p < df['bb_inf'].shift(1)) & (c >= df['bb_inf']) & (df['adx14'] < 25),
                            (p > df['bb_sup'].shift(1)) & (c <= df['bb_sup']) & (df['adx14'] < 25))
    return s


def operaciones(df, activo, R):
    """Operaciones de las estrategias base (una posicion a la vez por familia) con sus variables."""
    filas = []
    c, atr = df['bc'].to_numpy(), df['atr'].to_numpy()
    for fam_i, (fam, (compra, venta)) in enumerate(senales(df, activo).items()):
        sen = [(i, 1) for i in np.where(compra.fillna(False).to_numpy())[0]] + [(i, -1) for i in np.where(venta.fillna(False).to_numpy())[0]]
        sen.sort()
        libre = -1
        for i, d in sen:
            r, vela, costo = R[d]
            if i < libre or not np.isfinite(r[i]):
                continue
            libre = vela[i]
            filas.append((i, d, fam, fam_i, r[i], costo[i], vela[i]))
    t = pd.DataFrame(filas, columns=['i', 'dir', 'estrategia', 'familia', 'r', 'costo_extra', 'vela_salida'])
    if t.empty:
        return t
    i, d = t['i'].to_numpy(), t['dir'].to_numpy()
    X = pd.DataFrame({
        'd_ema200_h1': (c[i] - df['ema200_h1'].to_numpy()[i]) / atr[i] * d,
        'd_ema50_d1': (c[i] - df['ema50_d1'].to_numpy()[i]) / atr[i] * d,
        'sar_h1_lado': np.sign(c[i] - df['sar_h1'].to_numpy()[i]) * d,
        'rsi14': df['rsi14'].to_numpy()[i], 'wpr14': df['wpr14'].to_numpy()[i], 'adx14': df['adx14'].to_numpy()[i],
        'di_dif': df['di'].to_numpy()[i] * d, 'atr_pct100': df['atr_pct100'].to_numpy()[i],
        'bb_ancho': df['bb_ancho'].to_numpy()[i],
        'd_max_dia': (c[i] - df['max_dia'].to_numpy()[i]) / atr[i], 'd_min_dia': (c[i] - df['min_dia'].to_numpy()[i]) / atr[i],
        'pos_canal10': df['pos_canal10'].to_numpy()[i], 'interior': df['interior'].to_numpy()[i],
        'hora': df['hora'].to_numpy()[i], 'dia': df['dia'].to_numpy()[i], 'min_sesion': df['min_sesion'].to_numpy()[i],
        'adx_h1_pct100': df['adx_h1_pct100'].to_numpy()[i], 'volreal_pct100': df['volreal_pct100'].to_numpy()[i],
        'spread_atr': df['spread'].to_numpy()[i] / atr[i]})   # dir y familia ya estan en t
    t = pd.concat([t, X], axis=1)
    e = df['epoch'].to_numpy()
    t['epoch'], t['salida_epoch'] = e[i], e[t['vela_salida'].to_numpy()]
    t['anio'], t['hora_i'] = df['anio'].to_numpy()[i], df['hora'].to_numpy()[i]
    return t.sort_values('epoch').reset_index(drop=True)


# ------------------------------------------------------------------ modelo

def entrenar(t):
    m = lgb.LGBMClassifier(**PARAMS)
    m.fit(t[VARIABLES], (t['r'] > 0).astype(int))
    return m, float(np.median(m.predict_proba(t[VARIABLES])[:, 1]))


def caminar(t, primer_anio):
    """Prediccion caminando por anos con purga de 1 dia. Devuelve las operaciones predichas con 'toma'."""
    res = []
    for a in range(primer_anio, 2027):
        prueba = t[t['anio'] == a]
        ent = t[t['salida_epoch'] < ep(a) - 86400]
        if prueba.empty or len(ent) < 100:
            continue
        m, umbral = entrenar(ent)
        p = m.predict_proba(prueba[VARIABLES])[:, 1]
        res.append(prueba.assign(p=p, toma=p >= umbral))
    return pd.concat(res) if res else t.iloc[:0].assign(p=[], toma=[])


def is_oos(t):
    """70/30 por fecha. IS medido con predicciones fuera de pliego (5 bloques, purga 1 dia)."""
    corte = t['epoch'].min() + 0.7 * (t['epoch'].max() - t['epoch'].min())
    IS, OOS = t[t['salida_epoch'] < corte - 86400], t[t['epoch'] >= corte]
    bordes = np.linspace(IS['epoch'].min(), IS['epoch'].max() + 1, 6)
    tomadas = []
    for k in range(5):
        a, b = bordes[k], bordes[k + 1]
        blq = IS[(IS['epoch'] >= a) & (IS['epoch'] < b)]
        ent = IS[(IS['salida_epoch'] < a - 86400) | (IS['epoch'] >= b + 86400)]
        if blq.empty or len(ent) < 100:
            continue
        m, u = entrenar(ent)
        tomadas.append(blq[m.predict_proba(blq[VARIABLES])[:, 1] >= u])
    r_is = float(pd.concat(tomadas)['r'].mean()) if tomadas else float('nan')
    m, u = entrenar(IS)
    oos = OOS[m.predict_proba(OOS[VARIABLES])[:, 1] >= u]
    r_oos = float(oos['r'].mean()) if len(oos) else float('nan')
    return {'corte': dt.datetime.fromtimestamp(corte, dt.timezone.utc).strftime('%Y-%m-%d'), 'r_medio_is': round(r_is, 4),
            'r_medio_oos': round(r_oos, 4), 'ops_oos': int(len(oos)),
            'pasa': bool(r_is > 0 and r_oos > 0 and r_oos >= 0.5 * r_is)}


# ------------------------------------------------------------------ criterios

def azar(sis, df, R, rng):
    """Suma de R de N_AZAR simulaciones: mismas operaciones con entrada al azar (mismo ano, hora y direccion)."""
    grupos = {}
    anio, hora = df['anio'].to_numpy(), df['hora'].to_numpy()
    for d in (1, -1):
        ok = np.isfinite(R[d][0])
        claves = pd.Series(np.arange(len(df))[ok]).groupby([anio[ok], hora[ok]]).apply(lambda s: s.to_numpy())
        grupos[d] = claves.to_dict()
    sumas = np.zeros(N_AZAR)
    for (a, hh, d), n in sis.groupby(['anio', 'hora_i', 'dir']).size().items():
        pool = grupos[d].get((a, hh))
        if pool is None or len(pool) == 0:
            continue
        elegidos = pool[rng.integers(0, len(pool), size=(N_AZAR, n))]
        sumas += R[d][0][elegidos].sum(axis=1)
    return sumas


def criterios(sis, df, R, rng, io=None):
    por_anio = sis.groupby('anio')['r'].sum()
    ult = [float(por_anio.get(a, 0.0)) for a in ULTIMOS]
    estres = [a for a in ESTRES if a in por_anio.index]
    suma = float(sis['r'].sum())
    sims = azar(sis, df, R, rng)
    p95, pb = float(np.percentile(sims, PCT_AZAR)), float(np.percentile(sims, PCT_BONF))
    doble = float((sis['r'] - sis['costo_extra']).sum())
    c = {
        '1_expectativa': {'r_medio': round(float(sis['r'].mean()), 4) if len(sis) else None, 'pasa': bool(len(sis) and sis['r'].mean() > 0)},
        '2_caminata_6_anos': {'suma_r_2020_2025': [round(x, 2) for x in ult], 'positivos': sum(x > 0 for x in ult),
                              'pasa': sum(x > 0 for x in ult) >= 5},
        '3_estres': {'anos': estres, 'suma_r': round(float(por_anio.reindex(estres).sum()), 2) if estres else None,
                     'pasa': bool(estres) and float(por_anio.reindex(estres).sum()) >= 0},
        '4_azar': {'suma_r': round(suma, 2), 'p95_azar': round(p95, 2), 'p98_3_azar': round(pb, 2),
                   'percentil': round(float((sims < suma).mean() * 100), 1), 'pasa': suma > p95, 'pasa_bonferroni': suma > pb},
        '5_costo_doble': {'suma_r': round(doble, 2), 'pasa': doble > 0},
        '7_frecuencia': {'operaciones': int(len(sis)), 'pasa': len(sis) >= 100},
    }
    if io is not None:
        c['6_is_oos'] = io
    c['veredicto'] = 'EVIDENCIA DE VENTAJA' if all(v['pasa'] for k, v in c.items() if isinstance(v, dict)) and \
        c['4_azar']['pasa_bonferroni'] else 'NO HAY EVIDENCIA DE VENTAJA'
    return c


def resumen(t):
    if t.empty:
        return {'ops': 0}
    return {'ops': int(len(t)), 'r_medio': round(float(t['r'].mean()), 4), 'suma_r': round(float(t['r'].sum()), 2),
            'acierto': round(float((t['r'] > 0).mean()), 4)}


def linea_c(nombre, c):
    return ('  %-26s %s | R medio %s | 6 anos %d/6 | estres %s | azar pct %.1f (p95 %.1f) | costo x2 %.1f | %s ops%s' % (
        nombre, c['veredicto'], c['1_expectativa']['r_medio'], c['2_caminata_6_anos']['positivos'], c['3_estres']['suma_r'],
        c['4_azar']['percentil'], c['4_azar']['p95_azar'], c['5_costo_doble']['suma_r'], c['7_frecuencia']['operaciones'],
        (' | IS/OOS %s -> %s %s' % (c['6_is_oos']['r_medio_is'], c['6_is_oos']['r_medio_oos'], 'pasa' if c['6_is_oos']['pasa'] else 'falla'))
        if '6_is_oos' in c else ''))


def main(modo, salida):
    global VELA, HORIZONTE
    VELA = 3600 if modo == 'h1' else 900
    HORIZONTE = 4 * 3600 // VELA
    t0 = time.time()
    rng = np.random.default_rng(1)
    J = {'plan': 'docs/validacion/brief_modelo_filtro_plan.md', 'activos': {}}
    J['version'] = 'H1 (enmienda 1)' if VELA == 3600 else 'M15 (plan original)'
    L = ['EXPERIMENTO DEL BRIEF: MODELO-FILTRO EN %s - plan pre-registrado en %s' % (J['version'], J['plan']),
         'Triple barrera (stop 1,5 ATR, objetivo 1,5 R, %d velas = 4 h), entrada ask/bid + deslizamiento 0,5 spread por lado.' % HORIZONTE,
         'Veredicto: EVIDENCIA solo si pasa los 7 criterios del brief y el azar al percentil 98,3 (3 activos).', '']
    for activo, sym in ACTIVOS:
        log('%s: preparando' % activo)
        df = preparar(sym)
        R = resultados(df)
        t = operaciones(df, activo, R)
        primer = int(df['anio'].min()) + 3
        sis_todo = caminar(t, primer)
        sis = sis_todo[sis_todo['toma']]
        base = t[t['anio'] >= primer]
        log('%s: %d velas M15, %d senales base, %d tomadas por el filtro' % (activo, len(df), len(base), len(sis)))
        io = is_oos(t)
        cs = criterios(sis, df, R, rng, io)
        cb = criterios(base, df, R, rng)
        A = {'velas_m15': int(len(df)), 'desde': str(dt.datetime.fromtimestamp(int(df['epoch'].min()), dt.timezone.utc).date()),
             'primer_ano_caminado': primer, 'spread_mediano': round(float(df['spread'].median()), 5),
             'sistema': cs, 'base_sin_filtro': cb}
        A['por_estrategia'] = {f: {'base': resumen(base[base['estrategia'] == f]), 'sistema': resumen(sis[sis['estrategia'] == f])}
                               for f in t['estrategia'].unique()}
        A['por_direccion'] = {('compras' if d == 1 else 'ventas'): {'base': resumen(base[base['dir'] == d]), 'sistema': resumen(sis[sis['dir'] == d])}
                              for d in (1, -1)}
        A['por_ano_sistema'] = {int(a): round(float(v), 2) for a, v in sis.groupby('anio')['r'].sum().items()}
        A['por_ano_base'] = {int(a): round(float(v), 2) for a, v in base.groupby('anio')['r'].sum().items()}
        # sensibilidad (informativa): stop 2,0 ATR y objetivo 2 R
        R2 = resultados(df, 2.0, 2.0)
        t2 = operaciones(df, activo, R2)
        s2 = caminar(t2, primer)
        A['sensibilidad_stop2_obj2'] = {'sistema': resumen(s2[s2['toma']]), 'base': resumen(t2[t2['anio'] >= primer])}
        J['activos'][activo] = A
        L.append('== %s (%s, desde %s, caminado desde %d; spread mediano %s)' % (activo, sym, A['desde'], primer, A['spread_mediano']))
        L.append(linea_c('SISTEMA (base + filtro)', cs))
        L.append(linea_c('base sin filtro', cb))
        for f, v in A['por_estrategia'].items():
            L.append('    %-16s base %s | sistema %s' % (f, v['base'], v['sistema']))
        for k, v in A['por_direccion'].items():
            L.append('    %-16s base %s | sistema %s' % (k, v['base'], v['sistema']))
        L.append('    por ano (sistema): ' + ', '.join('%d %+.1f' % (a, v) for a, v in A['por_ano_sistema'].items()))
        L.append('    sensibilidad stop 2 ATR / objetivo 2R: sistema %s | base %s' % (
            A['sensibilidad_stop2_obj2']['sistema'], A['sensibilidad_stop2_obj2']['base']))
        L.append('')
    L.append('== VEREDICTOS')
    for activo, A in J['activos'].items():
        fallos = [k for k, v in A['sistema'].items() if isinstance(v, dict) and not v['pasa']]
        L.append('  %-7s %s%s' % (activo, A['sistema']['veredicto'], ('  (falla: %s)' % ', '.join(fallos)) if fallos else ''))
    L += ['', 'Tiempo total: %.1f min' % ((time.time() - t0) / 60)]
    open(salida + '.txt', 'w', encoding='utf-8').write('\n'.join(L) + '\n')
    json.dump(J, open(salida + '.json', 'w', encoding='utf-8'), indent=1, default=float)
    print('\n'.join(L))


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
