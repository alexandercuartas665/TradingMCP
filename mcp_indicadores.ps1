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
