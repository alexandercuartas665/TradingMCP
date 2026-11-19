#!/usr/bin/env bash
# Cadena del experimento del brief cuando Dukascopy limita (429): espera a que responda, completa el
# spread de junio (enmienda 1), corre la version H1 y despues baja los minutos para la version M15.
# Uso: bash scripts/ml/brief_cadena.sh   (desde la raiz del repo; registros en D:/traiding_ml)
P=D:/traiding_ml/venv/Scripts/python.exe
LOG=D:/traiding_ml/brief_cadena.log
PRUEBA=https://datafeed.dukascopy.com/datafeed/XAUUSD/2020/05/ASK_candles_hour_1.bi5
n_spread() { "$P" -I -c "import json;print(len(json.load(open(r'D:/traiding_ml/brief/spread_h1.json'))))" 2>/dev/null || echo 0; }

echo "$(date -u +%FT%TZ) inicio, spread medido: $(n_spread) de 64" >> "$LOG"
for intento in $(seq 1 60); do
  [ "$(n_spread)" -ge 64 ] && break
  until [ "$(curl -s -m 30 -o /dev/null -w '%{http_code}' "$PRUEBA")" = 200 ]; do sleep 1200; done
  echo "$(date -u +%FT%TZ) Dukascopy responde; spread (intento $intento)" >> "$LOG"
  "$P" -I scripts/ml/brief_datos.py spread >> D:/traiding_ml/brief_spread.log 2>&1
  echo "$(date -u +%FT%TZ) spread medido: $(n_spread) de 64" >> "$LOG"
done
if [ "$(n_spread)" -ge 64 ]; then
  echo "$(date -u +%FT%TZ) corriendo version H1" >> "$LOG"
  "$P" -I scripts/ml/brief_experimento.py h1 docs/validacion/brief_modelo_filtro_h1_2026-11-18 > D:/traiding_ml/brief_h1.log 2>&1
  echo "$(date -u +%FT%TZ) H1 terminada (codigo $?)" >> "$LOG"
fi
echo "$(date -u +%FT%TZ) bajando minutos para M15" >> "$LOG"
"$P" -I scripts/ml/brief_datos.py minutos --hilos 1 >> D:/traiding_ml/brief_datos.log 2>&1
echo "$(date -u +%FT%TZ) fin de la cadena" >> "$LOG"
