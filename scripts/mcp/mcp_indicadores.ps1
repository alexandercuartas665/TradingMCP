# ============================================================
# mcp_indicadores.ps1
# Servidor MCP (Model Context Protocol) sobre stdin/stdout
#
# Expone los indicadores tecnicos del proyecto Deriv como
# herramientas que Claude Code puede usar directamente.
#
# Herramientas:
#   listar_indicadores   — catalogo completo de indicadores
#   calcular_indicador   — calcula un indicador sobre velas reales
#   analizar_mercado     — analisis EMA+RSI (estrategia rapida)
#   analizar_seykota     — analisis Donchian+ATR (Seykota)
#
# Uso: registrar en .claude/settings.json como MCP server
# ============================================================

# Token de las APIs locales 8765/8766 (ver agent_auth.ps1)
. (Join-Path $PSScriptRoot '..\agent_auth.ps1')

$SERVER = "http://localhost:8765"

# ── Forzar stdout en UTF-8 sin BOM ─────────────────────────
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding          = [System.Text.Encoding]::UTF8

# ── Helpers JSON-RPC ────────────────────────────────────────
function Send-Response($obj) {
    $json = $obj | ConvertTo-Json -Depth 20 -Compress
    [Console]::WriteLine($json)
}

function Send-Result($id, $result) {
    Send-Response @{ jsonrpc = "2.0"; id = $id; result = $result }
}

function Send-Error($id, $code, $message) {
    Send-Response @{
        jsonrpc = "2.0"; id = $id
        error   = @{ code = $code; message = $message }
    }
}

# ── Llamada al API HTTP ──────────────────────────────────────
function Invoke-Api($path) {
    try {
        return Invoke-RestMethod -Uri "$SERVER$path" -Method GET -TimeoutSec 30
    } catch {
        return $null
    }
}

function Invoke-ApiPost($path, $body) {
    try {
        $json = $body | ConvertTo-Json -Depth 10 -Compress
        return Invoke-RestMethod -Uri "$SERVER$path" -Method POST -Body $json -ContentType "application/json; charset=utf-8" -TimeoutSec 60
    } catch {
        return $null
    }
}

# ── Definicion de herramientas ───────────────────────────────
$TOOLS = @(
    @{
        name        = "listar_indicadores"
        description = "Lista todos los indicadores tecnicos disponibles en el registry del proyecto, con nombre, descripcion y categoria."
        inputSchema = @{
            type       = "object"
            properties = @{}
            required   = @()
        }
    },
    @{
        name        = "calcular_indicador"
        description = "Calcula un indicador tecnico sobre velas reales del activo solicitado. Devuelve los ultimos N resultados con todos sus valores."
        inputSchema = @{
            type       = "object"
            properties = @{
                nombre      = @{ type = "string"; description = "Nombre o fragmento del indicador (ej: 'RSI', 'EMA', 'Bollinger', 'ATR')" }
                symbol      = @{ type = "string"; description = "Simbolo del activo (ej: R_100, frxEURUSD). Default: R_100" }
                granularity = @{ type = "integer"; description = "Granularidad de velas en segundos (60=1min, 300=5min, 3600=1h). Default: 60" }
                ultimos     = @{ type = "integer"; description = "Cuantos resultados devolver (Default: 5, max: 50)" }
            }
            required = @("nombre")
        }
    },
    @{
        name        = "analizar_mercado"
        description = "Analiza el mercado usando la estrategia EMA(5)+EMA(10)+RSI(14). Devuelve senal CALL/PUT/NEUTRAL con confianza y datos de las ultimas 5 velas."
        inputSchema = @{
            type       = "object"
            properties = @{
                symbol      = @{ type = "string"; description = "Simbolo del activo (ej: R_100). Default: R_100" }
                granularity = @{ type = "integer"; description = "Granularidad de velas en segundos. Default: 60" }
            }
            required = @()
        }
    },
    @{
        name        = "analizar_seykota"
        description = "Analiza el mercado con la estrategia Donchian Channel + ATR de Ed Seykota. Devuelve senal CALL/PUT/NEUTRAL, confianza (55 o 80%) y pendiente del canal."
        inputSchema = @{
            type       = "object"
            properties = @{
                symbol      = @{ type = "string"; description = "Simbolo del activo (ej: R_100). Default: R_100" }
                periodo     = @{ type = "integer"; description = "Periodo del canal Donchian (Default: 20)" }
                granularity = @{ type = "integer"; description = "Granularidad de velas en segundos. Default: 60" }
            }
            required = @()
        }
    },
    @{
        name        = "descargar_historico"
        description = "Descarga datos historicos OHLC de un activo desde la API de Deriv y los guarda en PostgreSQL. La descarga corre en background; usa estado_descarga para ver el progreso. Timeframes validos: 1m, 5m, 15m, 1h, 4h, 1d."
        inputSchema = @{
            type       = "object"
            properties = @{
                symbol    = @{ type = "string"; description = "Simbolo del activo (ej: R_100, frxEURUSD, 1HZ100V)" }
                timeframe = @{ type = "string"; description = "Temporalidad: 1m, 5m, 15m, 1h, 4h, 1d" }
                desde     = @{ type = "string"; description = "Fecha inicio en formato YYYY-MM-DD (ej: 2024-01-01)" }
                hasta     = @{ type = "string"; description = "Fecha fin en formato YYYY-MM-DD (ej: 2024-12-31). Default: hoy" }
            }
            required = @("symbol", "timeframe", "desde")
        }
    },
    @{
        name        = "estado_descarga"
        description = "Consulta el estado de la descarga historica en curso (o la ultima ejecutada): cuantas velas se guardaron, pagina actual, si termino o tuvo error."
        inputSchema = @{
            type       = "object"
            properties = @{}
            required   = @()
        }
    },
    @{
        name        = "consultar_datos"
        description = "Consulta las velas OHLC almacenadas en la base de datos PostgreSQL local. Devuelve las ultimas N velas para el activo y temporalidad indicados."
        inputSchema = @{
            type       = "object"
            properties = @{
                symbol    = @{ type = "string";  description = "Simbolo del activo (ej: R_100). Default: R_100" }
                timeframe = @{ type = "string";  description = "Temporalidad (ej: 1h). Default: 1h" }
                limit     = @{ type = "integer"; description = "Maximo de velas a devolver (Default: 100, max: 1000)" }
            }
            required = @()
        }
    },
    @{
        name        = "contar_registros"
        description = "Cuenta cuantas velas historicas hay guardadas en la base de datos para un activo y temporalidad especificos."
        inputSchema = @{
            type       = "object"
            properties = @{
                symbol    = @{ type = "string"; description = "Simbolo del activo (ej: R_100). Default: R_100" }
                timeframe = @{ type = "string"; description = "Temporalidad (ej: 1h). Default: 1h" }
            }
            required = @()
        }
    },
    @{
        name        = "listar_estrategias"
        description = "Lista todas las estrategias de trading disponibles en el registry del proyecto, con nombre y descripcion."
        inputSchema = @{
            type       = "object"
            properties = @{}
            required   = @()
        }
    },
    @{
        name        = "ejecutar_estrategia"
        description = "Ejecuta una estrategia de trading sobre velas reales del activo indicado. Devuelve senal CALL/PUT/NEUTRAL, confianza, motivo y datos de la estrategia. Estrategias disponibles: EmaRsi, Seykota."
        inputSchema = @{
            type       = "object"
            properties = @{
                nombre      = @{ type = "string";  description = "Nombre o fragmento de la estrategia (ej: 'EmaRsi', 'Seykota')" }
                symbol      = @{ type = "string";  description = "Simbolo del activo (ej: R_100). Default: R_100" }
                granularity = @{ type = "integer"; description = "Granularidad de velas en segundos (60=1min, 300=5min). Default: 60" }
                periodo     = @{ type = "integer"; description = "Periodo para estrategias que lo requieren (ej: Seykota usa Donchian(periodo)). Default: 20" }
            }
            required = @("nombre")
        }
    },
    @{
        name        = "dinamicas_listar"
        description = "Lista todas las estrategias dinamicas JSON disponibles. Cada una puede usarse en backtest o live test."
        inputSchema = @{ type = "object"; properties = @{}; required = @() }
    },
    @{
        name        = "dinamicas_leer"
        description = "Lee el contenido JSON completo de una estrategia dinamica. Util para ver o modificar indicadores y condiciones."
        inputSchema = @{
            type       = "object"
            properties = @{
                nombre = @{ type = "string"; description = "Nombre de la estrategia (ej: EmaRsiCruce)" }
            }
            required = @("nombre")
        }
    },
    @{
        name        = "dinamicas_guardar"
        description = "Crea o actualiza una estrategia dinamica como archivo JSON. El archivo se guarda como nombre.json en el directorio de estrategias."
        inputSchema = @{
            type       = "object"
            properties = @{
                nombre    = @{ type = "string"; description = "Nombre de la estrategia" }
                contenido = @{ type = "string"; description = "Contenido JSON completo de la estrategia" }
            }
            required = @("nombre", "contenido")
        }
    },
    @{
        name        = "dinamicas_eliminar"
        description = "Elimina una estrategia dinamica del directorio de estrategias."
        inputSchema = @{
            type       = "object"
            properties = @{
                nombre = @{ type = "string"; description = "Nombre de la estrategia a eliminar" }
            }
            required = @("nombre")
        }
    },
    @{
        name        = "dinamicas_backtest"
        description = "Ejecuta un backtest sobre datos historicos en BD. Devuelve metricas: win_rate, profit_loss, max_drawdown, profit_factor y sesion_id guardado en BD."
        inputSchema = @{
            type       = "object"
            properties = @{
                nombre         = @{ type = "string";  description = "Nombre de la estrategia JSON (ej: EmaRsiCruce)" }
                symbol         = @{ type = "string";  description = "Simbolo del activo. Default: R_100" }
                timeframe      = @{ type = "string";  description = "Temporalidad: 1m, 5m, 1h, 4h, 1d. Default: 1h" }
                desde_epoch    = @{ type = "integer"; description = "Epoch Unix de inicio. Si se omite, usa todo el historial disponible en BD." }
                hasta_epoch    = @{ type = "integer"; description = "Epoch Unix de fin (default: ahora)" }
                duracion_velas = @{ type = "integer"; description = "Velas de duracion de cada trade (default: 5)" }
                payout         = @{ type = "number";  description = "Payout en % para binarias (default: 85)" }
                min_confianza  = @{ type = "integer"; description = "Confianza minima 0-100 (default: 55)" }
                monto          = @{ type = "number";  description = "Monto simulado por operacion (default: 10)" }
                ventana        = @{ type = "integer"; description = "Velas de historia que recibe la estrategia en cada paso (default: 100)" }
            }
            required = @("nombre")
        }
    },
    @{
        name        = "dinamicas_sesiones"
        description = "Lista sesiones de backtest guardadas en la BD. Puede filtrar por nombre de estrategia para ver solo sus ejecuciones."
        inputSchema = @{
            type       = "object"
            properties = @{
                limite     = @{ type = "integer"; description = "Maximo de sesiones (default: 20)" }
                estrategia = @{ type = "string";  description = "Filtrar por nombre de estrategia (ej: EmaRsiCruce). Omitir para ver todas." }
            }
            required = @()
        }
    },
    @{
        name        = "dinamicas_historial"
        description = "Muestra la evolucion temporal de una estrategia: todas sus sesiones ordenadas cronologicamente con deltas de win_rate y PnL entre ejecuciones. Util para ver si los ajustes mejoran o degradan la estrategia."
        inputSchema = @{
            type       = "object"
            properties = @{
                nombre = @{ type = "string";  description = "Nombre de la estrategia (ej: EmaRsiCruce)" }
                limite = @{ type = "integer"; description = "Maximo de sesiones a mostrar (default: 50)" }
            }
            required = @("nombre")
        }
    },
    @{
        name        = "dinamicas_comparar"
        description = "Compara dos sesiones de backtest lado a lado: win_rate, PnL, drawdown, profit_factor con deltas y veredicto (A_mejor/B_mejor/similar). Ideal para evaluar si un cambio en la estrategia mejora los resultados."
        inputSchema = @{
            type       = "object"
            properties = @{
                sesion_a = @{ type = "integer"; description = "ID de la sesion base (antes del cambio)" }
                sesion_b = @{ type = "integer"; description = "ID de la sesion a comparar (despues del cambio)" }
            }
            required = @("sesion_a", "sesion_b")
        }
    },
    @{
        name        = "dinamicas_operaciones"
        description = "Devuelve todas las operaciones de una sesion con tipo, precios, estado (Ganado/Perdido) y PnL."
        inputSchema = @{
            type       = "object"
            properties = @{
                sesion_id = @{ type = "integer"; description = "ID de la sesion (obtenido con dinamicas_sesiones)" }
            }
            required = @("sesion_id")
        }
    },
    @{
        name        = "dinamicas_perdidas"
        description = "Devuelve solo las operaciones perdidas de una sesion con contexto de velas previas y datos de indicadores para analisis."
        inputSchema = @{
            type       = "object"
            properties = @{
                sesion_id = @{ type = "integer"; description = "ID de la sesion" }
            }
            required = @("sesion_id")
        }
    },
    @{
        name        = "dinamicas_live_iniciar"
        description = "Inicia un live test con trades reales en la cuenta demo de Deriv. El streaming de la app debe estar activo para el mismo symbol/timeframe."
        inputSchema = @{
            type       = "object"
            properties = @{
                nombre         = @{ type = "string";  description = "Nombre de la estrategia JSON a usar" }
                symbol         = @{ type = "string";  description = "Simbolo del activo. Default: R_100" }
                timeframe      = @{ type = "string";  description = "Temporalidad. Default: 1h" }
                duracion_velas = @{ type = "integer"; description = "Duracion de cada trade en velas (default: 5)" }
                payout         = @{ type = "number";  description = "Payout esperado en % (default: 85)" }
                min_confianza  = @{ type = "integer"; description = "Confianza minima (default: 55)" }
                monto          = @{ type = "number";  description = "Monto real en USD por operacion (default: 1)" }
            }
            required = @("nombre")
        }
    },
    @{
        name        = "dinamicas_live_detener"
        description = "Detiene el live test activo y guarda metricas finales en BD."
        inputSchema = @{ type = "object"; properties = @{}; required = @() }
    },
    @{
        name        = "dinamicas_live_estado"
        description = "Estado del live test: activo, buffer de velas, trades pendientes, operaciones ganadas/perdidas y PnL acumulado."
        inputSchema = @{ type = "object"; properties = @{}; required = @() }
    }
)

# ── Dispatcher de herramientas ───────────────────────────────
function Invoke-Tool($name, $p) {
    switch ($name) {

        "listar_indicadores" {
            $data = Invoke-Api "/api/indicadores"
            if ($data -eq $null) {
                return @{ error = "No se pudo conectar al servidor en $SERVER. Asegurate de que la app este corriendo." }
            }
            return $data
        }

        "calcular_indicador" {
            $nombre      = $p.nombre
            $symbol      = if ($p.symbol)      { $p.symbol }      else { "R_100" }
            $granularity = if ($p.granularity)  { $p.granularity } else { 60 }
            $ultimos     = if ($p.ultimos)       { $p.ultimos }     else { 5 }

            if ([string]::IsNullOrWhiteSpace($nombre)) {
                return @{ error = "El parametro 'nombre' es obligatorio." }
            }

            $url  = "/api/indicador?symbol=$symbol&nombre=$([uri]::EscapeDataString($nombre))&granularity=$granularity&ultimos=$ultimos"
            $data = Invoke-Api $url
            if ($data -eq $null) {
                return @{ error = "No se pudo conectar al servidor en $SERVER. Asegurate de que la app este corriendo." }
            }
            return $data
        }

        "analizar_mercado" {
            $symbol      = if ($p.symbol)      { $p.symbol }      else { "R_100" }
            $granularity = if ($p.granularity)  { $p.granularity } else { 60 }

            $data = Invoke-Api "/api/analizar?symbol=$symbol&granularity=$granularity"
            if ($data -eq $null) {
                return @{ error = "No se pudo conectar al servidor en $SERVER. Asegurate de que la app este corriendo." }
            }
            return $data
        }

        "analizar_seykota" {
            $symbol      = if ($p.symbol)      { $p.symbol }      else { "R_100" }
            $periodo     = if ($p.periodo)       { $p.periodo }     else { 20 }
            $granularity = if ($p.granularity)  { $p.granularity } else { 60 }

            $data = Invoke-Api "/api/seykota?symbol=$symbol&periodo=$periodo&granularity=$granularity"
            if ($data -eq $null) {
                return @{ error = "No se pudo conectar al servidor en $SERVER. Asegurate de que la app este corriendo." }
            }
            return $data
        }

        "descargar_historico" {
            $symbol    = if ($p.symbol)    { $p.symbol }    else { return @{ error = "El parametro 'symbol' es obligatorio." } }
            $timeframe = if ($p.timeframe) { $p.timeframe } else { return @{ error = "El parametro 'timeframe' es obligatorio." } }
            $desde     = if ($p.desde)     { $p.desde }     else { return @{ error = "El parametro 'desde' es obligatorio." } }
            $hasta     = if ($p.hasta)     { $p.hasta }     else { (Get-Date).ToString("yyyy-MM-dd") }

            $url  = "/api/historico/descargar?symbol=$symbol&timeframe=$([uri]::EscapeDataString($timeframe))&desde=$desde&hasta=$hasta"
            $data = Invoke-Api $url
            if ($data -eq $null) {
                return @{ error = "No se pudo conectar al servidor en $SERVER. Asegurate de que la app este corriendo." }
            }
            return $data
        }

        "estado_descarga" {
            $data = Invoke-Api "/api/historico/estado"
            if ($data -eq $null) {
                return @{ error = "No se pudo conectar al servidor en $SERVER. Asegurate de que la app este corriendo." }
            }
            return $data
        }

        "consultar_datos" {
            $symbol    = if ($p.symbol)    { $p.symbol }    else { "R_100" }
            $timeframe = if ($p.timeframe) { $p.timeframe } else { "1h" }
            $limit     = if ($p.limit)     { $p.limit }     else { 100 }

            $url  = "/api/historico/datos?symbol=$symbol&timeframe=$([uri]::EscapeDataString($timeframe))&limit=$limit"
            $data = Invoke-Api $url
            if ($data -eq $null) {
                return @{ error = "No se pudo conectar al servidor en $SERVER. Asegurate de que la app este corriendo." }
            }
            return $data
        }

        "contar_registros" {
            $symbol    = if ($p.symbol)    { $p.symbol }    else { "R_100" }
            $timeframe = if ($p.timeframe) { $p.timeframe } else { "1h" }

            $data = Invoke-Api "/api/historico/contar?symbol=$symbol&timeframe=$([uri]::EscapeDataString($timeframe))"
            if ($data -eq $null) {
                return @{ error = "No se pudo conectar al servidor en $SERVER. Asegurate de que la app este corriendo." }
            }
            return $data
        }

        "listar_estrategias" {
            $data = Invoke-Api "/api/estrategias"
            if ($data -eq $null) {
                return @{ error = "No se pudo conectar al servidor en $SERVER. Asegurate de que la app este corriendo." }
            }
            return $data
        }

        "ejecutar_estrategia" {
            $nombre      = $p.nombre
            $symbol      = if ($p.symbol)      { $p.symbol }      else { "R_100" }
            $granularity = if ($p.granularity)  { $p.granularity } else { 60 }
            $periodo     = if ($p.periodo)       { $p.periodo }     else { 20 }

            if ([string]::IsNullOrWhiteSpace($nombre)) {
                return @{ error = "El parametro 'nombre' es obligatorio. Usa listar_estrategias para ver las disponibles." }
            }

            $url  = "/api/estrategia?nombre=$([uri]::EscapeDataString($nombre))&symbol=$symbol&granularity=$granularity&periodo=$periodo"
            $data = Invoke-Api $url
            if ($data -eq $null) {
                return @{ error = "No se pudo conectar al servidor en $SERVER. Asegurate de que la app este corriendo." }
            }
            return $data
        }

        "dinamicas_listar" {
            $data = Invoke-Api "/api/dinamicas/listar"
            if ($data -eq $null) { return @{ error = "No se pudo conectar al servidor en $SERVER." } }
            return $data
        }

        "dinamicas_leer" {
            $nombre = $p.nombre
            if ([string]::IsNullOrWhiteSpace($nombre)) { return @{ error = "Parametro 'nombre' requerido." } }
            $data = Invoke-Api "/api/dinamicas/leer?nombre=$([uri]::EscapeDataString($nombre))"
            if ($data -eq $null) { return @{ error = "No se pudo conectar al servidor en $SERVER." } }
            return $data
        }

        "dinamicas_guardar" {
            $nombre    = $p.nombre
            $contenido = $p.contenido
            if ([string]::IsNullOrWhiteSpace($nombre))    { return @{ error = "Parametro 'nombre' requerido." } }
            if ([string]::IsNullOrWhiteSpace($contenido)) { return @{ error = "Parametro 'contenido' requerido." } }
            $body = @{ nombre = $nombre; contenido = $contenido }
            $data = Invoke-ApiPost "/api/dinamicas/guardar" $body
            if ($data -eq $null) { return @{ error = "No se pudo conectar al servidor en $SERVER." } }
            return $data
        }

        "dinamicas_eliminar" {
            $nombre = $p.nombre
            if ([string]::IsNullOrWhiteSpace($nombre)) { return @{ error = "Parametro 'nombre' requerido." } }
            $body = @{ nombre = $nombre }
            $data = Invoke-ApiPost "/api/dinamicas/eliminar" $body
            if ($data -eq $null) { return @{ error = "No se pudo conectar al servidor en $SERVER." } }
            return $data
        }

        "dinamicas_backtest" {
            $nombre = $p.nombre
            if ([string]::IsNullOrWhiteSpace($nombre)) { return @{ error = "Parametro 'nombre' requerido." } }
            $symbol         = if ($p.symbol)         { $p.symbol }         else { "R_100" }
            $timeframe      = if ($p.timeframe)       { $p.timeframe }      else { "1h" }
            $duracion_velas = if ($p.duracion_velas)  { $p.duracion_velas } else { 5 }
            $payout         = if ($p.payout)          { $p.payout }         else { 85 }
            $min_confianza  = if ($p.min_confianza)   { $p.min_confianza }  else { 55 }
            $monto          = if ($p.monto)           { $p.monto }          else { 10 }

            $url = "/api/dinamicas/backtest?nombre=$([uri]::EscapeDataString($nombre))&symbol=$symbol&timeframe=$([uri]::EscapeDataString($timeframe))&duracion_velas=$duracion_velas&payout=$payout&min_confianza=$min_confianza&monto=$monto"
            if ($p.desde_epoch) { $url += "&desde_epoch=$($p.desde_epoch)" }
            if ($p.hasta_epoch) { $url += "&hasta_epoch=$($p.hasta_epoch)" }
            if ($p.ventana)     { $url += "&ventana=$($p.ventana)" }

            $data = Invoke-Api $url
            if ($data -eq $null) { return @{ error = "No se pudo conectar al servidor en $SERVER." } }
            return $data
        }

        "dinamicas_sesiones" {
            $limite = if ($p.limite) { $p.limite } else { 20 }
            $url = "/api/dinamicas/sesiones?limite=$limite"
            if (-not [string]::IsNullOrWhiteSpace($p.estrategia)) {
                $url += "&estrategia=$([uri]::EscapeDataString($p.estrategia))"
            }
            $data = Invoke-Api $url
            if ($data -eq $null) { return @{ error = "No se pudo conectar al servidor en $SERVER." } }
            return $data
        }

        "dinamicas_historial" {
            $nombre = $p.nombre
            if ([string]::IsNullOrWhiteSpace($nombre)) { return @{ error = "Parametro 'nombre' requerido." } }
            $limite = if ($p.limite) { $p.limite } else { 50 }
            $data = Invoke-Api "/api/dinamicas/historial?nombre=$([uri]::EscapeDataString($nombre))&limite=$limite"
            if ($data -eq $null) { return @{ error = "No se pudo conectar al servidor en $SERVER." } }
            return $data
        }

        "dinamicas_comparar" {
            if ($null -eq $p.sesion_a -or $null -eq $p.sesion_b) {
                return @{ error = "Parametros 'sesion_a' y 'sesion_b' requeridos." }
            }
            $data = Invoke-Api "/api/dinamicas/comparar?sesion_a=$($p.sesion_a)&sesion_b=$($p.sesion_b)"
            if ($data -eq $null) { return @{ error = "No se pudo conectar al servidor en $SERVER." } }
            return $data
        }

        "dinamicas_operaciones" {
            $sesion_id = $p.sesion_id
            if ($null -eq $sesion_id) { return @{ error = "Parametro 'sesion_id' requerido." } }
            $data = Invoke-Api "/api/dinamicas/operaciones?sesion_id=$sesion_id"
            if ($data -eq $null) { return @{ error = "No se pudo conectar al servidor en $SERVER." } }
            return $data
        }

        "dinamicas_perdidas" {
            $sesion_id = $p.sesion_id
            if ($null -eq $sesion_id) { return @{ error = "Parametro 'sesion_id' requerido." } }
            $data = Invoke-Api "/api/dinamicas/perdidas?sesion_id=$sesion_id"
            if ($data -eq $null) { return @{ error = "No se pudo conectar al servidor en $SERVER." } }
            return $data
        }

        "dinamicas_live_iniciar" {
            $nombre = $p.nombre
            if ([string]::IsNullOrWhiteSpace($nombre)) { return @{ error = "Parametro 'nombre' requerido." } }
            $body = @{
                nombre         = $nombre
                symbol         = if ($p.symbol)         { $p.symbol }         else { "R_100" }
                timeframe      = if ($p.timeframe)       { $p.timeframe }      else { "1h" }
                duracion_velas = if ($p.duracion_velas)  { $p.duracion_velas } else { 5 }
                payout         = if ($p.payout)          { $p.payout }         else { 85 }
                min_confianza  = if ($p.min_confianza)   { $p.min_confianza }  else { 55 }
                monto          = if ($p.monto)           { $p.monto }          else { 1 }
            }
            $data = Invoke-ApiPost "/api/dinamicas/live/iniciar" $body
            if ($data -eq $null) { return @{ error = "No se pudo conectar al servidor en $SERVER." } }
            return $data
        }

        "dinamicas_live_detener" {
            $data = Invoke-ApiPost "/api/dinamicas/live/detener" @{}
            if ($data -eq $null) { return @{ error = "No se pudo conectar al servidor en $SERVER." } }
            return $data
        }

        "dinamicas_live_estado" {
            $data = Invoke-Api "/api/dinamicas/live/estado"
            if ($data -eq $null) { return @{ error = "No se pudo conectar al servidor en $SERVER." } }
            return $data
        }

        default {
            return @{ error = "Herramienta desconocida: $name" }
        }
    }
}

# ── BUCLE PRINCIPAL MCP ──────────────────────────────────────
while ($true) {
    $line = [Console]::ReadLine()
    if ($null -eq $line) { break }
    $line = $line.Trim()
    if ($line -eq "") { continue }

    try {
        $msg = $line | ConvertFrom-Json
    } catch {
        # Mensaje malformado — ignorar
        continue
    }

    $method = $msg.method
    $id     = $msg.id
    $params = $msg.params

    switch ($method) {

        # ── Handshake inicial ──────────────────────────────
        "initialize" {
            Send-Result $id @{
                protocolVersion = "2024-11-05"
                capabilities    = @{ tools = @{ listChanged = $false } }
                serverInfo      = @{
                    name    = "mcp-indicadores-deriv"
                    version = "1.0.0"
                }
            }
        }

        "notifications/initialized" {
            # Notificacion, sin respuesta
        }

        # ── Lista de herramientas ──────────────────────────
        "tools/list" {
            Send-Result $id @{ tools = $TOOLS }
        }

        # ── Ejecucion de herramienta ───────────────────────
        "tools/call" {
            $toolName = $params.name
            $toolArgs = if ($params.arguments) { $params.arguments } else { @{} }

            $result = Invoke-Tool $toolName $toolArgs

            # Convertir resultado a texto JSON para el content block
            $resultJson = $result | ConvertTo-Json -Depth 20

            Send-Result $id @{
                content = @(
                    @{
                        type = "text"
                        text = $resultJson
                    }
                )
                isError = ($result.error -ne $null)
            }
        }

        # ── Ping ───────────────────────────────────────────
        "ping" {
            Send-Result $id @{}
        }

        default {
            if ($id -ne $null) {
                Send-Error $id -32601 "Method not found: $method"
            }
        }
    }
}
