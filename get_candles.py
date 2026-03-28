import asyncio
import websockets
import json
import sys
import argparse
from datetime import datetime

# Token y App ID
DERIV_API_TOKEN = 'pat_f33f6f20c8c31e2707d1060a7f37b61b7c219d2756398d2e9a0279ee5426caa3'
APP_ID = '1089'
URI = f"wss://ws.derivws.com/websockets/v3?app_id={APP_ID}"

# Mapa de timeframes (texto) a granularidad (segundos permitidos por Deriv)
TIMEFRAME_MAP = {
    '1m': 60,
    '2m': 120,
    '3m': 180,
    '5m': 300,
    '10m': 600,
    '15m': 900,
    '30m': 1800,
    '1h': 3600,
    '2h': 7200,
    '4h': 14400,
    '8h': 28800,
    '1d': 86400
}

async def get_historical_candles(symbol, timeframe_str, count):
    if timeframe_str not in TIMEFRAME_MAP:
        print(f"[X] Error: Timeframe '{timeframe_str}' no válido.")
        print(f"Opciones válidas: {', '.join(TIMEFRAME_MAP.keys())}")
        sys.exit(1)
        
    granularity = TIMEFRAME_MAP[timeframe_str]
    
    print(f"[*] Conectando a Deriv API buscando {count} velas de {timeframe_str} para {symbol}...")
    
    try:
        async with websockets.connect(URI) as websocket:
            # Solicitud de historial en formato "candles" (velas japonesas)
            request = {
                "ticks_history": symbol,
                "adjust_start_time": 1,
                "count": count,
                "end": "latest",
                "style": "candles",
                "granularity": granularity
            }
            
            await websocket.send(json.dumps(request))
            response = await websocket.recv()
            data = json.loads(response)
            
            if 'error' in data:
                print(f"\n[!] Ocurrió un error en la solicitud: {data['error']['message']}")
                sys.exit(1)
                
            candles = data.get('candles', [])
            
            if not candles:
                print("\n[!] No se encontró historial para este activo y temporalidad.")
                return

            print(f"\n[+] Historial recuperado ({len(candles)} velas).")
            print("-" * 85)
            print(f"{'FECHA Y HORA'.ljust(20)} | {'APERTURA (Open)'.ljust(15)} | {'MÁXIMO (High)'.ljust(15)} | {'MÍNIMO (Low)'.ljust(15)} | {'CIERRE (Close)'}")
            print("-" * 85)
            
            for candle in candles:
                # Convertir epoch timestamp a texto legible local
                dt_obj = datetime.fromtimestamp(candle['epoch'])
                date_str = dt_obj.strftime('%Y-%m-%d %H:%M:%S')
                
                open_p = f"{candle['open']:.5f}"
                high_p = f"{candle['high']:.5f}"
                low_p = f"{candle['low']:.5f}"
                close_p = f"{candle['close']:.5f}"
                
                print(f"{date_str.ljust(20)} | {open_p.ljust(15)} | {high_p.ljust(15)} | {low_p.ljust(15)} | {close_p}")
                
            print("-" * 85)
            
            # Análisis super básico del último movimiento
            if len(candles) >= 2:
                last_c = candles[-1]['close']
                prev_c = candles[-2]['close']
                diff = last_c - prev_c
                tendencia = "ALCISTA 🟩" if diff > 0 else "BAJISTA 🟥" if diff < 0 else "NEUTRA ⬜"
                print(f"Última vela analizada marca tendencia a corto plazo: {tendencia} (Dif: {diff:.5f})")

    except Exception as e:
        print(f"\n[X] Error de conexión: {e}")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Obtener velas históricas de Deriv")
    parser.add_argument('--symbol', type=str, required=True, help="Símbolo del activo (ej: R_100, frxEURUSD)")
    parser.add_argument('--timeframe', type=str, required=True, help="Temporalidad (1m, 5m, 1h, 1d, etc)")
    parser.add_argument('--count', type=int, default=10, help="Cantidad de velas a recuperar (máx 5000)")
    
    args = parser.parse_args()
    
    # Inicia el ciclo asíncrono pasándole los argumentos
    asyncio.run(get_historical_candles(args.symbol, args.timeframe, args.count))
