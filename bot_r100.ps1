# ============================================================
# bot_r100.ps1
# Bot automatico de trading para R_100 via servidor local.
# Solo opera cuando la senal tiene confianza >= MIN_CONFIANZA.
# Espera a que el contrato cierre antes de la siguiente entrada.
# ============================================================

$SERVER   = "http://localhost:8765"
$SYMBOL   = "R_100"
$AMOUNT   = 1          # Monto por operacion (USD)
$DURATION = 5          # Duracion en ticks
$PERIODO  = 25         # Velas para el analisis
$MIN_CONF = 60         # Minimo de confianza para operar (0-100)
$WAIT_SEC = 90         # Segundos entre ciclos de analisis
$LOG_FILE = "$PSScriptRoot\bot_r100_log.txt"

# ---- Contadores de sesion ----
$ganadas  = 0
$perdidas = 0
$neutras  = 0
$totalPnL = 0.0

# ---- Helpers ----
function Log($msg) {
    $line = "[$(Get-Date -Format 'HH:mm:ss')] $msg"
    Write-Host $line
    Add-Content -Path $LOG_FILE -Value $line -Encoding UTF8
}

function Separador {
    $line = "-" * 60
    Write-Host $line
    Add-Content -Path $LOG_FILE -Value $line -Encoding UTF8
}

function Get-Signal {
    try {
        $url = "$SERVER/api/analizar?symbol=$SYMBOL&periodo=$PERIODO"
        $r   = Invoke-RestMethod -Uri $url -Method GET -TimeoutSec 15
        return $r
    } catch {
        return $null
    }
}

function Execute-Trade($senal) {
    try {
        $body = @{
            symbol        = $SYMBOL
            amount        = $AMOUNT
            duration      = $DURATION
            duration_unit = "t"
            periodo       = $PERIODO
            min_confianza = $MIN_CONF
        } | ConvertTo-Json -Compress

        $r = Invoke-RestMethod -Uri "$SERVER/api/analizar-y-operar" `
                               -Method POST `
                               -ContentType "application/json" `
                               -Body $body `
                               -TimeoutSec 30
        return $r
    } catch {
        return $null
    }
}

function Get-ContractResult($contractId) {
    # Espera hasta 30s a que el contrato cierre
    $tries = 0
    while ($tries -lt 10) {
        Start-Sleep -Seconds 3
        try {
            $ops = Invoke-RestMethod -Uri "$SERVER/api/operations" -Method GET -TimeoutSec 10
            $op  = $ops | Where-Object { $_.contract_id -eq $contractId } | Select-Object -First 1
            if ($op -and ($op.estado -eq "Ganado" -or $op.estado -eq "Perdido")) {
                return $op
            }
        } catch {}
        $tries++
    }
    return $null
}

# ---- Verificar servidor ----
try {
    $status = Invoke-RestMethod -Uri "$SERVER/api/status" -Method GET -TimeoutSec 10
    if (-not $status.connected) {
        Write-Host "ERROR: Trading no conectado. Conecta una cuenta en la app primero."
        exit 1
    }
    Log "=== BOT R_100 INICIADO ==="
    Log "Cuenta  : $($status.account)"
    Log "Balance : $($status.balance)"
    Log "Config  : conf>=$MIN_CONF | monto=$AMOUNT USD | $DURATION ticks | cada ${WAIT_SEC}s"
    Separador
} catch {
    Write-Host "ERROR: No se puede conectar al servidor en $SERVER"
    Write-Host "Asegurate de que la app este corriendo y el servidor iniciado."
    exit 1
}

# ============================================================
#  BUCLE PRINCIPAL
# ============================================================
while ($true) {

    # 1. Analizar mercado
    Log "Analizando $SYMBOL..."
    $sig = Get-Signal

    if ($sig -eq $null) {
        Log "WARN: No se obtuvo respuesta del servidor. Reintentando en ${WAIT_SEC}s..."
        Start-Sleep -Seconds $WAIT_SEC
        continue
    }

    $senal     = $sig.senal
    $confianza = $sig.confianza
    $score     = $sig.score
    $ema5      = [math]::Round($sig.ema5,  2)
    $ema10     = [math]::Round($sig.ema10, 2)
    $rsi       = [math]::Round($sig.rsi14, 2)

    Log "Senal: $senal | Score: $score | Confianza: $confianza% | EMA5=$ema5 EMA10=$ema10 RSI=$rsi"

    # 2. Filtro de calidad
    if ($senal -eq "NEUTRAL") {
        Log "NEUTRAL - sin senal clara. Esperando ${WAIT_SEC}s..."
        $neutras++
        Start-Sleep -Seconds $WAIT_SEC
        continue
    }

    if ($confianza -lt $MIN_CONF) {
        Log "Confianza $confianza% < minimo $MIN_CONF%. Esperando ${WAIT_SEC}s..."
        $neutras++
        Start-Sleep -Seconds $WAIT_SEC
        continue
    }

    # 3. Ejecutar operacion
    Log ">>> EJECUTANDO $senal $SYMBOL | USD $AMOUNT | $DURATION ticks | confianza=$confianza%"
    $trade = Execute-Trade $senal

    if ($trade -eq $null) {
        Log "ERROR al llamar al servidor. Esperando ${WAIT_SEC}s..."
        Start-Sleep -Seconds $WAIT_SEC
        continue
    }

    if (-not $trade.ok) {
        $motivo = if ($trade.motivo) { $trade.motivo } else { $trade.error }
        Log "NO operado: $motivo"
        $neutras++
        Start-Sleep -Seconds $WAIT_SEC
        continue
    }

    $contractId = $trade.operacion.contract_id
    $hora       = $trade.operacion.hora
    Log "Contrato abierto: $contractId | hora: $hora"

    # 4. Esperar resultado
    Log "Esperando cierre del contrato..."
    $result = Get-ContractResult $contractId

    if ($result -eq $null) {
        Log "WARN: No se obtuvo resultado del contrato $contractId en 30s."
        Start-Sleep -Seconds $WAIT_SEC
        continue
    }

    $estado = $result.estado
    $pnl    = [math]::Round($result.profit_loss, 2)
    $totalPnL += $pnl

    if ($estado -eq "Ganado") {
        $ganadas++
        Log "<<< GANADO  +$$pnl | Total sesion: G=$ganadas P=$perdidas | PnL acum: $([math]::Round($totalPnL,2))"
    } else {
        $perdidas++
        Log "<<< PERDIDO $$pnl | Total sesion: G=$ganadas P=$perdidas | PnL acum: $([math]::Round($totalPnL,2))"
    }

    # 5. Resumen cada 10 operaciones
    $total = $ganadas + $perdidas
    if ($total -gt 0 -and $total % 10 -eq 0) {
        $wr = [math]::Round($ganadas / $total * 100, 1)
        Separador
        Log "=== RESUMEN ($total ops): Ganadas=$ganadas Perdidas=$perdidas WinRate=$wr% PnL=$([math]::Round($totalPnL,2)) USD ==="
        Separador
    }

    Separador
    # 6. Siguiente ciclo
    Log "Esperando ${WAIT_SEC}s para el proximo analisis..."
    Start-Sleep -Seconds $WAIT_SEC
}
