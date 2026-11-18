"""Forward test en papel de la tendencia en cripto (docs/validacion/forward_cripto_plan.md).

Cada dia, al cierre de la vela diaria UTC (00:00 UTC + 3 min), baja de la API publica de Binance las
ultimas velas diarias de 9 monedas y:
  1. anota el resultado del dia que cerro con los pesos decididos el dia anterior,
  2. calcula los pesos del dia siguiente: E2 (tendencia con tamano por volatilidad), H1 (tendencia
     simple) y comprar y mantener.
No compra nada. Si el PC estuvo apagado recupera los dias pendientes con velas cerradas.

Uso (entorno de D:):
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/forward_cripto.py            # bucle diario
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/forward_cripto.py --una-vez  # un ciclo ahora
  D:/traiding_ml/venv/Scripts/python.exe -I scripts/ml/forward_cripto.py --resumen  # informe
"""
import argparse
import datetime as dt
import json
import math
import os
import time
import traceback
import urllib.request

MONEDAS = ['BTCUSDT', 'ETHUSDT', 'BNBUSDT', 'LTCUSDT', 'XRPUSDT', 'ADAUSDT', 'LINKUSDT', 'DOGEUSDT', 'SOLUSDT']
APIS = ['https://data-api.binance.vision/api/v3/klines', 'https://api.binance.com/api/v3/klines']
COSTO = 0.00125
SMA = 50
VOL = 30
VOL_OBJETIVO = 0.50
DIA = 86400
CARPETA = 'D:/traiding_ml/forward_cripto'
ESTRATEGIAS = ['E2', 'H1', 'BH']


def iso_dia(epoch):
    return dt.datetime.fromtimestamp(epoch, dt.timezone.utc).strftime('%Y-%m-%d')


def log(msg, carpeta):
    linea = '%s %s' % (dt.datetime.now(dt.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'), msg)
    with open(os.path.join(carpeta, 'forward_cripto.log'), 'a', encoding='utf-8') as f:
        f.write(linea + '\n')
    print(linea, flush=True)


def leer(ruta):
    if not os.path.exists(ruta):
        return []
    return [json.loads(x) for x in open(ruta, encoding='utf-8') if x.strip()]


def velas(sym, limite=120):
    """{epoch_dia: cierre} solo de velas diarias ya cerradas."""
    ultimo = None
    for base in APIS:
        try:
            url = '%s?symbol=%s&interval=1d&limit=%d' % (base, sym, limite)
            req = urllib.request.Request(url, headers={'User-Agent': 'traiding-research/1.0'})
            with urllib.request.urlopen(req, timeout=30) as r:
                filas = json.loads(r.read().decode('utf-8'))
            ahora_ms = time.time() * 1000
            return {int(f[0]) // 1000: float(f[4]) for f in filas if int(f[6]) < ahora_ms}
        except Exception as e:  # siguiente fuente
            ultimo = e
    raise RuntimeError('sin datos de %s: %s' % (sym, ultimo))


def pesos(cierres, dia):
    """cierres: {sym: {epoch: cierre}}. Pesos para el dia siguiente a `dia` con datos hasta `dia`."""
    info, disp = {}, []
    for s in MONEDAS:
        serie = cierres.get(s, {})
        dias = [d for d in sorted(serie) if d <= dia]
        if not dias or dias[-1] != dia or len(dias) < SMA + 1:
            continue
        c = [serie[d] for d in dias]
        if any(dias[i] - dias[i - 1] != DIA for i in range(len(dias) - VOL - 1, len(dias))):
            continue
        sma = sum(c[-SMA:]) / SMA
        rets = [c[i] / c[i - 1] - 1 for i in range(len(c) - VOL, len(c))]
        m = sum(rets) / VOL
        vol = math.sqrt(sum((x - m) ** 2 for x in rets) / (VOL - 1)) * math.sqrt(365)
        senal = 1.0 if c[-1] > sma else 0.0
        info[s] = {'cierre': c[-1], 'sma50': round(sma, 8), 'vol30_anual': round(vol, 4), 'senal': senal}
        disp.append(s)
    n = len(disp)
    w = {k: {} for k in ESTRATEGIAS}
    for s in disp:
        x = info[s]
        w['H1'][s] = x['senal'] / n
        w['E2'][s] = x['senal'] * min(1.0, VOL_OBJETIVO / x['vol30_anual']) / n if x['vol30_anual'] > 0 else 0.0
        w['BH'][s] = 1.0 / n
    return w, info


def ciclo(carpeta):
    ruta = os.path.join(carpeta, 'diario.jsonl')
    hist = leer(ruta)
    cierres = {s: velas(s) for s in MONEDAS}
    ultimo_cerrado = max(max(v) for v in cierres.values() if v)
    if not hist:
        w, info = pesos(cierres, ultimo_cerrado)
        fila = {'dia': iso_dia(ultimo_cerrado), 'epoch': ultimo_cerrado, 'inicio': True, 'pesos_siguiente': w, 'info': info,
                'equidad': {k: 1.0 for k in ESTRATEGIAS}, 'registrado': int(time.time())}
        with open(ruta, 'a', encoding='utf-8') as f:
            f.write(json.dumps(fila) + '\n')
        log('INICIO con cierre de %s. E2 invertido %.1f%%, H1 %.1f%%' % (
            fila['dia'], sum(w['E2'].values()) * 100, sum(w['H1'].values()) * 100), carpeta)
        return
    prev = hist[-1]
    dia = prev['epoch'] + DIA
    while dia <= ultimo_cerrado:
        faltan = [s for s in prev['pesos_siguiente']['BH'] if dia not in cierres.get(s, {}) or prev['epoch'] not in cierres.get(s, {})]
        if faltan:
            log('dia %s: faltan velas de %s; se reintenta despues' % (iso_dia(dia), ','.join(faltan)), carpeta)
            return
        ret = {s: cierres[s][dia] / cierres[s][prev['epoch']] - 1 for s in prev['pesos_siguiente']['BH']}
        w_ant = prev.get('pesos_aplicados') or {k: {} for k in ESTRATEGIAS}
        neto, eq = {}, {}
        for k in ESTRATEGIAS:
            wk = prev['pesos_siguiente'][k]
            giro = sum(abs(wk.get(s, 0.0) - w_ant.get(k, {}).get(s, 0.0)) for s in set(wk) | set(w_ant.get(k, {})))
            neto[k] = sum(wk[s] * ret[s] for s in wk) - giro * COSTO
            eq[k] = prev['equidad'][k] * (1 + neto[k])
        w, info = pesos(cierres, dia)
        ahora = int(time.time())
        fila = {'dia': iso_dia(dia), 'epoch': dia, 'retornos': ret, 'pesos_aplicados': prev['pesos_siguiente'],
                'neto': neto, 'equidad': eq, 'pesos_siguiente': w, 'info': info, 'registrado': ahora,
                'recuperado': ahora > dia + 2 * DIA + 6 * 3600}
        with open(ruta, 'a', encoding='utf-8') as f:
            f.write(json.dumps(fila) + '\n')
        log('dia %s: E2 %+.2f%% H1 %+.2f%% BH %+.2f%% | equidad E2 %.4f H1 %.4f BH %.4f | manana E2 invertido %.1f%%%s' % (
            fila['dia'], neto['E2'] * 100, neto['H1'] * 100, neto['BH'] * 100, eq['E2'], eq['H1'], eq['BH'],
            sum(w['E2'].values()) * 100, ' (recuperado)' if fila['recuperado'] else ''), carpeta)
        prev = fila
        dia += DIA


def caida(eq):
    pico, peor = 1.0, 0.0
    for x in eq:
        pico = max(pico, x)
        peor = max(peor, 1 - x / pico)
    return peor


def resumen(carpeta):
    hist = leer(os.path.join(carpeta, 'diario.jsonl'))
    if not hist:
        print('Sin registros todavia en %s' % carpeta)
        return
    dias = [h for h in hist if not h.get('inicio')]
    print('FORWARD CRIPTO EN PAPEL (docs/validacion/forward_cripto_plan.md)')
    print('Inicio: cierre de %s. Dias contados: %d (recuperados: %d)' % (
        hist[0]['dia'], len(dias), sum(1 for h in dias if h.get('recuperado'))))
    nombres = {'E2': 'E2 tendencia por volatilidad', 'H1': 'H1 tendencia simple', 'BH': 'Comprar y mantener'}
    for k in ESTRATEGIAS:
        eq = [h['equidad'][k] for h in dias]
        r = [h['neto'][k] for h in dias]
        linea = '  %-30s acumulado %+7.2f%%  caida max %5.2f%%' % (nombres[k], ((eq[-1] if eq else 1.0) - 1) * 100, caida(eq) * 100)
        if len(r) >= 30:
            m = sum(r) / len(r)
            sd = math.sqrt(sum((x - m) ** 2 for x in r) / (len(r) - 1))
            linea += '  Sharpe %.2f' % (m / sd * math.sqrt(365) if sd > 0 else 0.0)
        print(linea)
    ult = hist[-1]
    print('Pesos para el dia siguiente a %s (fraccion del capital):' % ult['dia'])
    for s in MONEDAS:
        if s in ult['pesos_siguiente']['BH']:
            x = ult['info'][s]
            print('  %-9s E2 %5.1f%%  H1 %5.1f%%  | %s media50, vol30 %.0f%%' % (
                s, ult['pesos_siguiente']['E2'][s] * 100, ult['pesos_siguiente']['H1'][s] * 100,
                'sobre' if x['senal'] else 'bajo ', x['vol30_anual'] * 100))
    print('  Total invertido: E2 %.1f%%  H1 %.1f%% (el resto en USDT)' % (
        sum(ult['pesos_siguiente']['E2'].values()) * 100, sum(ult['pesos_siguiente']['H1'].values()) * 100))
    print('Registro en papel, sin dinero. No es asesoria financiera.')


def bucle(carpeta):
    log('bucle iniciado (pid %d)' % os.getpid(), carpeta)
    while True:
        try:
            ciclo(carpeta)
            espera = DIA - (time.time() % DIA) + 180
        except Exception:
            log('ERROR ' + traceback.format_exc().replace('\n', ' | '), carpeta)
            espera = 600
        time.sleep(espera)


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('--una-vez', action='store_true')
    ap.add_argument('--resumen', action='store_true')
    ap.add_argument('--carpeta', default=CARPETA)
    a = ap.parse_args()
    os.makedirs(a.carpeta, exist_ok=True)
    if a.resumen:
        resumen(a.carpeta)
    elif a.una_vez:
        ciclo(a.carpeta)
    else:
        bucle(a.carpeta)
