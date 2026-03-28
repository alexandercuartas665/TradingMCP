import asyncio
import websockets
import json
import sys

# El Token que generamos en la consola de Deriv
DERIV_API_TOKEN = 'pat_f33f6f20c8c31e2707d1060a7f37b61b7c219d2756398d2e9a0279ee5426caa3'
# App ID genérico para pruebas (1089)
APP_ID = '1089'
URI = f"wss://ws.derivws.com/websockets/v3?app_id={APP_ID}"

async def main():
    print(f"[*] Conectando a la API de Deriv (App ID: {APP_ID})...")
    
    try:
        async with websockets.connect(URI) as websocket:
            print("[*] Conexión establecida correctamente.")
            
            # --- Opcional: Autenticación ---
            # Si quisieras hacer operaciones con tu cuenta, descomenta las siguientes lineas:
            # print("[*] Autenticando con el token...")
            # await websocket.send(json.dumps({"authorize": DERIV_API_TOKEN}))
            # auth_response = await websocket.recv()
            # print("Respuesta Autorización:", json.loads(auth_response))

            print("[*] Solicitando símbolos activos de la plataforma...")
            # Solicitud de listado de símbolos activos (active_symbols)
            # product_type: basic (opciones, multiplicadores) o light (muy resumido)
            request = {
                "active_symbols": "brief",
                "product_type": "basic"
            }
            await websocket.send(json.dumps(request))
            
            print("[*] Esperando respuesta de los servidores...")
            response = await websocket.recv()
            data = json.loads(response)
            
            if 'error' in data:
                print(f"[!] Ocurrió un error en la solicitud: {data['error']['message']}")
                sys.exit(1)
                
            symbols = data.get('active_symbols', [])
            print(f"\n[+] Operación Exitosa. Total de activos encontrados: {len(symbols)}")
            print("-" * 50)
            
            # Formateando e imprimiendo la salida para la consola PowerShell
            print(f"{'SÍMBOLO (ID)'.ljust(20)} | {'MERCADO'[:15].ljust(15)} | NOMBRE")
            print("-" * 50)
            
            # Mostrar todos los activos encontrados
            for sym in symbols:
                symbol_id = sym.get('symbol', 'N/A')
                market = sym.get('market_display_name', 'N/A')
                name = sym.get('display_name', 'N/A')
                
                print(f"{symbol_id.ljust(20)} | {market[:15].ljust(15)} | {name}")
                
            print("-" * 50)
            print("Todos los activos mostrados con éxito.")
            
    except Exception as e:
        print(f"\n[X] Error de conexión: {e}")
        print("Asegúrate de tener conexión a Internet y que la librería 'websockets' esté bien instalada.")

if __name__ == "__main__":
    # Inicia el ciclo de eventos asíncronos de Python
    asyncio.run(main())
