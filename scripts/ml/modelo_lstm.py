"""Prueba pre-registrada de red neuronal LSTM (docs/validacion/modelo_lstm_plan.md).

Misma tarea que el LightGBM de binarias (subida/bajada 2 velas de 1h despues, 7 mercados), pero la
red recibe la SECUENCIA de las ultimas 48 velas en vez de variables resumidas. Entrenamiento
caminando con reentreno anual en la reserva 2019-2024 (Dukascopy) y reserva 2 en Deriv desde
2024-04-01 con el modelo entrenado en todo Dukascopy. Solo CPU.

Uso (entorno de D:):
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/modelo_lstm.py D:/traiding_ml docs/validacion/eventos_eeuu_2003_2024.json docs/validacion/modelo_lstm_2026-11-16
  ... --medir-tiempo   (solo cronometra una epoca con datos al azar, no mira resultados)
"""
import json
import math
import os
import sys
import time

AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, AQUI)

import lightgbm as lgb  # noqa: E402
import numpy as np  # noqa: E402
import torch  # noqa: E402
from torch import nn  # noqa: E402

import modelo_ml as mm  # noqa: E402

SECUENCIA = 48
OCULTAS = 32
LOTE = 2048
EPOCAS_MAX = 8
PACIENCIA = 2
APRENDIZAJE = 1e-3
HZ = 2
NY = 'y_binarias'
PASO = ['_r1', 'rango', 'cuerpo', 'mecha_sup', 'mecha_inf', 'hora_sin', 'hora_cos']
CRUZADAS = ['x_%s_r1' % s[3:] for s in mm.MERCADOS]
HILOS = 12


def preparar(d):
    """Devuelve por mercado: matriz de pasos (n, f), one-hot, y, ret, cierre."""
    out = {}
    for i, sym in enumerate(mm.MERCADOS):
        g = d[d['symbol'] == sym].sort_values('epoch').reset_index(drop=True)
        if g.empty:
            continue
        r1 = g['x_%s_r1' % sym[3:]]
        cols = [r1] + [g[c] for c in PASO[1:]] + [g[c] for c in CRUZADAS]
        X = np.nan_to_num(np.column_stack([np.asarray(c, dtype=np.float32) for c in cols]), nan=0.0, posinf=0.0, neginf=0.0)
        X = np.clip(X, -10, 10).astype(np.float32)
        oh = np.zeros(len(mm.MERCADOS), np.float32)
        oh[i] = 1
        out[sym] = {'X': X, 'oh': oh, 'y': g[NY].to_numpy(np.float32), 'ret': g['ret_binarias'].to_numpy(np.float64),
                    'cierre': g['cierre'].to_numpy(np.int64)}
    return out


def indices(datos, desde, hasta, exigir_y=True, fin_horizonte=False):
    """Lista (symbol, fila) con historia de 48 velas y cierre en [desde, hasta)."""
    sel = []
    for sym, m in datos.items():
        c = m['cierre']
        lim = c + HZ * 3600 if fin_horizonte else c
        ok = (c >= desde) & (lim < hasta)
        ok[:SECUENCIA - 1] = False
        if exigir_y:
            ok &= ~np.isnan(m['y'])
        for f in np.nonzero(ok)[0]:
            sel.append((sym, int(f)))
    return sel


class Red(nn.Module):
    def __init__(self, n_paso, n_mercados):
        super().__init__()
        self.lstm = nn.LSTM(n_paso, OCULTAS, batch_first=True)
        self.cabeza = nn.Sequential(nn.Dropout(0.2), nn.Linear(OCULTAS + n_mercados, 16), nn.ReLU(), nn.Linear(16, 1))

    def forward(self, x, oh):
        _, (h, _) = self.lstm(x)
        return self.cabeza(torch.cat([h[-1], oh], dim=1)).squeeze(1)


def lotes(datos, sel, tam):
    """Genera (x, oh, y) por lotes; agrupa por mercado para cortar ventanas sin copiar todo."""
    vistas = {s: np.lib.stride_tricks.sliding_window_view(m['X'], SECUENCIA, axis=0) for s, m in datos.items()}
    for i in range(0, len(sel), tam):
        parte = sel[i:i + tam]
        x = np.stack([vistas[s][f - SECUENCIA + 1] for s, f in parte]).transpose(0, 2, 1)
        oh = np.stack([datos[s]['oh'] for s, _ in parte])
        y = np.array([datos[s]['y'][f] for s, f in parte], np.float32)
        yield torch.from_numpy(np.ascontiguousarray(x)), torch.from_numpy(oh), torch.from_numpy(y)


def predecir(red, datos, sel):
    red.eval()
    ps = []
    with torch.no_grad():
        for x, oh, _ in lotes(datos, sel, 8192):
            ps.append(torch.sigmoid(red(x, oh)).numpy())
    return np.concatenate(ps) if ps else np.zeros(0)


def entrenar(datos, hasta, registro):
    """Entrena con filas cuyo horizonte termina antes de hasta - 1 dia; ultimo 10% para parar."""
    torch.manual_seed(mm.SEMILLA)
    rng = np.random.default_rng(mm.SEMILLA)
    sel = indices(datos, mm.INICIO, hasta - mm.SEPARACION, fin_horizonte=True)
    sel.sort(key=lambda t: datos[t[0]]['cierre'][t[1]])
    corte = int(len(sel) * 0.9)
    tr, va = sel[:corte], sel[corte:]
    n_paso = datos[tr[0][0]]['X'].shape[1]
    red = Red(n_paso, len(mm.MERCADOS))
    opt = torch.optim.Adam(red.parameters(), lr=APRENDIZAJE)
    perdida = nn.BCEWithLogitsLoss()
    yva = np.array([datos[s]['y'][f] for s, f in va])
    mejor, estado, sin_mejora = 1e9, None, 0
    for ep in range(EPOCAS_MAX):
        red.train()
        orden = [tr[i] for i in rng.permutation(len(tr))]
        t0 = time.time()
        for x, oh, y in lotes(datos, orden, LOTE):
            opt.zero_grad()
            l = perdida(red(x, oh), y)
            l.backward()
            opt.step()
        p = np.clip(predecir(red, datos, va), 1e-6, 1 - 1e-6)
        lv = float(-np.mean(yva * np.log(p) + (1 - yva) * np.log(1 - p)))
        registro('    epoca %d: perdida validacion %.5f (%.0fs)' % (ep + 1, lv, time.time() - t0))
        if lv < mejor - 1e-5:
            mejor, estado, sin_mejora = lv, {k: v.clone() for k, v in red.state_dict().items()}, 0
        else:
            sin_mejora += 1
            if sin_mejora >= PACIENCIA:
                break
    red.load_state_dict(estado)
    p_tr = predecir(red, datos, sel)
    umbrales = {k: float(np.percentile(np.abs(p_tr - 0.5), q)) if q > 0 else 0.0 for k, q in mm.COBERTURAS.items()}
    return red, umbrales, len(sel), p_tr, sel


def evaluar(red, umbrales, datos, sel, p=None):
    if p is None:
        p = predecir(red, datos, sel)
    import pandas as pd
    base = pd.DataFrame({'symbol': [s for s, _ in sel], 'cierre': [datos[s]['cierre'][f] for s, f in sel],
                         'ret': [datos[s]['ret'][f] for s, f in sel]})
    return {k: mm.operar(base, p, u, HZ) for k, u in umbrales.items()}, p


def medir_tiempo():
    torch.set_num_threads(HILOS)
    n = 600000
    red = Red(len(PASO) + len(CRUZADAS), len(mm.MERCADOS))
    opt = torch.optim.Adam(red.parameters(), lr=APRENDIZAJE)
    perdida = nn.BCEWithLogitsLoss()
    x = torch.randn(LOTE, SECUENCIA, len(PASO) + len(CRUZADAS))
    oh = torch.zeros(LOTE, len(mm.MERCADOS))
    y = (torch.rand(LOTE) > 0.5).float()
    t0 = time.time()
    for _ in range(20):
        opt.zero_grad()
        perdida(red(x, oh), y).backward()
        opt.step()
    s = (time.time() - t0) / 20
    print('%.3f s por lote de %d -> %.1f min por epoca de %d secuencias' % (s, LOTE, s * n / LOTE / 60, n))


def main(carpeta, ruta_eventos, salida):
    import datetime as dt
    torch.set_num_threads(HILOS)
    t0 = time.time()
    lineas_log = []

    def registro(t):
        print(t, flush=True)
        lineas_log.append(t)

    ev = json.load(open(ruta_eventos, encoding='utf-8'))['eventos']
    eventos = [(int(dt.datetime.fromisoformat(e['inicio_utc'].replace('Z', '+00:00')).timestamp()),
                int(dt.datetime.fromisoformat(e['fin_utc'].replace('Z', '+00:00')).timestamp())) for e in ev]
    duka_df = mm.construir(carpeta, 'Dukascopy', eventos)
    duka_df = duka_df[duka_df['cierre'] < mm.FIN_DUKA].reset_index(drop=True)
    deriv_df = mm.construir(carpeta, 'Deriv', eventos)
    # _r1 propio = x_<SYM>_r1 (ya esta en las cruzadas); se conserva para PASO via preparar
    duka, deriv = preparar(duka_df), preparar(deriv_df)
    registro('datos listos %.0fs' % (time.time() - t0))

    lineas = ['RED NEURONAL LSTM - plan pre-registrado en docs/validacion/modelo_lstm_plan.md',
              'Secuencia de %d velas de 1h, %d variables por vela, LSTM de %d unidades. Binarias (2 velas).' % (
                  SECUENCIA, len(PASO) + len(CRUZADAS), OCULTAS),
              'Ventaja: reserva 1 10%% acierto > 56,18%% con z(85%%) >= 2,24 y reserva 2 Deriv 10%% > 54,05%%. Operable: reserva 2 10%% > 58,0%%.', '']
    por = {k: [] for k in mm.COBERTURAS}
    por_anio = []
    for anio in mm.ANIOS_RESERVA:
        ini, fin = mm.epoch_anio(anio), min(mm.epoch_anio(anio + 1), mm.FIN_DUKA)
        registro('== reserva 1, ano %d' % anio)
        red, umb, n_tr, _, _ = entrenar(duka, ini, registro)
        sel = indices(duka, ini, fin)
        ops, _ = evaluar(red, umb, duka, sel)
        for k in mm.COBERTURAS:
            por[k].append(ops[k])
        r10 = mm.resumen(ops['10%'])
        por_anio.append({'anio': anio, 'filas_entrenamiento': n_tr, **r10})
        registro('  %d: entrenamiento %d, 10%% -> %s ops acierto %s (%.0f min)' % (
            anio, n_tr, r10.get('operaciones'), r10.get('acierto'), (time.time() - t0) / 60))
    registro('== reserva 2: entrenado con todo Dukascopy, aplicado a Deriv')
    red, umb, n_tr, p_tr, sel_tr = entrenar(duka, mm.FIN_DUKA + mm.SEPARACION, registro)
    sel2 = indices(deriv, mm.FIN_DUKA, 2 ** 40)
    ops2, p2 = evaluar(red, umb, deriv, sel2)
    torch.save({'estado': red.state_dict(), 'umbrales': umb, 'paso': PASO + CRUZADAS, 'secuencia': SECUENCIA},
               os.path.join(carpeta, 'modelos', 'lstm_binarias.pt'))

    r = {'por_anio': por_anio,
         'reserva1': {k: mm.resumen(np.vstack(v) if len(v) else np.zeros((0, 3))) for k, v in por.items()},
         'reserva2_deriv': {k: mm.resumen(v) for k, v in ops2.items()}, 'umbrales_final': umb}
    r1, r2 = r['reserva1']['10%'], r['reserva2_deriv']['10%']
    ventaja = (r1.get('operaciones', 0) >= 30 and r1['z_vs_85'] >= mm.Z_CRITICO and r1['acierto'] > mm.EQ78
               and r2.get('operaciones', 0) > 0 and r2['acierto'] > mm.EQ85)
    r['veredicto'] = 'VENTAJA' if ventaja else 'SIN VENTAJA'
    r['operable'] = bool(r2.get('operaciones', 0) > 0 and r2['acierto'] > 0.58)

    # secundaria: promedio LSTM + LightGBM en la reserva 2 (umbrales del promedio en entrenamiento)
    booster = lgb.Booster(model_file=os.path.join(carpeta, 'modelos', 'lightgbm_binarias.txt'))
    cols = json.load(open(os.path.join(AQUI, '..', '..', 'docs', 'validacion', 'modelo_ml_2026-11-16.json'), encoding='utf-8'))['variables']

    def p_lgb(df, datos, sel):
        claves = {(s, int(datos[s]['cierre'][f])) for s, f in sel}
        g = df[[(s, int(c)) in claves for s, c in zip(df['symbol'], df['cierre'])]]
        pm = dict(zip(zip(g['symbol'], g['cierre'].astype(int)), booster.predict(g[cols].to_numpy(np.float32))))
        return np.array([pm[(s, int(datos[s]['cierre'][f]))] for s, f in sel])

    pe_tr = (p_tr + p_lgb(duka_df, duka, sel_tr)) / 2
    umb_e = {k: float(np.percentile(np.abs(pe_tr - 0.5), q)) if q > 0 else 0.0 for k, q in mm.COBERTURAS.items()}
    ops_e, _ = evaluar(None, umb_e, deriv, sel2, p=(p2 + p_lgb(deriv_df, deriv, sel2)) / 2)
    r['promedio_lstm_lightgbm_reserva2'] = {k: mm.resumen(v) for k, v in ops_e.items()}

    lineas.append('LSTM => %s%s   (modelo en %s)' % (r['veredicto'], ' y OPERABLE' if r['operable'] else ', no operable',
                                                    os.path.join(carpeta, 'modelos', 'lstm_binarias.pt')))
    for periodo, titulo in (('reserva1', 'reserva 1 2019-2024'), ('reserva2_deriv', 'reserva 2 Deriv'),
                            ('promedio_lstm_lightgbm_reserva2', 'promedio con LightGBM, Deriv')):
        for k in mm.COBERTURAS:
            x = r[periodo][k]
            if not x.get('operaciones'):
                lineas.append('  %-30s %4s: sin operaciones' % (titulo, k))
                continue
            lineas.append('  %-30s %4s: %6d ops  acierto %6.2f%%  z(85%%) %6.2f  retorno medio %+.4f%%' % (
                titulo, k, x['operaciones'], x['acierto'] * 100, x['z_vs_85'], x['retorno_medio_pct']))
    lineas.append('  por ano (10%): ' + ', '.join('%d %s%%' % (a['anio'], round(a['acierto'] * 100, 1) if a.get('operaciones') else '-') for a in por_anio))
    lineas.append('  referencia LightGBM reserva 2 Deriv 10%: 55,47% (7226 ops); 5%: 59,83% (2975 ops)')
    lineas.append('')
    lineas.append('Tiempo total: %.0f min' % ((time.time() - t0) / 60))
    open(salida + '.txt', 'w', encoding='utf-8').write('\n'.join(lineas) + '\n')
    json.dump({'resultados': r, 'registro': lineas_log}, open(salida + '.json', 'w', encoding='utf-8'), indent=1, default=float)
    print('\n'.join(lineas))


if __name__ == '__main__':
    if '--medir-tiempo' in sys.argv:
        medir_tiempo()
    else:
        main(sys.argv[1], sys.argv[2], sys.argv[3])
