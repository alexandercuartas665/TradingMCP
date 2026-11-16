"""Forward test pre-registrado del modelo LightGBM de binarias (docs/validacion/forward_ml_plan.md).

Cada hora, al cierre de la vela de 1h (HH:00:20 UTC):
  1. pide las ultimas velas de 1h de los 7 mercados por la conexion autenticada de la app
     (POST /api/heavy/trading/consulta, solo lectura),
  2. calcula las mismas 47 variables del entrenamiento y la probabilidad de subida,
  3. guarda TODAS las predicciones (predicciones.jsonl),
  4. para cada senal (|p - 0,5| >= umbral del 10%, una posicion a la vez por mercado) cotiza
     Rise/Fall de 2h sin comprar (POST /api/heavy/payouts) y anota el precio de entrada
     (ultimo tick) en senales.jsonl,
  5. resuelve las senales vencidas con el tick del vencimiento (resultados.jsonl).

Modo papel (por defecto): no compra nada. Con --comprar-demo, las senales PRIMARIAS (divisas, 5%)
disponibles se compran en la cuenta DEMO (1 USD) por POST /api/heavy/trading/comprar-demo, que la
app rechaza si la cuenta conectada no es demo. Ese modo lo activa el usuario.

Uso (entorno de D:):
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/forward_lightgbm.py             # bucle, papel
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/forward_lightgbm.py --una-vez   # un ciclo ahora
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/forward_lightgbm.py --resumen   # informe
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/forward_lightgbm.py --comprar-demo
"""
import argparse
import datetime as dt
import json
import math
import os
import sys
import time
import traceback
import urllib.request

AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, AQUI)
RAIZ = os.path.abspath(os.path.join(AQUI, '..', '..'))

import lightgbm as lgb  # noqa: E402
import numpy as np  # noqa: E402
import pandas as pd  # noqa: E402

import modelo_ml as mm  # noqa: E402

API = 'http://localhost:8766/api/heavy'
DIVISAS = ['frxEURUSD', 'frxGBPJPY', 'frxGBPUSD', 'frxUSDCHF', 'frxUSDJPY']
HORIZONTE = 2 * 3600
VELAS = 400
OPS_DECISION = 200
PAYOUT_REFERENCIA = 72.45


# ------------------------------------------------------------------ utilidades
def ahora():
    return int(time.time())


def iso(epoch):
    return dt.datetime.fromtimestamp(epoch, dt.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')


def token():
    ruta = os.path.join(os.environ.get('LOCALAPPDATA', ''), 'WpfDerivClientVB', 'agent_token.txt')
    return open(ruta, encoding='utf-8').read().strip()


def llamar(ruta, cuerpo=None, timeout=120):
    datos = None if cuerpo is None else json.dumps(cuerpo).encode('utf-8')
    req = urllib.request.Request(API + ruta, data=datos, method='GET' if cuerpo is None else 'POST')
    req.add_header('X-Agent-Token', token())
    if datos is not None:
        req.add_header('Content-Type', 'application/json')
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode('utf-8'))


def anexar(ruta, fila):
    with open(ruta, 'a', encoding='utf-8') as f:
        f.write(json.dumps(fila, default=float) + '\n')


def leer(ruta):
    if not os.path.exists(ruta):
        return []
    with open(ruta, encoding='utf-8') as f:
        return [json.loads(x) for x in f if x.strip()]


def log(carpeta, texto):
    linea = '%s %s' % (iso(ahora()), texto)
    print(linea, flush=True)
    with open(os.path.join(carpeta, 'forward.log'), 'a', encoding='utf-8') as f:
        f.write(linea + '\n')


# ------------------------------------------------------------------ app
def conectar_demo():
    e = llamar('/trading/estado', timeout=20)
    if e.get('connected') and e.get('demo'):
        return True
    if e.get('connected') and not e.get('demo'):
        raise RuntimeError('la app esta conectada a la cuenta REAL; el forward test es solo demo')
    c = llamar('/trading/conectar', {'demo': True}, timeout=90)
    return bool(c.get('connected'))


def velas(sym):
    r = llamar('/trading/consulta', {'ticks_history': sym, 'style': 'candles', 'granularity': 3600,
                                     'count': VELAS, 'end': 'latest'}, timeout=60)
    if r.get('error'):
        raise RuntimeError('%s: %s' % (sym, r['error']))
    df = pd.DataFrame(r.get('candles') or [])
    if df.empty:
        return df
    df = df.rename(columns={'open': 'o', 'high': 'h', 'low': 'l', 'close': 'c'})[['epoch', 'o', 'h', 'l', 'c']]
    return df.astype({'epoch': 'int64', 'o': float, 'h': float, 'l': float, 'c': float})


def tick(sym, fin='latest'):
    r = llamar('/trading/consulta', {'ticks_history': sym, 'style': 'ticks', 'count': 1, 'end': fin}, timeout=60)
    h = r.get('history') or {}
    if not h.get('prices'):
        raise RuntimeError('%s sin tick (%s)' % (sym, r.get('error')))
    return int(h['times'][-1]), float(h['prices'][-1])


def cotizar(sym):
    r = llamar('/payouts', {'mercados': [[sym, '1h', 2]], 'monto': 10}, timeout=300)
    m = (r.get('mercados') or [{}])[0]
    err = m.get('error') or m.get('call_error') or m.get('put_error')
    return m.get('payout_pct'), (str(err).split('\n')[0] if err else None)


# ------------------------------------------------------------------ modelo
class Modelo:
    def __init__(self, carpeta):
        meta = json.load(open(os.path.join(RAIZ, 'docs', 'validacion', 'modelo_ml_2026-11-16.json'), encoding='utf-8'))
        self.cols = meta['variables']
        self.umbrales = meta['resultados']['binarias_lightgbm']['umbrales_final']
        self.booster = lgb.Booster(model_file=os.path.join(carpeta, 'modelos', 'lightgbm_binarias.txt'))
        ev = json.load(open(os.path.join(RAIZ, 'docs', 'validacion', 'eventos_eeuu_2003_2024.json'), encoding='utf-8'))['eventos']
        self.eventos = [(int(dt.datetime.fromisoformat(e['inicio_utc'].replace('Z', '+00:00')).timestamp()),
                         int(dt.datetime.fromisoformat(e['fin_utc'].replace('Z', '+00:00')).timestamp())) for e in ev]

    def nivel(self, p):
        d = abs(p - 0.5)
        for k in ('5%', '10%', '20%'):
            if d >= self.umbrales[k]:
                return k
        return '100%'

    def predecir(self, series, epoch_decision):
        d = mm.construir_desde_series(series, self.eventos)
        filas = d[d['epoch'] == epoch_decision]
        if filas.empty:
            return []
        p = self.booster.predict(filas[self.cols].to_numpy(np.float32))
        return list(zip(filas['symbol'], p))


# ------------------------------------------------------------------ ciclo
def ciclo(carpeta, modelo, comprar):
    rp = os.path.join(carpeta, 'predicciones.jsonl')
    rs = os.path.join(carpeta, 'senales.jsonl')
    rr = os.path.join(carpeta, 'resultados.jsonl')
    t = ahora()
    epoch_decision = (t // 3600) * 3600 - 3600          # vela que acaba de cerrar
    if any(x['epoch_vela'] == epoch_decision for x in leer(rp)[-50:]):
        log(carpeta, 'vela %s ya procesada' % iso(epoch_decision))
    elif t % 3600 > 600:
        log(carpeta, 'fuera de ventana (%s): la vela se procesa solo en los primeros 10 min de la hora' % iso(t))
    else:
        conectar_demo()
        series, falta = {}, []
        for sym in mm.MERCADOS:
            try:
                df = velas(sym)
                df = df[df['epoch'] + 3600 <= t]           # solo velas cerradas
                if len(df) and df['epoch'].iloc[-1] == epoch_decision:
                    series[sym] = df
                else:
                    falta.append(sym)
            except Exception as ex:                          # mercado cerrado o error puntual
                falta.append(sym)
                log(carpeta, 'velas %s: %s' % (sym, ex))
        preds = modelo.predecir(series, epoch_decision) if series else []
        abiertas = {s['symbol']: s['vence_epoch'] for s in leer(rs) if s['vence_epoch'] > t}
        n_sen = 0
        for sym, p in preds:
            nivel = modelo.nivel(float(p))
            anexar(rp, {'epoch_vela': epoch_decision, 'decision_utc': iso(epoch_decision + 3600), 'symbol': sym,
                        'p': round(float(p), 5), 'nivel': nivel})
            if nivel not in ('5%', '10%') or sym in abiertas:
                continue
            try:
                payout, err = cotizar(sym)
            except Exception as ex:
                payout, err = None, 'cotizacion fallida: %s' % ex
            try:
                entrada_epoch, entrada = tick(sym)
            except Exception as ex:
                log(carpeta, 'tick de entrada %s: %s' % (sym, ex))
                entrada_epoch, entrada = ahora(), None
            primaria = sym in DIVISAS and nivel == '5%'
            s = {'id': '%s_%d' % (sym, epoch_decision), 'symbol': sym, 'epoch_vela': epoch_decision,
                 'decision_utc': iso(epoch_decision + 3600), 'hora_utc': ((epoch_decision + 3600) % 86400) // 3600,
                 'p': round(float(p), 5), 'nivel': nivel, 'direccion': 'CALL' if p > 0.5 else 'PUT',
                 'primaria': primaria, 'disponible': err is None and payout is not None, 'payout_pct': payout,
                 'error_cotizacion': err, 'entrada_epoch': entrada_epoch, 'entrada': entrada,
                 'vence_epoch': entrada_epoch + HORIZONTE, 'compra_demo': None}
            if comprar and primaria and s['disponible']:
                try:
                    c = llamar('/trading/comprar-demo', {'contract_type': s['direccion'], 'symbol': sym, 'amount': 1,
                                                         'duration': 2, 'duration_unit': 'h', 'basis': 'stake',
                                                         '_meta_estrategia': 'ML_LightGBM_binarias'}, timeout=60)
                    s['compra_demo'] = {k: c.get(k) for k in ('ok', 'contract_id', 'buy_price', 'payout', 'error')}
                except Exception as ex:
                    s['compra_demo'] = {'ok': False, 'error': str(ex)}
            anexar(rs, s)
            n_sen += 1
            log(carpeta, 'SENAL %s %s p=%.3f nivel %s %s payout %s %s' % (
                sym, s['direccion'], p, nivel, 'PRIMARIA' if primaria else '', payout, err or ''))
        log(carpeta, 'vela %s: %d predicciones, %d senales, sin vela: %s' % (
            iso(epoch_decision), len(preds), n_sen, ','.join(falta) or '-'))
    # resolver senales vencidas
    hechas = {r['id'] for r in leer(rr)}
    for s in leer(rs):
        if s['id'] in hechas or s['vence_epoch'] > ahora() - 60 or s['entrada'] is None:
            continue
        try:
            salida_epoch, salida = tick(s['symbol'], s['vence_epoch'])
        except Exception as ex:
            log(carpeta, 'resolver %s: %s' % (s['id'], ex))
            continue
        sube = salida > s['entrada']
        gana = (sube if s['direccion'] == 'CALL' else salida < s['entrada'])
        anexar(rr, {'id': s['id'], 'salida_epoch': salida_epoch, 'salida': salida, 'gana': bool(gana)})
        log(carpeta, 'RESULTADO %s %s %s -> %s: %s' % (s['id'], s['direccion'], s['entrada'], salida, 'GANA' if gana else 'pierde'))


# ------------------------------------------------------------------ informe
def wilson(g, n, z=1.96):
    if n == 0:
        return (0.0, 0.0)
    p = g / n
    den = 1 + z * z / n
    c = (p + z * z / (2 * n)) / den
    m = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / den
    return (c - m, c + m)


def resumen(carpeta):
    senales = {s['id']: s for s in leer(os.path.join(carpeta, 'senales.jsonl'))}
    res = leer(os.path.join(carpeta, 'resultados.jsonl'))
    preds = leer(os.path.join(carpeta, 'predicciones.jsonl'))
    filas = [dict(senales[r['id']], **r) for r in res if r['id'] in senales]
    pagos = [s['payout_pct'] for s in senales.values() if s.get('payout_pct')]
    payout = float(np.mean(pagos)) if pagos else PAYOUT_REFERENCIA
    eq = 100.0 / (100.0 + payout)
    out = ['FORWARD TEST LightGBM binarias (plan: docs/validacion/forward_ml_plan.md)',
           'Velas evaluadas: %d   predicciones: %d   senales: %d   resueltas: %d' % (
               len({p['epoch_vela'] for p in preds}), len(preds), len(senales), len(filas)),
           'Payout medio cotizado %.2f%% -> punto de equilibrio %.2f%%' % (payout, eq * 100), '']

    def linea(titulo, xs):
        n = len(xs)
        g = sum(1 for x in xs if x['gana'])
        if n == 0:
            return '  %-44s sin operaciones' % titulo
        lo, hi = wilson(g, n)
        z = (g / n - eq) / math.sqrt(eq * (1 - eq) / n)
        pnl = sum(payout / 100 if x['gana'] else -1 for x in xs)
        return '  %-44s %4d ops  acierto %5.1f%%  IC95 %5.1f-%5.1f%%  z vs equilibrio %5.2f  PnL x1USD %+7.2f' % (
            titulo, n, 100 * g / n, 100 * lo, 100 * hi, z, pnl)

    disp = [x for x in filas if x['disponible']]
    prim = [x for x in disp if x['primaria']]
    out.append(linea('PRIMARIA divisas 5% disponibles', prim))
    out.append('    decision a las %d operaciones primarias: ventaja si z >= 1,645 (faltan %d)' % (
        OPS_DECISION, max(0, OPS_DECISION - len(prim))))
    out.append(linea('divisas 10% disponibles', [x for x in disp if x['symbol'] in DIVISAS]))
    out.append(linea('metales 10% disponibles', [x for x in disp if x['symbol'] not in DIVISAS]))
    out.append(linea('todas, incluidas no disponibles', filas))
    out.append('')
    for h in sorted({x['hora_utc'] for x in filas}):
        out.append(linea('hora %02d UTC (todas las senales)' % h, [x for x in filas if x['hora_utc'] == h]))
    nod = [s for s in senales.values() if not s['disponible']]
    if nod:
        out.append('')
        out.append('Senales no disponibles para comprar: %d (p. ej. %s)' % (len(nod), nod[-1].get('error_cotizacion')))
    compras = [s for s in senales.values() if s.get('compra_demo')]
    if compras:
        out.append('Compras demo: %d (ok %d)' % (len(compras), sum(1 for s in compras if s['compra_demo'].get('ok'))))
    texto = '\n'.join(out)
    print(texto)
    return texto


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--carpeta', default='D:/traiding_ml')
    ap.add_argument('--una-vez', action='store_true')
    ap.add_argument('--resumen', action='store_true')
    ap.add_argument('--comprar-demo', action='store_true')
    ap.add_argument('--prueba', action='store_true', help='escribe en forward_prueba (no cuenta)')
    a = ap.parse_args()
    carpeta = os.path.join(a.carpeta, 'forward_prueba' if a.prueba else 'forward')
    os.makedirs(carpeta, exist_ok=True)
    if a.resumen:
        resumen(carpeta)
        return
    modelo = Modelo(a.carpeta)
    log(carpeta, 'inicio forward test (%s), umbral 5%% %.4f, 10%% %.4f' % (
        'COMPRA DEMO' if a.comprar_demo else 'papel', modelo.umbrales['5%'], modelo.umbrales['10%']))
    while True:
        try:
            ciclo(carpeta, modelo, a.comprar_demo)
            fallo = False
        except Exception as ex:
            fallo = True
            log(carpeta, 'ERROR ciclo: %s | %s' % (ex, traceback.format_exc().splitlines()[-1]))
        if a.una_vez:
            return
        # siguiente cierre de vela + 20 s; si fallo (app caida), reintento cada 2 min en los
        # primeros 10 min de la hora: mas tarde la entrada ya no seria la del plan
        t = ahora()
        if fallo and t % 3600 < 480:
            time.sleep(120)
        else:
            time.sleep(max(30, (t // 3600 + 1) * 3600 + 20 - t))


if __name__ == '__main__':
    main()
