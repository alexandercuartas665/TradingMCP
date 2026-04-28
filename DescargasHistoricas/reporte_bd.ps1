# ============================================================
# reporte_bd.ps1
#
# Consulta PostgreSQL y muestra un reporte de cobertura de datos
# historicos descargados para cada activo/temporalidad.
#
# Uso:
#   .\reporte_bd.ps1
#   .\reporte_bd.ps1 -Symbol "frxEURUSD"
#   .\reporte_bd.ps1 -Timeframe "1h"
#   .\reporte_bd.ps1 -DetallePendientes
# ============================================================
param(
    [string]$Symbol         = "",
    [string]$Timeframe      = "",
    [string]$Servidor       = "http://localhost:8765",
    [switch]$DetallePendientes
)

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
$ErrorActionPreference = "Continue"

$ScriptDir    = $PSScriptRoot
$ProgressFile = "$ScriptDir\progreso.json"
$ConfigFile   = "C:\DesarrolloIA\Traiding\WpfDerivClientVB\bin\Debug\net48\db_config.json"

# ============================================================
# Leer configuracion de base de datos
# ============================================================
if (-not (Test-Path $ConfigFile)) {
    Write-Host "[ERROR] No se encontro db_config.json en: $ConfigFile" -ForegroundColor Red
    exit 1
}

$dbConf = Get-Content $ConfigFile -Raw | ConvertFrom-Json
$DB_HOST = $dbConf.Host
$DB_PORT = $dbConf.Port
$DB_NAME = $dbConf.Database
$DB_USER = $dbConf.Username
$DB_PASS = $dbConf.Password

# ============================================================
# Localizar psql
# ============================================================
$psqlPaths = @(
    "psql",
    "C:\Program Files\PostgreSQL\16\bin\psql.exe",
    "C:\Program Files\PostgreSQL\15\bin\psql.exe",
    "C:\Program Files\PostgreSQL\14\bin\psql.exe",
    "C:\Program Files\PostgreSQL\13\bin\psql.exe",
    "C:\Program Files\PostgreSQL\12\bin\psql.exe"
)

$psqlExe = $null
foreach ($p in $psqlPaths) {
    try {
        $found = Get-Command $p -ErrorAction SilentlyContinue
        if ($found) { $psqlExe = $p; break }
    } catch {}
    if (Test-Path $p) { $psqlExe = $p; break }
}

# Intentar via docker si no hay psql local
# Estrategia 1: buscar el contenedor que mapea exactamente el puerto de la BD
# Estrategia 2: usar "docker run --rm" con imagen postgres para conectar al host
$useDocker      = $false
$useDockerRun   = $false
$dockerContainer = ""

if (-not $psqlExe) {
    $dockerOk = Get-Command "docker" -ErrorAction SilentlyContinue
    if ($dockerOk) {

        # -- Estrategia 1: contenedor que publica el puerto DB_PORT ---------------
        $portFilter = docker ps --format "{{.Names}} {{.Ports}}" 2>$null
        foreach ($line in $portFilter) {
            if ($line -match ":${DB_PORT}->") {
                $dockerContainer = ($line -split " ")[0].Trim()
                # Verificar que tenga psql
                $testPsql = docker exec $dockerContainer psql --version 2>$null
                if ($LASTEXITCODE -eq 0) {
                    $useDocker = $true
                    Write-Host "[INFO] Contenedor con puerto $DB_PORT detectado: $dockerContainer" -ForegroundColor Cyan
                    break
                }
            }
        }

        # -- Estrategia 2: docker run con imagen postgres (psql client) -----------
        if (-not $useDocker) {
            # Intentar conectar desde host usando docker run con --network host
            $imgTest = docker image inspect postgres:alpine 2>$null
            if ($LASTEXITCODE -eq 0) {
                $useDockerRun = $true
                Write-Host "[INFO] Usando docker run postgres:alpine para conectar a $DB_HOST`:$DB_PORT" -ForegroundColor Cyan
            } else {
                # No hay imagen local, intentar pull silencioso
                Write-Host "[INFO] Descargando imagen postgres:alpine para psql client..." -ForegroundColor Cyan
                docker pull postgres:alpine 2>&1 | Out-Null
                if ($LASTEXITCODE -eq 0) { $useDockerRun = $true }
            }
        }
    }

    if (-not $useDocker -and -not $useDockerRun) {
        Write-Host "[ERROR] No se encontro psql instalado ni contenedor Docker con psql." -ForegroundColor Red
        Write-Host "        Opciones:" -ForegroundColor Yellow
        Write-Host "          1) Instalar PostgreSQL client y agregar psql al PATH" -ForegroundColor Yellow
        Write-Host "          2) Ejecutar desde el directorio donde esta psql.exe" -ForegroundColor Yellow
        Write-Host "          3) Asegurarse que Docker Desktop este corriendo" -ForegroundColor Yellow
        exit 1
    }
}

# ============================================================
# Funcion: ejecutar query SQL y devolver texto
# ============================================================
function Invoke-Psql {
    param([string]$Sql)

    $env:PGPASSWORD = $DB_PASS

    if ($useDocker) {
        # El contenedor tiene mapeado DB_PORT -> 5432 interno, conectamos por dentro
        $result = docker exec -e "PGPASSWORD=$DB_PASS" $dockerContainer `
            psql -h "127.0.0.1" -p 5432 -U $DB_USER -d $DB_NAME `
            -t -A -F "|" -c $Sql 2>&1
    } elseif ($useDockerRun) {
        # Usamos docker run para lanzar psql client que conecta al host
        # --network host: el contenedor comparte red con el host (Linux/WSL2)
        # Si falla, intentar con host.docker.internal
        $result = docker run --rm `
            -e "PGPASSWORD=$DB_PASS" `
            --network host `
            postgres:alpine `
            psql -h "127.0.0.1" -p $DB_PORT -U $DB_USER -d $DB_NAME `
            -t -A -F "|" -c $Sql 2>&1
        if ($LASTEXITCODE -ne 0) {
            # Fallback: host.docker.internal (Docker Desktop en Windows)
            $result = docker run --rm `
                -e "PGPASSWORD=$DB_PASS" `
                postgres:alpine `
                psql -h "host.docker.internal" -p $DB_PORT -U $DB_USER -d $DB_NAME `
                -t -A -F "|" -c $Sql 2>&1
        }
    } else {
        $result = & $psqlExe `
            -h $DB_HOST -p $DB_PORT `
            -U $DB_USER -d $DB_NAME `
            -t -A -F "|" -c $Sql 2>&1
    }

    $env:PGPASSWORD = ""
    return $result
}

# ============================================================
# Verificar conexion
# ============================================================
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " REPORTE DE DATOS HISTORICOS EN BASE DE DATOS" -ForegroundColor Cyan
Write-Host " Host: ${DB_HOST}:${DB_PORT}  DB: $DB_NAME" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

$pingResult = Invoke-Psql "SELECT 1;"
if ($LASTEXITCODE -ne 0 -or ($pingResult -notmatch "1")) {
    Write-Host "[ERROR] No se pudo conectar a PostgreSQL." -ForegroundColor Red
    Write-Host "        Respuesta: $pingResult" -ForegroundColor Yellow
    exit 1
}
Write-Host "[OK] Conexion exitosa a PostgreSQL" -ForegroundColor Green
Write-Host ""

# ============================================================
# Catalogo esperado: se consulta al servidor (GET /api/activos)
# Si el servidor no esta disponible, se usa el catalogo de respaldo.
# ============================================================
$CATALOGO_RESPALDO = @(
    @{ Nombre="EURUSD"; Symbol="frxEURUSD"; TFs=@("1d","4h","1h","5m") }
    @{ Nombre="GBPUSD"; Symbol="frxGBPUSD"; TFs=@("1d","4h","1h","5m") }
    @{ Nombre="USDCHF"; Symbol="frxUSDCHF"; TFs=@("1d","4h","1h","5m") }
    @{ Nombre="USDJPY"; Symbol="frxUSDJPY"; TFs=@("1d","4h","1h","5m") }
    @{ Nombre="GBPJPY"; Symbol="frxGBPJPY"; TFs=@("1d","4h","1h","5m") }
    @{ Nombre="XAUUSD"; Symbol="frxXAUUSD"; TFs=@("1d","4h","1h","5m") }
    @{ Nombre="XAGUSD"; Symbol="frxXAGUSD"; TFs=@("1d","4h","1h","5m") }
    @{ Nombre="OIL_US"; Symbol="frxUSOIL";  TFs=@("1d","4h","1h","5m") }
    @{ Nombre="NAS100"; Symbol="OTC_NDX";   TFs=@("1d","4h","1h","5m") }
    @{ Nombre="SP500";  Symbol="OTC_SPC";   TFs=@("1d","4h","1h","5m") }
)

# Intentar obtener catalogo del servidor
$activosRaw = @()
$tfList     = @("1d","4h","1h","5m")
try {
    $resp = Invoke-RestMethod -Uri "$Servidor/api/activos" -Method GET -TimeoutSec 5 -ErrorAction Stop
    if ($resp -and $resp.activos) {
        $activosRaw = @($resp.activos)
        $tfList     = @($resp.timeframes)
        Write-Host ("  Catalogo ({0}): {1} activos desde $Servidor" -f $resp.fuente, $activosRaw.Count) -ForegroundColor Green
    }
} catch {
    Write-Host "  [INFO] Servidor no disponible - usando catalogo de respaldo." -ForegroundColor DarkYellow
}

# Construir combinaciones (producto cartesiano activos x TFs)
$CATALOGO = @()
if ($activosRaw.Count -gt 0) {
    foreach ($a in $activosRaw) {
        foreach ($tf in $tfList) {
            $CATALOGO += @{ Id="$($a.nombre)-$tf"; Symbol=$a.symbol; TF=$tf; Nombre=$a.nombre }
        }
    }
} else {
    foreach ($a in $CATALOGO_RESPALDO) {
        foreach ($tf in $a.TFs) {
            $CATALOGO += @{ Id="$($a.Nombre)-$tf"; Symbol=$a.Symbol; TF=$tf; Nombre=$a.Nombre }
        }
    }
}

# Filtros opcionales
if ($Symbol -ne "") {
    $CATALOGO = $CATALOGO | Where-Object { $_.Symbol -eq $Symbol -or $_.Nombre -eq $Symbol }
}
if ($Timeframe -ne "") {
    $CATALOGO = $CATALOGO | Where-Object { $_.TF -eq $Timeframe }
}

# ============================================================
# Cargar progreso.json
# ============================================================
$progreso = @{}
if (Test-Path $ProgressFile) {
    try {
        $progreso = Get-Content $ProgressFile -Raw | ConvertFrom-Json
    } catch {
        Write-Host "[WARN] No se pudo leer progreso.json" -ForegroundColor Yellow
    }
}

# ============================================================
# Consulta principal a la BD: resumen por symbol+timeframe
# ============================================================
Write-Host "Consultando base de datos..." -ForegroundColor Cyan

$sqlResumen = @"
SELECT
    symbol,
    timeframe,
    COUNT(*)                                          AS total_velas,
    to_timestamp(MIN(epoch))::date::text              AS fecha_inicio,
    to_timestamp(MAX(epoch))::date::text              AS fecha_fin,
    ROUND((MAX(epoch) - MIN(epoch)) / 86400.0, 1)     AS dias_cubiertos
FROM public.candles
WHERE LOWER(platform) = 'deriv'
GROUP BY symbol, timeframe
ORDER BY symbol, timeframe;
"@

$rawResult = Invoke-Psql $sqlResumen

# Construir mapa de resultados BD
$bdMap = @{}
foreach ($line in $rawResult) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $parts = $line -split "\|"
    if ($parts.Count -lt 6) { continue }
    $key = "$($parts[0].Trim())|$($parts[1].Trim())"
    $bdMap[$key] = @{
        TotalVelas    = [long]($parts[2].Trim())
        FechaInicio   = $parts[3].Trim()
        FechaFin      = $parts[4].Trim()
        DiasCubiertos = [double]($parts[5].Trim())
    }
}

# ============================================================
# Velas esperadas por timeframe segun la fuente de datos real
#
#   1d  -> Yahoo Finance range=10y  (~260 dias/ano * 10 = 2600)
#   1h  -> Yahoo Finance 730 dias   (730 * 24 * 5/7 = ~12 500)
#   4h  -> Yahoo Finance 730 dias   (730 *  6 * 5/7 =  ~3 100)
#   5m  -> Deriv API ~380 dias      (380 * 288 * 5/7 = ~15 600)
#
# Se usa el 85% como umbral de COMPLETO (margen para feriados,
# gaps de mercado y diferencias entre activos).
# ============================================================
$VELAS_ESPERADAS = @{
    "1d" = 2600
    "4h" = 3100
    "1h" = 12500
    "5m" = 15600
}

# ============================================================
# Calcular totales para resumen global
# ============================================================
$totalVelasGlobal    = 0L
$totalCompletosGlobal = 0
$totalParciales       = 0
$totalVacios          = 0

# Pre-scan para totales
foreach ($combo in $CATALOGO) {
    $bdKey = "$($combo.Symbol)|$($combo.TF)"
    if ($bdMap.ContainsKey($bdKey)) {
        $totalVelasGlobal += $bdMap[$bdKey].TotalVelas
        $esperado = $VELAS_ESPERADAS[$combo.TF]
        $pct = [math]::Min(100, [math]::Round($bdMap[$bdKey].TotalVelas * 100.0 / $esperado, 0))
        if ($pct -ge 85) { $totalCompletosGlobal++ }
        else { $totalParciales++ }
    } else {
        $totalVacios++
    }
}

# ============================================================
# SALIDA: tabla por activo
# ============================================================
$encabezado = "{0,-8} {1,-5} {2,12} {3,12} {4,12} {5,8} {6,8} {7,-10}" -f `
    "ACTIVO", "TF", "VELAS BD", "DESDE", "HASTA", "DIAS", "COMPLET%", "ESTADO"

$separador = "-" * 84

# Agrupar por nombre de activo (ForEach-Object porque $CATALOGO es array de hashtables)
$activos = $CATALOGO | ForEach-Object { $_["Nombre"] } | Select-Object -Unique

foreach ($activo in $activos) {
    $combosActivo = $CATALOGO | Where-Object { $_["Nombre"] -eq $activo }

    Write-Host ""
    Write-Host "  [$activo]" -ForegroundColor Yellow
    Write-Host "  $separador"
    Write-Host "  $encabezado" -ForegroundColor Cyan
    Write-Host "  $separador"

    foreach ($combo in $combosActivo) {
        $bdKey     = "$($combo.Symbol)|$($combo.TF)"
        $progKey   = "$($combo.Nombre)-$($combo.TF)"
        $esperado  = $VELAS_ESPERADAS[$combo.TF]

        if ($bdMap.ContainsKey($bdKey)) {
            $d        = $bdMap[$bdKey]
            $pct      = [math]::Min(100, [math]::Round($d.TotalVelas * 100.0 / $esperado, 0))
            $velasStr = "{0:N0}" -f $d.TotalVelas

            # Pendientes en progreso.json
            $nPend = 0
            if ($progreso.PSObject.Properties[$progKey]) {
                $nPend = $progreso.$progKey.pendientes.Count
            }
            $pendStr = if ($nPend -gt 0) { " [!$nPend pend]" } else { "" }

            # Estado y color
            if ($pct -ge 85) {
                $estado = "[COMPLETO]"
                $color  = "Green"
            } elseif ($pct -ge 50) {
                $estado = "[PARCIAL]"
                $color  = "Yellow"
            } elseif ($pct -gt 0) {
                $estado = "[INICIO]"
                $color  = "DarkYellow"
            } else {
                $estado = "[MINIMO]"
                $color  = "Red"
            }

            $linea = "  {0,-8} {1,-5} {2,12} {3,12} {4,12} {5,8} {6,7}% {7,-10}{8}" -f `
                $combo.Nombre, $combo.TF, $velasStr,
                $d.FechaInicio, $d.FechaFin,
                [int]$d.DiasCubiertos, $pct,
                $estado, $pendStr

            Write-Host $linea -ForegroundColor $color

        } else {
            # No hay datos en BD
            $linea = "  {0,-8} {1,-5} {2,12} {3,12} {4,12} {5,8} {6,7}% {7,-10}" -f `
                $combo.Nombre, $combo.TF,
                "0", "-", "-", "0", "0",
                "[SIN DATOS]"
            Write-Host $linea -ForegroundColor DarkGray
        }
    }
}

# ============================================================
# RESUMEN GLOBAL
# ============================================================
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " RESUMEN GLOBAL" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ("  Total velas en BD   : {0:N0}" -f $totalVelasGlobal) -ForegroundColor White
Write-Host ("  Combinaciones       : {0}" -f $CATALOGO.Count) -ForegroundColor White
Write-Host ("  Completas (>= 90%%) : {0}" -f $totalCompletosGlobal) -ForegroundColor Green
Write-Host ("  Parciales           : {0}" -f $totalParciales) -ForegroundColor Yellow
Write-Host ("  Sin datos           : {0}" -f $totalVacios) -ForegroundColor DarkGray

# Pendientes globales de progreso.json
$totalPendientesJSON = 0
foreach ($k in $progreso.PSObject.Properties.Name) {
    $totalPendientesJSON += $progreso.$k.pendientes.Count
}
if ($totalPendientesJSON -gt 0) {
    Write-Host ("  Rangos pendientes   : {0} (ver -DetallePendientes)" -f $totalPendientesJSON) -ForegroundColor Yellow
} else {
    Write-Host "  Rangos pendientes   : 0" -ForegroundColor Green
}

# ============================================================
# DETALLE DE PENDIENTES (si se pidio)
# ============================================================
if ($DetallePendientes -and $totalPendientesJSON -gt 0) {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Yellow
    Write-Host " RANGOS PENDIENTES (progreso.json)" -ForegroundColor Yellow
    Write-Host "============================================================" -ForegroundColor Yellow

    foreach ($k in ($progreso.PSObject.Properties.Name | Sort-Object)) {
        $est   = $progreso.$k
        $pends = $est.pendientes
        if ($pends.Count -eq 0) { continue }

        Write-Host ""
        Write-Host "  [$k]  cursor: $($est.cursor)" -ForegroundColor Yellow
        foreach ($p in $pends) {
            $intentos = if ($p.PSObject.Properties["intentos"]) { $p.intentos } else { "?" }
            Write-Host ("    desde {0}  hasta {1}  (intento {2})" -f $p.desde, $p.hasta, $intentos) -ForegroundColor DarkYellow
        }
    }
}

# ============================================================
# Simbolos desconocidos en BD (no estan en el catalogo)
# ============================================================
$sqlExtra = "SELECT DISTINCT symbol, timeframe FROM public.candles WHERE LOWER(platform)='deriv' ORDER BY symbol, timeframe;"
$extraRaw = Invoke-Psql $sqlExtra

$extra = @()
foreach ($line in $extraRaw) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $parts = $line -split "\|"
    if ($parts.Count -lt 2) { continue }
    $sym = $parts[0].Trim(); $tf = $parts[1].Trim()
    $enCatalogo = $CATALOGO | Where-Object { $_.Symbol -eq $sym -and $_.TF -eq $tf }
    if (-not $enCatalogo) {
        $extra += "$sym / $tf"
    }
}

if ($extra.Count -gt 0) {
    Write-Host ""
    Write-Host "  Otros simbolos en BD (fuera del catalogo):" -ForegroundColor Cyan
    foreach ($e in $extra) {
        Write-Host "    $e" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "  Generado: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor DarkGray
Write-Host ""
