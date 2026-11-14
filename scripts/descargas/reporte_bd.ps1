# ============================================================
# reporte_bd.ps1  (v3)
#
# Reporte de cobertura de datos historicos en PostgreSQL +
# resumen de los mejores backtests ejecutados.
#
# Uso:
#   .\reporte_bd.ps1
#   .\reporte_bd.ps1 -Symbol "OTC_NDX"
#   .\reporte_bd.ps1 -Timeframe "1h"
#   .\reporte_bd.ps1 -DetallePendientes
#   .\reporte_bd.ps1 -SoloBacktest
# ============================================================
param(
    [string]$Symbol            = "",
    [string]$Timeframe         = "",
    [string]$Servidor          = "http://localhost:8765",
    [switch]$DetallePendientes,
    [switch]$SoloBacktest
)
# Token de las APIs locales 8765/8766 (ver agent_auth.ps1)
. (Join-Path $PSScriptRoot '..\agent_auth.ps1')


try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
$ErrorActionPreference = "Continue"

$ScriptDir   = $PSScriptRoot
$ProgresoDir = "$ScriptDir\progreso"
$ConfigFile  = "C:\DesarrolloIA\Traiding\WpfDerivClientVB\bin\Any CPU\Debug\net48\db_config.json"
if (-not (Test-Path $ConfigFile)) {
    $ConfigFile = "C:\DesarrolloIA\Traiding\WpfDerivClientVB\bin\Debug\net48\db_config.json"
}

# ============================================================
# Helper: velas esperadas dinamico por tipo de activo
# Se calcula sobre los dias REALMENTE cubiertos en la BD.
# ============================================================
function Get-VelasEsperadas($symbol, $tf, $diasCubiertos) {
    $minPorVela = switch ($tf) {
        "1m"  { 1 }; "5m" { 5 }; "1h" { 60 }; "4h" { 240 }; "1d" { 1440 }
        default { 60 }
    }
    # Horas de trading por dia segun tipo de activo
    if     ($symbol -like "R_*" -or $symbol -like "1HZ*") {
        $horasDia     = 24; $factorSemana = 1.0   # Sinteticos 24/7
    } elseif ($symbol -like "OTC*") {
        $horasDia     = 8;  $factorSemana = 5.0/7  # Indices OTC ~8h/dia, lun-vie
    } else {
        $horasDia     = 24; $factorSemana = 5.0/7  # Forex/metales 24h, lun-vie
    }
    $velasXDia = ($horasDia * 60.0) / $minPorVela
    return [int]($diasCubiertos * $velasXDia * $factorSemana * 0.90)  # 10% tolerancia feriados
}

# ============================================================
# Leer progreso v2 (carpeta progreso/*.json)
# Devuelve hashtable: clave="NOMBRE-TF" -> pendientesCount
# ============================================================
function Load-ProgresoV2 {
    $mapa = @{}
    if (-not (Test-Path $ProgresoDir)) { return $mapa }
    foreach ($f in Get-ChildItem "$ProgresoDir\*.json" -ErrorAction SilentlyContinue) {
        try {
            $raw = Get-Content $f.FullName -Raw | ConvertFrom-Json
            foreach ($prop in $raw.PSObject.Properties) {
                # clave = "NOMBRE-TF" (ej: "EURUSD-1h")
                $mapa[$prop.Name] = @{
                    Pendientes = if ($prop.Value.pendientes) { @($prop.Value.pendientes).Count } else { 0 }
                    Cursor     = if ($prop.Value.cursor) { $prop.Value.cursor } else { "" }
                }
            }
        } catch {}
    }
    return $mapa
}

if ($SoloBacktest) {
    # Solo mostrar seccion de backtests, sin conectar a BD
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host " RESULTADOS DE BACKTESTS DINAMICAS" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
} else {

# ============================================================
# Leer configuracion de BD
# ============================================================
if (-not (Test-Path $ConfigFile)) {
    Write-Host "[ERROR] No se encontro db_config.json en: $ConfigFile" -ForegroundColor Red; exit 1
}
$dbConf  = Get-Content $ConfigFile -Raw | ConvertFrom-Json
$DB_HOST = $dbConf.Host; $DB_PORT = $dbConf.Port
$DB_NAME = $dbConf.Database; $DB_USER = $dbConf.Username; $DB_PASS = $dbConf.Password

# ============================================================
# Localizar psql (local o Docker)
# ============================================================
$psqlExe = $null
foreach ($p in @("psql","C:\Program Files\PostgreSQL\16\bin\psql.exe",
                  "C:\Program Files\PostgreSQL\15\bin\psql.exe",
                  "C:\Program Files\PostgreSQL\14\bin\psql.exe",
                  "C:\Program Files\PostgreSQL\13\bin\psql.exe")) {
    if (Get-Command $p -ErrorAction SilentlyContinue) { $psqlExe = $p; break }
    if (Test-Path $p)                                 { $psqlExe = $p; break }
}

$useDocker = $false; $useDockerRun = $false; $dockerContainer = ""
if (-not $psqlExe) {
    $dockerOk = Get-Command "docker" -ErrorAction SilentlyContinue
    if ($dockerOk) {
        $portFilter = docker ps --format "{{.Names}} {{.Ports}}" 2>$null
        foreach ($line in $portFilter) {
            if ($line -match ":${DB_PORT}->") {
                $dockerContainer = ($line -split " ")[0].Trim()
                $t = docker exec $dockerContainer psql --version 2>$null
                if ($LASTEXITCODE -eq 0) { $useDocker = $true; break }
            }
        }
        if (-not $useDocker) {
            $imgTest = docker image inspect postgres:alpine 2>$null
            if ($LASTEXITCODE -eq 0) { $useDockerRun = $true }
            else { docker pull postgres:alpine 2>&1 | Out-Null; if ($LASTEXITCODE -eq 0) { $useDockerRun = $true } }
        }
    }
    if (-not $useDocker -and -not $useDockerRun) {
        Write-Host "[ERROR] No se encontro psql ni Docker." -ForegroundColor Red; exit 1
    }
}

function Invoke-Psql([string]$Sql) {
    $env:PGPASSWORD = $DB_PASS
    if ($useDocker) {
        $r = docker exec -e "PGPASSWORD=$DB_PASS" $dockerContainer `
             psql -h "127.0.0.1" -p 5432 -U $DB_USER -d $DB_NAME -t -A -F "|" -c $Sql 2>&1
    } elseif ($useDockerRun) {
        $r = docker run --rm -e "PGPASSWORD=$DB_PASS" --network host postgres:alpine `
             psql -h "127.0.0.1" -p $DB_PORT -U $DB_USER -d $DB_NAME -t -A -F "|" -c $Sql 2>&1
        if ($LASTEXITCODE -ne 0) {
            $r = docker run --rm -e "PGPASSWORD=$DB_PASS" postgres:alpine `
                 psql -h "host.docker.internal" -p $DB_PORT -U $DB_USER -d $DB_NAME -t -A -F "|" -c $Sql 2>&1
        }
    } else {
        $r = & $psqlExe -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME -t -A -F "|" -c $Sql 2>&1
    }
    $env:PGPASSWORD = ""
    return $r
}

# ============================================================
# Header
# ============================================================
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " REPORTE DE DATOS HISTORICOS EN BASE DE DATOS" -ForegroundColor Cyan
Write-Host " Host: ${DB_HOST}:${DB_PORT}  DB: $DB_NAME" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

$pingResult = Invoke-Psql "SELECT 1;"
if ($LASTEXITCODE -ne 0 -or ($pingResult -notmatch "1")) {
    Write-Host "[ERROR] No se pudo conectar a PostgreSQL." -ForegroundColor Red; exit 1
}
Write-Host "[OK] Conexion exitosa a PostgreSQL" -ForegroundColor Green

# ============================================================
# Catalogo: servidor (GET /api/activos) con respaldo local
# ============================================================
$CATALOGO_RESPALDO = @(
    @{ Nombre="EURUSD"; Symbol="frxEURUSD" }
    @{ Nombre="GBPUSD"; Symbol="frxGBPUSD" }
    @{ Nombre="USDCHF"; Symbol="frxUSDCHF" }
    @{ Nombre="USDJPY"; Symbol="frxUSDJPY" }
    @{ Nombre="GBPJPY"; Symbol="frxGBPJPY" }
    @{ Nombre="XAUUSD"; Symbol="frxXAUUSD" }
    @{ Nombre="XAGUSD"; Symbol="frxXAGUSD" }
    @{ Nombre="OIL_US"; Symbol="frxUSOIL"  }
    @{ Nombre="NAS100"; Symbol="OTC_NDX"   }
    @{ Nombre="SP500";  Symbol="OTC_SPC"   }
    @{ Nombre="DJ30";   Symbol="OTC_DJI"   }
    @{ Nombre="R_100";  Symbol="R_100"     }
)

$activosRaw = @(); $tfList = @("1d","4h","1h","5m")
try {
    $resp = Invoke-RestMethod -Uri "$Servidor/api/activos" -Method GET -TimeoutSec 5 -ErrorAction Stop
    if ($resp -and $resp.activos) {
        $activosRaw = @($resp.activos); $tfList = @($resp.timeframes)
        Write-Host ("  Catalogo ({0}): {1} activos desde {2}" -f $resp.fuente, $activosRaw.Count, $Servidor) -ForegroundColor Green
    }
} catch {
    Write-Host "  [INFO] Servidor no disponible - usando catalogo de respaldo." -ForegroundColor DarkYellow
}

$CATALOGO = @()
if ($activosRaw.Count -gt 0) {
    foreach ($a in $activosRaw) {
        foreach ($tf in $tfList) {
            $CATALOGO += @{ Id="$($a.nombre)-$tf"; Symbol=$a.symbol; TF=$tf; Nombre=$a.nombre }
        }
    }
} else {
    foreach ($a in $CATALOGO_RESPALDO) {
        foreach ($tf in $tfList) {
            $CATALOGO += @{ Id="$($a.Nombre)-$tf"; Symbol=$a.Symbol; TF=$tf; Nombre=$a.Nombre }
        }
    }
}

# Sinteticos Deriv (R_*, 1HZ*): agregar 1m al catalogo aunque no este en la lista global
foreach ($activo in $activosRaw) {
    if (($activo.symbol -like "R_*" -or $activo.symbol -like "1HZ*") -and "1m" -notin $tfList) {
        $CATALOGO += @{ Id="$($activo.nombre)-1m"; Symbol=$activo.symbol; TF="1m"; Nombre=$activo.nombre }
    }
}
# Mismo tratamiento para el catalogo de respaldo
if ($activosRaw.Count -eq 0) {
    foreach ($a in $CATALOGO_RESPALDO) {
        if (($a.Symbol -like "R_*" -or $a.Symbol -like "1HZ*") -and "1m" -notin $tfList) {
            $CATALOGO += @{ Id="$($a.Nombre)-1m"; Symbol=$a.Symbol; TF="1m"; Nombre=$a.Nombre }
        }
    }
}

if ($Symbol    -ne "") { $CATALOGO = $CATALOGO | Where-Object { $_.Symbol -eq $Symbol -or $_.Nombre -eq $Symbol } }
if ($Timeframe -ne "") { $CATALOGO = $CATALOGO | Where-Object { $_.TF -eq $Timeframe } }

# ============================================================
# Progreso v2 (carpeta progreso/*.json)
# ============================================================
$progreso = Load-ProgresoV2

# ============================================================
# Consulta principal a BD
# ============================================================
Write-Host ""
Write-Host "Consultando base de datos..." -ForegroundColor Cyan

$sqlResumen = @"
SELECT
    symbol,
    timeframe,
    COUNT(*)                                       AS total_velas,
    to_timestamp(MIN(epoch))::date::text           AS fecha_inicio,
    to_timestamp(MAX(epoch))::date::text           AS fecha_fin,
    ROUND((MAX(epoch) - MIN(epoch)) / 86400.0, 1)  AS dias_cubiertos
FROM public.candles
WHERE LOWER(platform) = 'deriv'
GROUP BY symbol, timeframe
ORDER BY symbol, timeframe;
"@

$rawResult = Invoke-Psql $sqlResumen

$bdMap = @{}
foreach ($line in $rawResult) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $p = $line -split "\|"
    if ($p.Count -lt 6) { continue }
    $key = "$($p[0].Trim())|$($p[1].Trim())"
    $dias = try { [double]($p[5].Trim()) } catch { 1 }
    $bdMap[$key] = @{
        TotalVelas    = [long]($p[2].Trim())
        FechaInicio   = $p[3].Trim()
        FechaFin      = $p[4].Trim()
        DiasCubiertos = $dias
    }
}

# ============================================================
# Totales globales
# ============================================================
$totalVelasGlobal  = 0L
$totalCompletos    = 0
$totalParciales    = 0
$totalVacios       = 0

foreach ($combo in $CATALOGO) {
    $key = "$($combo.Symbol)|$($combo.TF)"
    if ($bdMap.ContainsKey($key)) {
        $totalVelasGlobal += $bdMap[$key].TotalVelas
        $esp = Get-VelasEsperadas $combo.Symbol $combo.TF $bdMap[$key].DiasCubiertos
        $esp = [Math]::Max($esp, 1)
        $pct = [math]::Min(100, [math]::Round($bdMap[$key].TotalVelas * 100.0 / $esp, 0))
        if ($pct -ge 80) { $totalCompletos++ } else { $totalParciales++ }
    } else { $totalVacios++ }
}

# ============================================================
# Tabla por activo
# ============================================================
$hdr = "{0,-8} {1,-5} {2,12} {3,12} {4,12} {5,6} {6,7} {7,-12}" -f `
       "ACTIVO","TF","VELAS BD","DESDE","HASTA","DIAS","COMPL%","ESTADO"
$sep = "-" * 82

$activos = $CATALOGO | ForEach-Object { $_["Nombre"] } | Select-Object -Unique

foreach ($activo in $activos) {
    $combosActivo = $CATALOGO | Where-Object { $_["Nombre"] -eq $activo }
    Write-Host ""
    Write-Host "  [$activo]" -ForegroundColor Yellow
    Write-Host "  $sep"
    Write-Host "  $hdr" -ForegroundColor Cyan
    Write-Host "  $sep"

    foreach ($combo in $combosActivo) {
        $key     = "$($combo.Symbol)|$($combo.TF)"
        $progKey = "$($combo.Nombre)-$($combo.TF)"

        if ($bdMap.ContainsKey($key)) {
            $d   = $bdMap[$key]
            $esp = [Math]::Max(1, (Get-VelasEsperadas $combo.Symbol $combo.TF $d.DiasCubiertos))
            $pct = [math]::Min(100, [math]::Round($d.TotalVelas * 100.0 / $esp, 0))

            $nPend   = if ($progreso.ContainsKey($progKey)) { $progreso[$progKey].Pendientes } else { 0 }
            $pendStr = if ($nPend -gt 0) { " [!$nPend pend]" } else { "" }

            if ($pct -ge 80)      { $estado = "[COMPLETO]"; $color = "Green"     }
            elseif ($pct -ge 40)  { $estado = "[PARCIAL]";  $color = "Yellow"    }
            elseif ($pct -gt 0)   { $estado = "[INICIO]";   $color = "DarkYellow"}
            else                  { $estado = "[MINIMO]";   $color = "Red"       }

            $linea = "  {0,-8} {1,-5} {2,12} {3,12} {4,12} {5,6} {6,7}% {7,-12}{8}" -f `
                $combo.Nombre, $combo.TF,
                ("{0:N0}" -f $d.TotalVelas),
                $d.FechaInicio, $d.FechaFin,
                [int]$d.DiasCubiertos, $pct,
                $estado, $pendStr
            Write-Host $linea -ForegroundColor $color
        } else {
            $linea = "  {0,-8} {1,-5} {2,12} {3,12} {4,12} {5,6} {6,7}% {7,-12}" -f `
                $combo.Nombre, $combo.TF, "0","-","-","0","0","[SIN DATOS]"
            Write-Host $linea -ForegroundColor DarkGray
        }
    }
}

# ============================================================
# Pendientes detallados
# ============================================================
if ($DetallePendientes) {
    $hayPend = $false
    foreach ($k in ($progreso.Keys | Sort-Object)) {
        if ($progreso[$k].Pendientes -eq 0) { continue }
        if (-not $hayPend) {
            Write-Host ""
            Write-Host "  RANGOS PENDIENTES (progreso/):" -ForegroundColor Yellow
            $hayPend = $true
        }
        Write-Host "  [$k]  cursor: $($progreso[$k].Cursor)  pendientes: $($progreso[$k].Pendientes)" -ForegroundColor DarkYellow
    }
    if (-not $hayPend) { Write-Host "  Sin rangos pendientes." -ForegroundColor Green }
}

# ============================================================
# Simbolos en BD fuera del catalogo
# ============================================================
$sqlExtra = "SELECT DISTINCT symbol, timeframe FROM public.candles WHERE LOWER(platform)='deriv' ORDER BY symbol, timeframe;"
$extraRaw = Invoke-Psql $sqlExtra
$extra    = @()
foreach ($line in $extraRaw) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $p = $line -split "\|"; if ($p.Count -lt 2) { continue }
    $sym = $p[0].Trim(); $tf = $p[1].Trim()
    if (-not ($CATALOGO | Where-Object { $_.Symbol -eq $sym -and $_.TF -eq $tf })) {
        $extra += "$sym / $tf"
    }
}
if ($extra.Count -gt 0) {
    Write-Host ""
    Write-Host "  Otros simbolos en BD (no en catalogo):" -ForegroundColor Cyan
    foreach ($e in $extra) { Write-Host "    $e" -ForegroundColor Gray }
}

# ============================================================
# Resumen global
# ============================================================
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " RESUMEN GLOBAL" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
$totalPend = ($progreso.Values | ForEach-Object { $_.Pendientes } | Measure-Object -Sum).Sum
Write-Host ("  Total velas en BD    : {0:N0}" -f $totalVelasGlobal)        -ForegroundColor White
Write-Host ("  Combinaciones        : {0}"    -f $CATALOGO.Count)          -ForegroundColor White
Write-Host ("  Completas (>= 80%%)  : {0}"    -f $totalCompletos)           -ForegroundColor Green
Write-Host ("  Parciales            : {0}"    -f $totalParciales)           -ForegroundColor Yellow
Write-Host ("  Sin datos            : {0}"    -f $totalVacios)              -ForegroundColor DarkGray
Write-Host ("  Rangos pendientes    : {0}"    -f [int]$totalPend)           -ForegroundColor $(if ($totalPend -gt 0) {"Yellow"} else {"Green"})

} # fin bloque NOT SoloBacktest

# ============================================================
# SECCION: MEJORES BACKTESTS (necesita servidor corriendo)
# ============================================================
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " MEJORES BACKTESTS EJECUTADOS" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

try {
    $sesResp = Invoke-RestMethod -Uri "$Servidor/api/dinamicas/sesiones?limite=50" -Method GET -TimeoutSec 10 -ErrorAction Stop
    if ($sesResp -and $sesResp.sesiones -and $sesResp.sesiones.Count -gt 0) {
        # Filtrar solo rentables (win_rate > 54 con payout 85%)
        $todas      = @($sesResp.sesiones)
        $rentables  = @($todas | Where-Object { $_.win_rate -gt 54 } | Sort-Object win_rate -Descending)
        $perdedoras = @($todas | Where-Object { $_.win_rate -le 54 })

        if ($rentables.Count -gt 0) {
            Write-Host ""
            Write-Host "  RENTABLES (win_rate > 54%):" -ForegroundColor Green
            $hdrB = "  {0,-25} {1,-8} {2,-5} {3,5} {4,8} {5,10} {6,7} {7,8}" -f `
                    "ESTRATEGIA","SYMBOL","TF","DUR","WIN%","P&L","PF","OPS"
            Write-Host $hdrB -ForegroundColor Cyan
            Write-Host ("  " + "-" * 78)

            foreach ($s in $rentables) {
                $color = if ($s.win_rate -ge 60) { "Green" } elseif ($s.win_rate -ge 57) { "Yellow" } else { "DarkYellow" }
                $linea = "  {0,-25} {1,-8} {2,-5} {3,5} {4,8} {5,10} {6,7} {7,8}" -f `
                    $s.estrategia, $s.symbol, $s.timeframe,
                    $s.duracion_velas,
                    ("{0:F1}%" -f $s.win_rate),
                    ("{0:+0.00;-0.00}" -f $s.profit_loss),
                    ("{0:F2}" -f $s.profit_factor),
                    $s.total_ops
                Write-Host $linea -ForegroundColor $color
            }
        }

        if ($perdedoras.Count -gt 0) {
            Write-Host ""
            Write-Host ("  PERDEDORAS: {0} sesiones (win_rate <= 54%)" -f $perdedoras.Count) -ForegroundColor DarkGray
            $mejorPerd = $perdedoras | Sort-Object win_rate -Descending | Select-Object -First 3
            foreach ($s in $mejorPerd) {
                Write-Host ("    {0,-25} {1,-6} {2,-4} | WR:{3:F1}% OPS:{4}" -f `
                    $s.estrategia, $s.symbol, $s.timeframe, $s.win_rate, $s.total_ops) -ForegroundColor DarkGray
            }
        }

        Write-Host ""
        Write-Host ("  Total sesiones: {0} | Rentables: {1} | Perdedoras: {2}" -f `
            $todas.Count, $rentables.Count, $perdedoras.Count) -ForegroundColor Cyan
        Write-Host "  Usa la app -> tab Estrategias para ver operaciones detalladas." -ForegroundColor DarkGray

    } else {
        Write-Host "  Sin sesiones guardadas aun. Ejecuta un backtest desde la app." -ForegroundColor DarkGray
    }
} catch {
    Write-Host "  [INFO] Servidor no disponible - seccion de backtests omitida." -ForegroundColor DarkGray
    Write-Host "         Ejecuta la app y vuelve a correr el reporte." -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "  Generado: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  Comandos utiles:" -ForegroundColor DarkGray
Write-Host "    .\descargar_masivo.ps1              -> descarga Deriv reciente" -ForegroundColor DarkGray
Write-Host "    .\importar_historico.ps1             -> importa Yahoo (10 anos 1d, 2 anos 1h)" -ForegroundColor DarkGray
Write-Host "    .\descargar_masivo.ps1 -VerPendientes -> ver rangos fallidos" -ForegroundColor DarkGray
Write-Host ""
