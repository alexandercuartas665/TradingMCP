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

TIMEFRAME_MAP = {
    '1m': 60, '2m': 120, '3m': 180, '5m': 300, 
    '10m': 600, '15m': 900, '30m': 1800, '1h': 3600, 
    '2h': 7200, '4h': 14400, '8h': 28800, '1d': 86400
}

async def stream_candles(symbol, timeframe_str):
    if timeframe_str not in TIMEFRAME_MAP:
        print(f"[X] Error: Timeframe '{timeframe_str}' no válido.")
        sys.exit(1)
        
    granularity = TIMEFRAME_MAP[timeframe_str]
    print(f"[*] Estableciendo conexión de STREAMING para {symbol} (Velas: {timeframe_str})...")
    print("[*] Presiona CTRL+C en cualquier momento para detener.\n")
    
    try:
        async with websockets.connect(URI) as websocket:
            # Solicitud con SUBSCRIBRE en 1 para mantener el canal abierto
            request = {
                "ticks_history": symbol,
                "adjust_start_time": 1,
                "count": 20, # Pedimos las ultimas 20 velas
                "end": "latest",
                "style": "candles",
                "granularity": granularity,
                "subscribe": 1 
            }
            
            await websocket.send(json.dumps(request))
            
            print(f"{'HORA DE ACTUALIZACIÓN'.ljust(22)} | {'APERTURA (Open)'.ljust(15)} | {'MÁXIMO (High)'.ljust(15)} | {'MÍNIMO (Low)'.ljust(15)} | {'CIERRE (Close)'}")
            print("-" * 90)
            
            current_open_time = None

            # Bucle infinito escuchando los mensajes que llegan del servidor
            async for message in websocket:
                data = json.loads(message)
                
                # Manejo de errores
                if 'error' in data:
                    print(f"\n[!] Error reportado por el servidor: {data['error']['message']}")
                    break
                    
                # 1. Recibir el paquete historico inicial (las 20 velas)
                if 'candles' in data and len(data['candles']) > 0:
                    candles = data['candles']
                    # Imprimir todas menos la ultima (que seguira viva)
                    for candle in candles[:-1]:
                        dt_obj = datetime.fromtimestamp(candle['epoch'])
                        date_str = dt_obj.strftime('%Y-%m-%d %H:%M:%S')
                        
                        open_p = f"{float(candle['open']):.5f}"
                        high_p = f"{float(candle['high']):.5f}"
                        low_p = f"{float(candle['low']):.5f}"
                        close_p = f"{float(candle['close']):.5f}"
                        print(f"{date_str.ljust(22)} | {open_p.ljust(15)} | {high_p.ljust(15)} | {low_p.ljust(15)} | {close_p}  [Cerrada]")
                    
                    if candles:
                        current_open_time = candles[-1]['epoch']

                # 2. Si es un mensaje de actualización de vela en vivo (ohlc)
                elif 'ohlc' in data:
                    candle = data['ohlc']
                    
                    # Si el tiempo de apertura cambia, significa que empezamos una vela nueva
                    if current_open_time is not None and candle['open_time'] != current_open_time:
                        print("  [Cerrada]") # Termina la linea anterior asegurando el tag
                        current_open_time = candle['open_time']
                        
                    # Convertimos el epoch actual a hora legible
                    dt_obj = datetime.fromtimestamp(candle['open_time'])
                    date_str = dt_obj.strftime('%Y-%m-%d %H:%M:%S')
                    
                    open_p = f"{float(candle['open']):.5f}"
                    high_p = f"{float(candle['high']):.5f}"
                    low_p = f"{float(candle['low']):.5f}"
                    close_p = f"{float(candle['close']):.5f}"
                    
                    # La vela se actualiza constantemente hasta que cambie open_time
                    print(f"\r{date_str.ljust(22)} | {open_p.ljust(15)} | {high_p.ljust(15)} | {low_p.ljust(15)} | {close_p}  (Actualizándose...)", end="", flush=True)

    except asyncio.CancelledError:
        print("\n\n[-] Streaming cerrado por el usuario.")
    except Exception as e:
        print(f"\n\n[X] Desconectado inesperadamente: {e}")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Streaming de precios Deriv")
    parser.add_argument('--symbol', type=str, required=True)
    parser.add_argument('--timeframe', type=str, required=True)
    
    args = parser.parse_args()
    
    try:
        asyncio.run(stream_candles(args.symbol, args.timeframe))
    except KeyboardInterrupt:
        print("\n\n[-] Programa terminado mediante CTRL+C")
