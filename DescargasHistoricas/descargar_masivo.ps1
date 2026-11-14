# ============================================================
# descargar_masivo.ps1  (v2 - alta velocidad)
#
# Mejoras sobre v1:
#   - Chunks ADAPTATIVOS por timeframe (hasta 400x mas rapidos en 1d/4h)
#   - Delay en SEGUNDOS por timeframe (no minutos fijos para todos)
#   - Descarga PARALELA de simbolos (MaxParalelo, default 3)
#   - Progreso individual por simbolo en carpeta progreso/
#   - Migracion automatica desde progreso.json v1
#
# LIMITE DEL API DERIV (verificado empiricamente):
#   El API solo tiene ~380 dias de historia disponible.
#   No importa que fecha se pida, siempre devuelve desde hoy hacia atras.
#   Por eso Desde se limita automaticamente a 380 dias atras.
#   Para datos mas antiguos usar: .\importar_historico.ps1 (Stooq/Yahoo)
#
# Limites velas por conexion WebSocket:
#   1d  -> 2000 dias/chunk = ~2000 velas  OK
#   4h  -> 1000 dias/chunk = ~6000 velas  OK
#   1h  -> 400  dias/chunk = ~9600 velas  OK
#   5m  -> 55   dias/chunk = ~15840 velas OK
#
# Uso:
#   .\descargar_masivo.ps1
#   .\descargar_masivo.ps1 -MaxParalelo 5
#   .\descargar_masivo.ps1 -SoloActivo "frxEURUSD" -SoloTF "5m"
#   .\descargar_masivo.ps1 -Resetear
#   .\descargar_masivo.ps1 -VerPendientes
# ============================================================
param(
    [int]$MaxParalelo    = 3,
    [int]$MaxReintentos  = 3,
    [string]$Desde       = "",          # default: calculado automaticamente segun limite API
    [string]$Hasta       = (Get-Date -Format "yyyy-MM-dd"),
    [string]$Server      = "http://localhost:8765",
    [string]$SoloActivo  = "",
    [string]$SoloTF      = "",
    [switch]$Resetear,
    [switch]$VerPendientes
)
# Token de las APIs locales 8765/8766 (ver agent_auth.ps1)
. (Join-Path $PSScriptRoot '..\agent_auth.ps1')


try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
$ErrorActionPreference = "Continue"

# ============================================================
# LIMITE REAL DEL API DE DERIV (verificado: ~380 dias max)
# Pendientes mas antiguos que esto se descartan automaticamente.
# ============================================================
$DERIV_API_LIMITE_DIAS = 380
$DERIV_API_INICIO = (Get-Date).AddDays(-$DERIV_API_LIMITE_DIAS)

# Si no se especifico Desde, usar el limite real del API
if ($Desde -eq "") {
    $Desde = $DERIV_API_INICIO.ToString("yyyy-MM-dd")
}

$ScriptDir    = $PSScriptRoot
$ProgresoDir  = "$ScriptDir\progreso"
$ProgresoV1   = "$ScriptDir\progreso.json"

# Chunks adaptativos por TF (dias por peticion)
$CHUNK_POR_TF  = @{ "1d" = 2000; "4h" = 1000; "1h" = 400; "5m" = 55; "1m" = 10 }
# Delay en segundos entre chunks
$DELAY_POR_TF  = @{ "1d" = 3;    "4h" = 5;    "1h" = 10;  "5m" = 30; "1m" = 60 }

# ============================================================
# CATALOGO: se consulta al servidor en tiempo de ejecucion
# El servidor expone GET /api/activos  (lee activos_descarga.json
# o devuelve el catalogo por defecto si el archivo no existe).
# Para editar el catalogo: modificar activos_descarga.json en el
# directorio del exe, o hacer POST /api/activos con el nuevo JSON.
# La variable $COMBINACIONES se construye dinamicamente mas abajo,
# despues de verificar la conexion con el servidor.
# ============================================================
$COMBINACIONES = @()   # se llena despues del ping al servidor

# ============================================================
# HELPERS DE PROGRESO (archivos por simbolo)
# El nombre del archivo se basa en el Nombre del activo: EURUSD.json
# ============================================================

function Get-ProgresoFile($nombre) {
    return "$ProgresoDir\$nombre.json"
}

function Load-ProgresoSimbolo($nombre) {
    $file = Get-ProgresoFile $nombre
    if (-not (Test-Path $file)) { return @{} }
    try {
        $raw = Get-Content $file -Raw | ConvertFrom-Json
        $result = @{}
        $limitStr = $DERIV_API_INICIO.ToString("yyyy-MM-dd")

        foreach ($prop in $raw.PSObject.Properties) {
            $v = $prop.Value
            if ($v -is [string]) {
                $result[$prop.Name] = @{ cursor = $v; pendientes = @() }
            } else {
                # Auto-descartar pendientes mas viejos que el limite del API
                $pendFiltrados = @($v.pendientes | Where-Object {
                    $_.hasta -gt $limitStr
                })
                $descartados = $v.pendientes.Count - $pendFiltrados.Count
                if ($descartados -gt 0) {
                    Write-Host "  [AUTO-DESCARTADO] $descartados rangos de $($prop.Name) fuera del limite del API ($limitStr)" -ForegroundColor DarkGray
                }

                $result[$prop.Name] = @{
                    cursor     = $v.cursor
                    pendientes = @($pendFiltrados | ForEach-Object {
                        @{ desde = $_.desde; hasta = $_.hasta; intentos = if ($_.intentos) { [int]$_.intentos } else { 1 } }
                    })
                }
            }
        }
        return $result
    } catch { return @{} }
}

function Save-ProgresoSimbolo($nombre, $prog) {
    if (-not (Test-Path $ProgresoDir)) { New-Item -ItemType Directory -Path $ProgresoDir | Out-Null }
    $prog | ConvertTo-Json -Depth 5 | Set-Content (Get-ProgresoFile $nombre) -Encoding UTF8
}

# ============================================================
# MIGRACION desde progreso.json v1 (si existe)
# ============================================================
function Migrate-V1 {
    if (-not (Test-Path $ProgresoV1)) { return }
    try {
        $raw = Get-Content $ProgresoV1 -Raw | ConvertFrom-Json
        if (-not (Test-Path $ProgresoDir)) { New-Item -ItemType Directory -Path $ProgresoDir | Out-Null }

        # Agrupar claves por nombre de activo (EURUSD-1h -> EURUSD)
        $grupos = @{}
        foreach ($prop in $raw.PSObject.Properties) {
            $partes  = $prop.Name -split "-"
            $activo  = $partes[0]
            if (-not $grupos.ContainsKey($activo)) { $grupos[$activo] = @{} }
            $v = $prop.Value
            if ($v -is [string]) {
                $grupos[$activo][$prop.Name] = @{ cursor = $v; pendientes = @() }
            } else {
                $grupos[$activo][$prop.Name] = @{
                    cursor     = $v.cursor
                    pendientes = @($v.pendientes | ForEach-Object {
                        @{ desde = $_.desde; hasta = $_.hasta; intentos = if ($_.intentos) { [int]$_.intentos } else { 1 } }
                    })
                }
            }
        }

        foreach ($activo in $grupos.Keys) {
            $file = Get-ProgresoFile $activo
            if (-not (Test-Path $file)) {
                $grupos[$activo] | ConvertTo-Json -Depth 5 | Set-Content $file -Encoding UTF8
                Write-Host "  Migrado: $activo.json" -ForegroundColor DarkGray
            }
        }

        # Renombrar el v1 para no migrar dos veces
        Rename-Item $ProgresoV1 "$ProgresoV1.migrado" -Force
        Write-Host "  progreso.json migrado a carpeta progreso/ (el original queda como .migrado)" -ForegroundColor Yellow
    } catch {
        Write-Host "  [WARN] No se pudo migrar progreso.json: $_" -ForegroundColor Yellow
    }
}

# ============================================================
# API HELPERS
# ============================================================
function Invoke-Api($path) {
    try { return Invoke-RestMethod -Uri "$Server$path" -Method GET -TimeoutSec 90 }
    catch { return $null }
}

function Wait-Chunk([int]$MaxSeg = 300) {
    $t = 0
    do {
        Start-Sleep -Seconds 5
        $t += 5
        $e = Invoke-Api "/api/historico/estado"
        if ($e -ne $null -and -not $e.esta_activo) { return $e }
    } while ($t -lt $MaxSeg)
    return $null
}

function Descargar-Chunk($symbol, $tf, $desdeStr, $hastaStr) {
    $url  = "/api/historico/descargar?symbol=$symbol&timeframe=$([uri]::EscapeDataString($tf))&desde=$desdeStr&hasta=$hastaStr"
    $resp = Invoke-Api $url
    if ($resp -eq $null -or -not $resp.ok) { return $null }
    return Wait-Chunk
}

function Format-Eta([int]$chunks, [int]$delaySeg) {
    $seg = $chunks * $delaySeg
    if ($seg -lt 60)   { return "$seg seg" }
    if ($seg -lt 3600) { return "$([int]($seg/60))m $($seg%60)s" }
    return "$([int]($seg/3600))h $([int](($seg%3600)/60))m"
}

# ============================================================
# WORKER: descarga todas las TFs de un simbolo
# Devuelve un hashtable de resultados
# ============================================================
function Invoke-DescargarSimbolo {
    param(
        $Combos,          # Array de combos de este simbolo
        $Nombre,          # "EURUSD"
        $DesdeStr,        # "2019-04-11"
        $HastaStr,        # "2026-04-23"
        $MaxReintentos,
        $Server,
        $ChunkPorTF,
        $DelayPorTF,
        $ProgresoDir,
        $LogPrefix        # para identificar en salida paralela
    )

    $prog        = Load-ProgresoSimbolo $Nombre
    $fechaLimite = [datetime]::ParseExact($DesdeStr, "yyyy-MM-dd", $null)
    $velasTotal  = 0
    $errores     = 0

    foreach ($combo in $Combos) {
        $id        = $combo.Id
        $tf        = $combo.TF
        $chunkDias = $ChunkPorTF[$tf]
        $delaySeg  = $DelayPorTF[$tf]

        # Estado inicial
        if ($prog.ContainsKey($id)) {
            $est = $prog[$id]
        } else {
            $est = @{ cursor = $HastaStr; pendientes = @() }
        }

        $cursor     = [datetime]::ParseExact($est.cursor, "yyyy-MM-dd", $null)
        $pendientes = [System.Collections.ArrayList]@($est.pendientes | ForEach-Object {
            @{ desde = $_.desde; hasta = $_.hasta; intentos = if ($_.intentos) { [int]$_.intentos } else { 1 } }
        })

        $yaCompleto = ($cursor -le $fechaLimite) -and ($pendientes.Count -eq 0)
        if ($yaCompleto) {
            Write-Host "$LogPrefix [$id] ya completo - saltando" -ForegroundColor DarkGray
            continue
        }

        $diasRest    = [Math]::Max(0, ($cursor - $fechaLimite).TotalDays)
        $chunksNew   = [int][Math]::Ceiling($diasRest / $chunkDias)
        $chunksTotal = $chunksNew + $pendientes.Count

        Write-Host "$LogPrefix --- $id  chunk=$chunkDias dias  delay=$delaySeg s  ETA=$(Format-Eta $chunksTotal $delaySeg) ---" -ForegroundColor Cyan

        # -- 1. REINTENTAR PENDIENTES --
        $pendientesAun = [System.Collections.ArrayList]@()
        foreach ($p in $pendientes) {
            $intentos = if ($p.intentos) { [int]$p.intentos } else { 1 }

            if ($intentos -gt $MaxReintentos) {
                Write-Host "$LogPrefix   [DESCARTADO] $($p.desde)->$($p.hasta) (fallo $intentos veces)" -ForegroundColor DarkGray
                continue
            }

            $hora = Get-Date -Format "HH:mm:ss"
            Write-Host "$LogPrefix [$hora] RETRY $intentos/$MaxReintentos  $id  $($p.desde)->$($p.hasta) ..." -NoNewline -ForegroundColor Yellow
            $estado = Descargar-Chunk $combo.Symbol $tf $p.desde $p.hasta

            if ($estado -ne $null -and -not $estado.tuvo_error) {
                $velasTotal += $estado.velas_guardadas
                Write-Host " OK ($($estado.velas_guardadas) velas)" -ForegroundColor Green
            } else {
                $msg = if ($estado) { $estado.mensaje } else { "Timeout" }
                Write-Host " FALLO ($msg)" -ForegroundColor Red
                $pendientesAun.Add(@{ desde = $p.desde; hasta = $p.hasta; intentos = $intentos + 1 }) | Out-Null
                $errores++
            }
            Start-Sleep -Seconds $delaySeg
        }

        $est.pendientes = @($pendientesAun)
        $prog[$id] = $est
        Save-ProgresoSimbolo $Nombre $prog

        # -- 2. NUEVOS CHUNKS --
        while ($cursor -gt $fechaLimite) {
            $chunkDesde = $cursor.AddDays(-$chunkDias)
            if ($chunkDesde -lt $fechaLimite) { $chunkDesde = $fechaLimite }

            $desdeStr2 = $chunkDesde.ToString("yyyy-MM-dd")
            $hastaStr2 = $cursor.ToString("yyyy-MM-dd")

            $hora = Get-Date -Format "HH:mm:ss"
            Write-Host "$LogPrefix [$hora] $id  $desdeStr2 -> $hastaStr2 ..." -NoNewline

            $estado = Descargar-Chunk $combo.Symbol $tf $desdeStr2 $hastaStr2

            if ($estado -ne $null -and -not $estado.tuvo_error) {
                $velasTotal += $estado.velas_guardadas
                Write-Host " OK ($($estado.velas_guardadas) velas)" -ForegroundColor Green
            } else {
                $msg = if ($estado) { $estado.mensaje } else { "Timeout" }
                Write-Host " FALLO: $msg" -ForegroundColor Red
                $est.pendientes += @{ desde = $desdeStr2; hasta = $hastaStr2; intentos = 1 }
                $errores++
            }

            $cursor     = $chunkDesde
            $est.cursor = $cursor.ToString("yyyy-MM-dd")
            $prog[$id]  = $est
            Save-ProgresoSimbolo $Nombre $prog

            if ($cursor -gt $fechaLimite) {
                Start-Sleep -Seconds $delaySeg
            }
        }

        $pendFinal = $est.pendientes.Count
        if ($pendFinal -eq 0) {
            Write-Host "$LogPrefix [COMPLETO] $id" -ForegroundColor Green
        } else {
            Write-Host "$LogPrefix [COMPLETO con $pendFinal pendientes] $id" -ForegroundColor Yellow
        }
    }

    return @{ Velas = $velasTotal; Errores = $errores }
}

# ============================================================
# INICIO
# ============================================================
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  DESCARGA MASIVA HISTORICA v2" -ForegroundColor Cyan
Write-Host "  Paralelo: $MaxParalelo simbolos | Periodo: $Desde -> $Hasta" -ForegroundColor Cyan
Write-Host "  Chunks adaptativos: 1d=$($CHUNK_POR_TF['1d'])d  4h=$($CHUNK_POR_TF['4h'])d  1h=$($CHUNK_POR_TF['1h'])d  5m=$($CHUNK_POR_TF['5m'])d" -ForegroundColor Cyan
Write-Host "  Delays:             1d=$($DELAY_POR_TF['1d'])s   4h=$($DELAY_POR_TF['4h'])s   1h=$($DELAY_POR_TF['1h'])s  5m=$($DELAY_POR_TF['5m'])s" -ForegroundColor Cyan
Write-Host "  Limite API Deriv  : $DERIV_API_LIMITE_DIAS dias atras ($($DERIV_API_INICIO.ToString('yyyy-MM-dd')))" -ForegroundColor DarkYellow
Write-Host "  Para datos anteriores usar: .\importar_historico.ps1" -ForegroundColor DarkYellow
Write-Host "============================================================" -ForegroundColor Cyan

# Migrar progreso v1 si existe
Migrate-V1

# Asegurar carpeta de progreso
if (-not (Test-Path $ProgresoDir)) { New-Item -ItemType Directory -Path $ProgresoDir | Out-Null }

# Resetear
if ($Resetear) {
    if (Test-Path $ProgresoDir) {
        Remove-Item "$ProgresoDir\*.json" -Force -ErrorAction SilentlyContinue
        Write-Host "  Progreso reiniciado." -ForegroundColor Yellow
    }
}

# ============================================================
# Verificar servidor y obtener catalogo de activos
# (debe hacerse antes de filtrar combinaciones)
# ============================================================
$ping = Invoke-Api "/api/indicadores"
if ($ping -eq $null) {
    Write-Host ""
    Write-Host "  [ERROR] No se puede conectar a $Server" -ForegroundColor Red
    Write-Host "          Abre la aplicacion WpfDerivClientVB primero." -ForegroundColor Yellow
    exit 1
}
Write-Host "  Servidor API: OK" -ForegroundColor Green

$catalogo = Invoke-Api "/api/activos"
if ($catalogo -eq $null -or $catalogo.activos -eq $null) {
    Write-Host "  [ERROR] No se pudo obtener el catalogo desde $Server/api/activos" -ForegroundColor Red
    exit 1
}

$activosList = @($catalogo.activos)
$tfList      = @($catalogo.timeframes)

Write-Host ("  Catalogo ({0}): {1} activos x {2} TFs  [{3}]" -f `
    $catalogo.fuente,
    $activosList.Count,
    $tfList.Count,
    ($tfList -join ", ")) -ForegroundColor Green

# Construir combinaciones: producto cartesiano activos x TFs
foreach ($activo in $activosList) {
    foreach ($tf in $tfList) {
        $COMBINACIONES += @{
            Id     = "$($activo.nombre)-$tf"
            Symbol = $activo.symbol
            TF     = $tf
            Nombre = $activo.nombre
        }
    }
    # Sinteticos Deriv (R_*, 1HZ*): agregar 1m automaticamente
    # Los indices sinteticos operan 24/7 y solo tienen historia util en 1m via Deriv.
    if ($activo.symbol -like "R_*" -or $activo.symbol -like "1HZ*") {
        if ("1m" -notin $tfList) {
            $COMBINACIONES += @{
                Id     = "$($activo.nombre)-1m"
                Symbol = $activo.symbol
                TF     = "1m"
                Nombre = $activo.nombre
            }
        }
    }
}

# Filtrar combinaciones segun parametros -SoloActivo / -SoloTF
$combos = $COMBINACIONES
if ($SoloActivo -ne "") { $combos = $combos | Where-Object { $_.Symbol -eq $SoloActivo -or $_.Nombre -eq $SoloActivo } }
if ($SoloTF     -ne "") { $combos = $combos | Where-Object { $_.TF -eq $SoloTF } }

# Agrupar por simbolo (Nombre)
$gruposSimbolo = @{}
foreach ($c in $combos) {
    if (-not $gruposSimbolo.ContainsKey($c.Nombre)) { $gruposSimbolo[$c.Nombre] = @() }
    $gruposSimbolo[$c.Nombre] += $c
}
$simbolos = @($gruposSimbolo.Keys | Sort-Object)

Write-Host "  Simbolos: $($simbolos.Count)  |  Combinaciones: $($combos.Count)" -ForegroundColor Cyan

# Ver pendientes
if ($VerPendientes) {
    Write-Host ""
    $hayPend = $false
    foreach ($sym in $simbolos) {
        $prog = Load-ProgresoSimbolo $sym
        foreach ($c in $gruposSimbolo[$sym]) {
            if ($prog.ContainsKey($c.Id) -and $prog[$c.Id].pendientes.Count -gt 0) {
                if (-not $hayPend) {
                    Write-Host "  RANGOS PENDIENTES:" -ForegroundColor Yellow
                    $hayPend = $true
                }
                Write-Host "  [$($c.Id)]" -ForegroundColor Yellow
                foreach ($p in $prog[$c.Id].pendientes) {
                    Write-Host "    $($p.desde) -> $($p.hasta)  (intento $($p.intentos))" -ForegroundColor DarkYellow
                }
            }
        }
    }
    if (-not $hayPend) { Write-Host "  Sin rangos pendientes." -ForegroundColor Green }
    exit 0
}
Write-Host ""

# Mostrar ETA estimada
$totalDias = ([datetime]::ParseExact($Hasta, "yyyy-MM-dd", $null) - [datetime]::ParseExact($Desde, "yyyy-MM-dd", $null)).TotalDays
Write-Host "  ETA estimada (descarga limpia, chunks nuevos):" -ForegroundColor DarkCyan
foreach ($tf in @("1d","4h","1h","5m")) {
    $nCombos = ($combos | Where-Object { $_.TF -eq $tf }).Count
    if ($nCombos -eq 0) { continue }
    $chunks  = [int][Math]::Ceiling($totalDias / $CHUNK_POR_TF[$tf])
    $seqSeg  = $chunks * $DELAY_POR_TF[$tf] * $nCombos
    $parSeg  = $chunks * $DELAY_POR_TF[$tf] * [Math]::Ceiling($nCombos / $MaxParalelo)
    Write-Host ("    {0,-4}: {1,3} combos x {2,4} chunks x {3,2}s = {4,6} seq  |  {5} paralelo" -f `
        $tf, $nCombos, $chunks, $DELAY_POR_TF[$tf],
        (Format-Eta ($chunks * $nCombos) $DELAY_POR_TF[$tf]),
        (Format-Eta ([int]($chunks * [Math]::Ceiling($nCombos/$MaxParalelo))) $DELAY_POR_TF[$tf])
    ) -ForegroundColor DarkCyan
}
Write-Host ""

# ============================================================
# EJECUCION PARALELA POR SIMBOLO
# Orden: primero los TF grandes (1d,4h,1h), luego 5m
# Dentro de cada simbolo el orden es 1d->4h->1h->5m
# ============================================================

# Ordenar TFs dentro de cada simbolo: 1d primero
$tfOrden = @{ "1d"=0; "4h"=1; "1h"=2; "5m"=3 }
foreach ($sym in $simbolos) {
    $gruposSimbolo[$sym] = @($gruposSimbolo[$sym] | Sort-Object { $tfOrden[$_.TF] })
}

$totalVelas   = 0
$totalErrores = 0

if ($MaxParalelo -le 1) {
    # ---- MODO SECUENCIAL (MaxParalelo=1) ----
    foreach ($sym in $simbolos) {
        $res = Invoke-DescargarSimbolo `
            -Combos       $gruposSimbolo[$sym] `
            -Nombre       $sym `
            -DesdeStr     $Desde `
            -HastaStr     $Hasta `
            -MaxReintentos $MaxReintentos `
            -Server       $Server `
            -ChunkPorTF   $CHUNK_POR_TF `
            -DelayPorTF   $DELAY_POR_TF `
            -ProgresoDir  $ProgresoDir `
            -LogPrefix    "[$sym]"
        $totalVelas   += $res.Velas
        $totalErrores += $res.Errores
    }

} else {
    # ---- MODO PARALELO (MaxParalelo > 1) ----
    # Dividir simbolos en lotes
    $jobsDef  = @()
    foreach ($sym in $simbolos) {
        $jobsDef += @{ Nombre = $sym; Combos = $gruposSimbolo[$sym] }
    }

    $idx = 0
    while ($idx -lt $jobsDef.Count) {
        # Tomar un lote de MaxParalelo simbolos
        $lote = $jobsDef[$idx .. ([Math]::Min($idx + $MaxParalelo - 1, $jobsDef.Count - 1))]
        $idx += $MaxParalelo

        Write-Host "--- Iniciando lote: $($lote | ForEach-Object { $_.Nombre }) ---" -ForegroundColor Magenta

        # Capturar variables necesarias para el scriptblock
        $capturedServer      = $Server
        $capturedDesde       = $Desde
        $capturedHasta       = $Hasta
        $capturedMaxR        = $MaxReintentos
        $capturedChunk       = $CHUNK_POR_TF
        $capturedDelay       = $DELAY_POR_TF
        $capturedProgresoDir = $ProgresoDir
        $capturedScriptDir   = $ScriptDir

        $jobs = @()
        foreach ($jd in $lote) {
            $symLocal    = $jd.Nombre
            $combosLocal = $jd.Combos

            $job = Start-Job -ScriptBlock {
                param($sym, $combos, $desdeStr, $hastaStr, $maxR, $srv, $chunkTF, $delayTF, $progDir)

                # Re-definir funciones dentro del job (los jobs no heredan funciones del scope padre)
                function Load-ProgresoSimbolo($nombre) {
                    $file = "$progDir\$nombre.json"
                    if (-not (Test-Path $file)) { return @{} }
                    try {
                        $raw = Get-Content $file -Raw | ConvertFrom-Json
                        $result = @{}
                        foreach ($prop in $raw.PSObject.Properties) {
                            $v = $prop.Value
                            if ($v -is [string]) {
                                $result[$prop.Name] = @{ cursor = $v; pendientes = @() }
                            } else {
                                $result[$prop.Name] = @{
                                    cursor     = $v.cursor
                                    pendientes = @($v.pendientes | ForEach-Object {
                                        @{ desde = $_.desde; hasta = $_.hasta; intentos = if ($_.intentos) { [int]$_.intentos } else { 1 } }
                                    })
                                }
                            }
                        }
                        return $result
                    } catch { return @{} }
                }

                function Save-ProgresoSimbolo($nombre, $prog) {
                    if (-not (Test-Path $progDir)) { New-Item -ItemType Directory -Path $progDir | Out-Null }
                    $prog | ConvertTo-Json -Depth 5 | Set-Content "$progDir\$nombre.json" -Encoding UTF8
                }

                function Invoke-ApiLocal($path) {
                    try { return Invoke-RestMethod -Uri "$srv$path" -Method GET -TimeoutSec 90 }
                    catch { return $null }
                }

                function Wait-ChunkLocal([int]$MaxSeg = 300) {
                    $t = 0
                    do {
                        Start-Sleep -Seconds 5; $t += 5
                        $e = Invoke-ApiLocal "/api/historico/estado"
                        if ($e -ne $null -and -not $e.esta_activo) { return $e }
                    } while ($t -lt $MaxSeg)
                    return $null
                }

                function Descargar-ChunkLocal($symbol, $tf, $d, $h) {
                    $url  = "/api/historico/descargar?symbol=$symbol&timeframe=$([uri]::EscapeDataString($tf))&desde=$d&hasta=$h"
                    $resp = Invoke-ApiLocal $url
                    if ($resp -eq $null -or -not $resp.ok) { return $null }
                    return Wait-ChunkLocal
                }

                function Format-EtaLocal([int]$chunks, [int]$delay) {
                    $s = $chunks * $delay
                    if ($s -lt 60) { return "$s seg" }
                    if ($s -lt 3600) { return "$([int]($s/60))m $($s%60)s" }
                    return "$([int]($s/3600))h $([int](($s%3600)/60))m"
                }

                $prog        = Load-ProgresoSimbolo $sym
                $fechaLimite = [datetime]::ParseExact($desdeStr, "yyyy-MM-dd", $null)
                $velasTotal  = 0
                $errores     = 0
                $pfx         = "[$sym]"

                foreach ($combo in $combos) {
                    $id        = $combo.Id
                    $tf        = $combo.TF
                    $chunkDias = $chunkTF[$tf]
                    $delaySeg  = $delayTF[$tf]

                    if ($prog.ContainsKey($id)) { $est = $prog[$id] }
                    else { $est = @{ cursor = $hastaStr; pendientes = @() } }

                    $cursor     = [datetime]::ParseExact($est.cursor, "yyyy-MM-dd", $null)
                    $pendientes = [System.Collections.ArrayList]@($est.pendientes | ForEach-Object {
                        @{ desde = $_.desde; hasta = $_.hasta; intentos = if ($_.intentos) { [int]$_.intentos } else { 1 } }
                    })

                    if (($cursor -le $fechaLimite) -and ($pendientes.Count -eq 0)) {
                        Write-Output "$pfx [$id] ya completo - saltando"
                        continue
                    }

                    $diasRest    = [Math]::Max(0, ($cursor - $fechaLimite).TotalDays)
                    $chunksNew   = [int][Math]::Ceiling($diasRest / $chunkDias)
                    $chunksTotal = $chunksNew + $pendientes.Count
                    Write-Output "$pfx --- $id  chunk=${chunkDias}d  delay=${delaySeg}s  ETA=$(Format-EtaLocal $chunksTotal $delaySeg) ---"

                    # Reintentar pendientes
                    $pendientesAun = [System.Collections.ArrayList]@()
                    foreach ($p in $pendientes) {
                        $intentos = if ($p.intentos) { [int]$p.intentos } else { 1 }
                        if ($intentos -gt $maxR) {
                            Write-Output "$pfx   [DESCARTADO] $($p.desde)->$($p.hasta)"
                            continue
                        }
                        $hora = Get-Date -Format "HH:mm:ss"
                        Write-Output "$pfx [$hora] RETRY $intentos/$maxR  $id  $($p.desde)->$($p.hasta) ..."
                        $estado = Descargar-ChunkLocal $combo.Symbol $tf $p.desde $p.hasta
                        if ($estado -ne $null -and -not $estado.tuvo_error) {
                            $velasTotal += $estado.velas_guardadas
                            Write-Output "$pfx OK ($($estado.velas_guardadas) velas)"
                        } else {
                            $pendientesAun.Add(@{ desde = $p.desde; hasta = $p.hasta; intentos = $intentos + 1 }) | Out-Null
                            $errores++
                            Write-Output "$pfx FALLO retry"
                        }
                        Start-Sleep -Seconds $delaySeg
                    }
                    $est.pendientes = @($pendientesAun)
                    $prog[$id] = $est
                    Save-ProgresoSimbolo $sym $prog

                    # Nuevos chunks
                    while ($cursor -gt $fechaLimite) {
                        $chunkDesde = $cursor.AddDays(-$chunkDias)
                        if ($chunkDesde -lt $fechaLimite) { $chunkDesde = $fechaLimite }
                        $d2 = $chunkDesde.ToString("yyyy-MM-dd")
                        $h2 = $cursor.ToString("yyyy-MM-dd")
                        $hora = Get-Date -Format "HH:mm:ss"
                        Write-Output "$pfx [$hora] $id  $d2 -> $h2 ..."
                        $estado = Descargar-ChunkLocal $combo.Symbol $tf $d2 $h2
                        if ($estado -ne $null -and -not $estado.tuvo_error) {
                            $velasTotal += $estado.velas_guardadas
                            Write-Output "$pfx   OK ($($estado.velas_guardadas) velas)"
                        } else {
                            $est.pendientes += @{ desde = $d2; hasta = $h2; intentos = 1 }
                            $errores++
                            Write-Output "$pfx   FALLO"
                        }
                        $cursor     = $chunkDesde
                        $est.cursor = $cursor.ToString("yyyy-MM-dd")
                        $prog[$id]  = $est
                        Save-ProgresoSimbolo $sym $prog
                        if ($cursor -gt $fechaLimite) { Start-Sleep -Seconds $delaySeg }
                    }
                    Write-Output "$pfx [FIN] $id  pendientes=$($est.pendientes.Count)"
                }
                return @{ Velas = $velasTotal; Errores = $errores }

            } -ArgumentList $symLocal, $combosLocal, $capturedDesde, $capturedHasta, $capturedMaxR, `
                             $capturedServer, $capturedChunk, $capturedDelay, $capturedProgresoDir

            $jobs += @{ Job = $job; Sym = $symLocal }
            Write-Host "  Job iniciado: $symLocal (ID=$($job.Id))" -ForegroundColor Magenta
        }

        # Monitorear jobs del lote hasta que todos terminen
        Write-Host "  Monitoreando $($jobs.Count) jobs..." -ForegroundColor DarkCyan
        $pendingJobs = [System.Collections.ArrayList]@($jobs)
        while ($pendingJobs.Count -gt 0) {
            Start-Sleep -Seconds 5
            $completados = @($pendingJobs | Where-Object { $_.Job.State -in @("Completed","Failed","Stopped") })
            foreach ($jj in $completados) {
                $output = Receive-Job -Job $jj.Job
                foreach ($line in $output) {
                    if ($line -is [hashtable]) {
                        $totalVelas   += $line.Velas
                        $totalErrores += $line.Errores
                    } elseif ($line -match "OK") {
                        Write-Host $line -ForegroundColor Green
                    } elseif ($line -match "FALLO|ERROR") {
                        Write-Host $line -ForegroundColor Red
                    } elseif ($line -match "RETRY|pendiente") {
                        Write-Host $line -ForegroundColor Yellow
                    } elseif ($line -match "saltando|DESCARTADO") {
                        Write-Host $line -ForegroundColor DarkGray
                    } else {
                        Write-Host $line -ForegroundColor Cyan
                    }
                }
                Remove-Job -Job $jj.Job -Force
                $pendingJobs.Remove($jj)
                Write-Host "  Job completado: $($jj.Sym)" -ForegroundColor Green
            }

            # Mostrar estado de jobs aun activos
            if ($pendingJobs.Count -gt 0) {
                $activos = $pendingJobs | ForEach-Object { "$($_.Sym)($($_.Job.State))" }
                Write-Host "  Activos: $($activos -join ', ')" -ForegroundColor DarkGray
            }
        }
        Write-Host "  Lote completado." -ForegroundColor Magenta
        Write-Host ""
    }
}

# ============================================================
# RESUMEN FINAL
# ============================================================
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  SESION TERMINADA" -ForegroundColor Green
Write-Host "  Velas nuevas   : $totalVelas" -ForegroundColor Green
if ($totalErrores -gt 0) {
    Write-Host "  Chunks fallidos: $totalErrores (guardados en progreso/)" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "  Estado por simbolo:" -ForegroundColor Cyan
foreach ($sym in $simbolos) {
    $prog = Load-ProgresoSimbolo $sym
    $totalPend = 0
    $completados = 0
    foreach ($c in $gruposSimbolo[$sym]) {
        if ($prog.ContainsKey($c.Id)) {
            $totalPend  += $prog[$c.Id].pendientes.Count
            $fechaLimite = [datetime]::ParseExact($Desde, "yyyy-MM-dd", $null)
            $cursor      = [datetime]::ParseExact($prog[$c.Id].cursor, "yyyy-MM-dd", $null)
            if ($cursor -le $fechaLimite) { $completados++ }
        }
    }
    $estado = if ($totalPend -eq 0 -and $completados -eq $gruposSimbolo[$sym].Count) {
        "[OK]"
    } elseif ($totalPend -gt 0) {
        "[!$totalPend pend]"
    } else {
        "[incompleto]"
    }
    Write-Host ("    {0,-10} {1}" -f $sym, $estado) -ForegroundColor $(if ($totalPend -eq 0) { "Green" } else { "Yellow" })
}
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  .\descargar_masivo.ps1 -VerPendientes   para ver rangos fallidos" -ForegroundColor DarkGray
Write-Host "  .\reporte_bd.ps1                        para ver cobertura en BD" -ForegroundColor DarkGray
Write-Host "============================================================" -ForegroundColor Cyan
