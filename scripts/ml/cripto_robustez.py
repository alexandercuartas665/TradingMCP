"""Robustez posterior (no decide): H1 por moneda y con otras medias; H2 con otros plazos."""
import sys, warnings
warnings.filterwarnings('ignore')
sys.path.insert(0, r'C:\DesarrolloIA\Traiding\scripts\ml')
import numpy as np, pandas as pd
import cripto_estudio as C
horas = {s: C.cargar(r'D:\traiding_ml', 'Binance_%s_1h.csv' % s) for s in C.MONEDAS}
cier = pd.DataFrame({s: C.diarias(h) for s, h in horas.items()})
r = cier.pct_change()
desde = C.INICIO_PRUEBA
def evalua(pos, nombre, cols=None, tramo=(desde, 2**40)):
    cols = cols or list(cier.columns)
    rr = r[cols]; disp = rr.notna(); n = disp.sum(axis=1).replace(0, np.nan)
    w = pos[cols].where(disp).div(n, axis=0).fillna(0)
    y = (w * rr.fillna(0)).sum(axis=1) - w.diff().abs().sum(axis=1) * C.COSTO
    bh = rr.where(disp).mean(axis=1)
    m = (y.index >= tramo[0]) & (y.index < tramo[1]) & bh.notna()
    a, t, b = C.newey_west_alfa(y[m].to_numpy(), bh[m].to_numpy())
    mt = C.metricas(y[m].to_numpy()); mb = C.metricas(bh[m].to_numpy())
    print('%-34s alfa %7.1f%% t %5.2f | sharpe %5.2f vs %5.2f | caida %5.1f%% vs %5.1f%%' % (nombre, a*365*100, t, mt['sharpe'], mb['sharpe'], mt['caida_max_pct'], mb['caida_max_pct']))
print('H1 media 50 por moneda (2019+):')
p50 = (cier > cier.rolling(50).mean()).astype(float).where(cier.rolling(50).mean().notna()).shift(1)
for s in C.MONEDAS: evalua(p50, '  ' + s, [s])
print('H1 cartera con otras medias:')
for k in (20, 30, 100, 150, 200):
    sm = cier.rolling(k).mean()
    evalua((cier > sm).astype(float).where(sm.notna()).shift(1), '  media %d' % k)
    evalua((cier > sm).astype(float).where(sm.notna()).shift(1), '  media %d solo 2022+' % k, tramo=(C.ep(2022), 2**40))
print('H1 solo BTC+ETH, media 50, 2022+:'); evalua(p50, '  BTC+ETH 2022+', ['BTCUSDT', 'ETHUSDT'], (C.ep(2022), 2**40))
