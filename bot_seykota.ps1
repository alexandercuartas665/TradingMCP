# ============================================================
# bot_seykota.ps1
# Estrategia Ed Seykota — Donchian Channels (20) + ATR (14)
#
# Logica:
#   CALL : precio toca banda superior (nuevo maximo de 20 periodos)
#          con canal en pendiente ascendente
#   PUT  : precio toca banda inferior (nuevo minimo de 20 periodos)
#          con canal en pendiente descendente
#   NEUTRAL : precio dentro del canal — no operar
#
# Piramidacion:
#   Cuando una operacion gana en la misma direccion de la tendencia,
#   se anade una capa de monto adicional hasta MAX_CAPAS.
#   Una perdida o cambio de senal resetea las capas.
# ============================================================

# ── CONFIGURACION ──────────────────────────────────────────
$SERVER      = "http://localhost:8765"
$SYMBOL      = "R_100"
$AMOUNT      = 1.0      # Monto base por operacion (USD)
$DURATION    = 5        # Duracion del contrato (ticks)
$PERIODO     = 20       # Periodo del canal Donchian
$GRANULARITY = 60       # Granularidad de velas en segundos (60 = 1 min)
$MIN_CONF    = 60       # Minima confianza para entrar (55 o 80 disponibles)
$WAIT_SEC    = 90       # Segundos entre ciclos de analisis
$STOP_SESION = -15.0    # Parar si el PnL de sesion cae a este valor (USD)
$MAX_TRADES  = 50       # Max operaciones por sesion
$LOG_FILE    = "$PSScriptRoot\bot_seykota_log.txt"

# ── PIRAMIDACION ───────────────────────────────────────────
$PIRAMIDACION   = $true   # Activar piramidacion
$MAX_CAPAS      = 3       # Maximo de capas de piramidacion
$MONTO_EXTRA    = 0.5     # USD adicional por cada capa

# ── ESTADO DE SESION ───────────────────────────────────────
$ganadas         = 0
$perdidas        = 0
$neutras         = 0
$totalPnL        = 0.0
$totalTrades     = 0
$tendenciaActual = "NEUTRAL"   # Tendencia que estamos siguiendo
$capasActuales   = 0           # Capas de piramidacion activas
$senalAnterior   = "NEUTRAL"

# ── HELPERS ────────────────────────────────────────────────
function Log($msg) {
    $line = "[$(Get-Date -Format 'HH:mm:ss')] $msg"
    Write-Host $line
    Add-Content -Path $LOG_FILE -Value $line -Encoding UTF8
}

function Separador {
    $line = "-" * 65
    Write-Host $line
    Add-Content -Path $LOG_FILE -Value $line -Encoding UTF8
}

function Get-Seykota {
    try {
        $url = "$SERVER/api/seykota?symbol=$SYMBOL&periodo=$PERIODO&granularity=$GRANULARITY"
        return Invoke-RestMethod -Uri $url -Method GET -TimeoutSec 15
    } catch {
        return $null
    }
}

function Execute-Trade($contractType, $amount) {
    try {
        $body = @{
            contract_type = $contractType
            symbol        = $SYMBOL
            amount        = $amount
            basis         = "stake"
            currency      = "USD"
            duration      = $DURATION
            duration_unit = "t"
        } | ConvertTo-Json -Compress

        return Invoke-RestMethod -Uri "$SERVER/api/trade" `
                                 -Method POST `
                                 -ContentType "application/json" `
                                 -Body $body `
                                 -TimeoutSec 20
    } catch {
        return $null
    }
}

function Get-ContractResult($contractId) {
    $tries = 0
    while ($tries -lt 12) {
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

# ── VERIFICAR SERVIDOR ─────────────────────────────────────
try {
    $status = Invoke-RestMethod -Uri "$SERVER/api/status" -Method GET -TimeoutSec 10
    if (-not $status.connected) {
        Write-Host "ERROR: Trading no conectado. Conecta una cuenta en la app primero."
        exit 1
    }
    Log "=== BOT SEYKOTA INICIADO ==="
    Log "Cuenta      : $($status.account)"
    Log "Balance     : $($status.balance)"
    Log "Simbolo     : $SYMBOL | Donchian($PERIODO) | ATR(14) | Velas de ${GRANULARITY}s"
    Log "Config      : conf>=$MIN_CONF | monto=$AMOUNT USD | $DURATION ticks | cada ${WAIT_SEC}s"
    Log "Piramidacion: $PIRAMIDACION (max $MAX_CAPAS capas x +$MONTO_EXTRA USD)"
    Log "Stop sesion : $STOP_SESION USD"
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

    # ── Stop de sesion ─────────────────────────────────────
    if ($totalPnL -le $STOP_SESION) {
        Separador
        Log "STOP DE SESION alcanzado: PnL = $([math]::Round($totalPnL,2)) USD"
        Log "Sesion finalizada: G=$ganadas P=$perdidas PnL=$([math]::Round($totalPnL,2)) USD"
        break
    }
    if ($totalTrades -ge $MAX_TRADES) {
        Separador
        Log "Limite de operaciones alcanzado ($MAX_TRADES)."
        Log "Sesion finalizada: G=$ganadas P=$perdidas PnL=$([math]::Round($totalPnL,2)) USD"
        break
    }

    # ── Analisis Donchian ──────────────────────────────────
    Log "Analizando $SYMBOL (Donchian $PERIODO | ATR14)..."
    $sig = Get-Seykota

    if ($sig -eq $null) {
        Log "WARN: Sin respuesta del servidor. Reintentando en ${WAIT_SEC}s..."
        Start-Sleep -Seconds $WAIT_SEC
        continue
    }

    $senal      = $sig.senal
    $confianza  = $sig.confianza
    $dcUpper    = [math]::Round($sig.dc_upper,   2)
    $dcLower    = [math]::Round($sig.dc_lower,   2)
    $dcMid      = [math]::Round($sig.dc_mid,     2)
    $atr        = [math]::Round($sig.atr14,      4)
    $precio     = [math]::Round($sig.precio_actual, 4)
    $slopeUp    = $sig.slope_up
    $slopeDown  = $sig.slope_down
    $motivo     = $sig.motivo

    $slopeTexto = if ($senal -eq "CALL") { if ($slopeUp) { "subiendo" } else { "lateral" } } `
                  elseif ($senal -eq "PUT") { if ($slopeDown) { "bajando" } else { "lateral" } } `
                  else { "plano" }

    Log ("Senal: {0,-7} | Conf: {1,3}% | Precio: {2} | DC: {3}-{4} | ATR: {5} | Canal: {6}" -f `
         $senal, $confianza, $precio, $dcLower, $dcUpper, $atr, $slopeTexto)

    # ── Filtros ────────────────────────────────────────────
    if ($senal -eq "NEUTRAL") {
        Log "Mercado dentro del canal — sin senal. Esperando ${WAIT_SEC}s..."
        $neutras++

        # Resetear piramidacion si perdimos la tendencia
        if ($capasActuales -gt 0) {
            Log "Tendencia perdida — reseteando piramidacion (capas: $capasActuales -> 0)"
            $capasActuales   = 0
            $tendenciaActual = "NEUTRAL"
        }

        Start-Sleep -Seconds $WAIT_SEC
        continue
    }

    if ($confianza -lt $MIN_CONF) {
        Log "Confianza $confianza% < minimo $MIN_CONF%. Esperando ${WAIT_SEC}s..."
        $neutras++
        Start-Sleep -Seconds $WAIT_SEC
        continue
    }

    # ── Cambio de tendencia: resetear piramidacion ─────────
    if ($tendenciaActual -ne "NEUTRAL" -and $tendenciaActual -ne $senal) {
        Log "Cambio de tendencia $tendenciaActual -> $senal. Reseteando piramidacion."
        $capasActuales   = 0
        $tendenciaActual = $senal
    } elseif ($tendenciaActual -eq "NEUTRAL") {
        $tendenciaActual = $senal
    }

    # ── Calcular monto con piramidacion ───────────────────
    $montoOp = $AMOUNT
    if ($PIRAMIDACION -and $capasActuales -gt 0) {
        $montoOp = $AMOUNT + ($capasActuales * $MONTO_EXTRA)
        Log "Piramidacion capa $capasActuales — monto: $$montoOp"
    }

    # ── Ejecutar operacion ────────────────────────────────
    Log (">>> EJECUTANDO {0} {1} | USD {2} | {3} ticks | conf:{4}% | {5}" -f `
         $senal, $SYMBOL, $montoOp, $DURATION, $confianza, $motivo)

    $trade = Execute-Trade $senal $montoOp

    if ($trade -eq $null -or -not $trade.ok) {
        $err = if ($trade -ne $null -and $trade.error) { $trade.error } else { "Sin respuesta" }
        Log "ERROR al ejecutar: $err"
        Start-Sleep -Seconds $WAIT_SEC
        continue
    }

    $contractId = $trade.contract_id
    $totalTrades++
    Log "Contrato abierto: $contractId"

    # ── Esperar resultado ─────────────────────────────────
    Log "Esperando cierre del contrato..."
    $result = Get-ContractResult $contractId

    if ($result -eq $null) {
        Log "WARN: No se obtuvo resultado de $contractId en tiempo. Continuando..."
        Start-Sleep -Seconds $WAIT_SEC
        continue
    }

    $estado = $result.estado
    $pnl    = [math]::Round($result.profit_loss, 2)
    $totalPnL += $pnl

    if ($estado -eq "Ganado") {
        $ganadas++
        # Piramidacion: incrementar capa si no llegamos al maximo
        if ($PIRAMIDACION -and $capasActuales -lt $MAX_CAPAS) {
            $capasActuales++
            Log ("<<< GANADO  +`$$pnl | Sesion: G=$ganadas P=$perdidas | " +
                 "PnL: $([math]::Round($totalPnL,2)) USD | Piramida -> capa $capasActuales")
        } else {
            Log ("<<< GANADO  +`$$pnl | Sesion: G=$ganadas P=$perdidas | " +
                 "PnL: $([math]::Round($totalPnL,2)) USD | Capas: $capasActuales/$MAX_CAPAS (tope)")
        }
    } else {
        $perdidas++
        # Perdida: resetear piramidacion
        if ($capasActuales -gt 0) {
            Log ("<<< PERDIDO `$$pnl | Sesion: G=$ganadas P=$perdidas | " +
                 "PnL: $([math]::Round($totalPnL,2)) USD | Piramida reseteada (capa $capasActuales -> 0)")
            $capasActuales = 0
        } else {
            Log ("<<< PERDIDO `$$pnl | Sesion: G=$ganadas P=$perdidas | " +
                 "PnL: $([math]::Round($totalPnL,2)) USD")
        }
    }

    # ── Resumen cada 10 operaciones ───────────────────────
    $total = $ganadas + $perdidas
    if ($total -gt 0 -and $total % 10 -eq 0) {
        $wr = [math]::Round($ganadas / $total * 100, 1)
        Separador
        Log ("=== RESUMEN ({0} ops) | G={1} P={2} | WinRate={3}% | PnL={4} USD ===" -f `
             $total, $ganadas, $perdidas, $wr, [math]::Round($totalPnL, 2))
        Separador
    }

    Separador
    Log "Esperando ${WAIT_SEC}s para el proximo ciclo..."
    Start-Sleep -Seconds $WAIT_SEC
}

# ── RESUMEN FINAL ──────────────────────────────────────────
Separador
$total = $ganadas + $perdidas
$wr    = if ($total -gt 0) { [math]::Round($ganadas / $total * 100, 1) } else { 0 }
Log "=== SESION FINALIZADA ==="
Log "Operaciones : $total  (G=$ganadas / P=$perdidas)"
Log "Win Rate    : $wr%"
Log ("PnL Total   : {0} USD" -f [math]::Round($totalPnL, 2))
Log "Sin senal   : $neutras ciclos omitidos"
Separador
