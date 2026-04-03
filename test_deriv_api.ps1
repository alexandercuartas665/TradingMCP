# ============================================================
#  TEST DERIV NEW API — PowerShell
#  Rellena TOKEN, ACCOUNT_ID y APP_ID antes de ejecutar
# ============================================================

$TOKEN      = "AQUI_TU_TOKEN_pat_..."       # tu token pat_xxxxx
$ACCOUNT_ID = "AQUI_TU_ACCOUNT_ID"          # ej: DOT90355933
$APP_ID     = "AQUI_TU_APP_ID"              # ej: 32SQld9iNe8Q4FJx0BEJP

# Parámetros de la operación de prueba
$SYMBOL        = "R_100"
$AMOUNT        = 1          # monto pequeño para prueba
$CONTRACT_TYPE = "CALL"
$DURATION      = 5
$DURATION_UNIT = "t"

# ---- Paso 1: Obtener OTP ----
Write-Host "`n[1] Obteniendo OTP..." -ForegroundColor Cyan
$otpUrl = "https://api.derivws.com/trading/v1/options/accounts/$ACCOUNT_ID/otp"
try {
    $wc = New-Object System.Net.WebClient
    $wc.Headers.Add("Authorization", "Bearer $TOKEN")
    $wc.Headers.Add("deriv-app-id", $APP_ID)
    $wc.Headers.Add("Content-Type", "application/json")
    $otpResponse = $wc.UploadString($otpUrl, "POST", "")
    Write-Host "OTP Response: $otpResponse" -ForegroundColor Green
} catch {
    Write-Host "ERROR obteniendo OTP: $_" -ForegroundColor Red
    exit 1
}

# Extraer URL del WebSocket del JSON
$otpJson   = $otpResponse | ConvertFrom-Json
$wsUrl     = $otpJson.data.url
if (-not $wsUrl) {
    Write-Host "No se encontro data.url en la respuesta. JSON completo:" -ForegroundColor Red
    Write-Host $otpResponse
    exit 1
}
Write-Host "WS URL: $wsUrl" -ForegroundColor Green

# ---- Paso 2: Conectar WebSocket ----
Write-Host "`n[2] Conectando WebSocket..." -ForegroundColor Cyan
$ws  = New-Object System.Net.WebSockets.ClientWebSocket
$cts = New-Object System.Threading.CancellationTokenSource
$ws.ConnectAsync([System.Uri]$wsUrl, $cts.Token).Wait()
Write-Host "Estado WebSocket: $($ws.State)" -ForegroundColor Green

function WsSend($ws, $cts, $msg) {
    $bytes   = [System.Text.Encoding]::UTF8.GetBytes($msg)
    $segment = New-Object System.ArraySegment[byte](,$bytes)
    $ws.SendAsync($segment, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token).Wait()
    Write-Host "  >> Enviado: $msg" -ForegroundColor DarkCyan
}

function WsReceive($ws, $cts) {
    $buffer  = New-Object byte[](65536)
    $segment = New-Object System.ArraySegment[byte](,$buffer)
    $sb      = New-Object System.Text.StringBuilder
    do {
        $result = $ws.ReceiveAsync($segment, $cts.Token).Result
        $sb.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count)) | Out-Null
    } while (-not $result.EndOfMessage)
    return $sb.ToString()
}

# ---- Paso 3: Suscribir balance (verificar que la conexión funciona) ----
Write-Host "`n[3] Suscribiendo balance..." -ForegroundColor Cyan
WsSend $ws $cts '{"balance":1,"subscribe":1}'
$resp = WsReceive $ws $cts
Write-Host "  << $resp" -ForegroundColor Yellow

# ---- Paso 4: Enviar PROPOSAL ----
Write-Host "`n[4] Enviando proposal $CONTRACT_TYPE $SYMBOL..." -ForegroundColor Cyan
$proposalMsg = @{
    proposal      = 1
    amount        = $AMOUNT
    basis         = "stake"
    contract_type = $CONTRACT_TYPE
    currency      = "USD"
    duration      = $DURATION
    duration_unit = $DURATION_UNIT
    symbol        = $SYMBOL
} | ConvertTo-Json -Compress
WsSend $ws $cts $proposalMsg

$resp = WsReceive $ws $cts
Write-Host "  << $resp" -ForegroundColor Yellow

# Intentar extraer proposal_id
try {
    $respJson    = $resp | ConvertFrom-Json
    $proposalId  = $respJson.proposal.id
    $askPrice    = $respJson.proposal.ask_price
    if ($proposalId) {
        Write-Host "`n  proposal_id = $proposalId  ask_price = $askPrice" -ForegroundColor Green

        # ---- Paso 5: Ejecutar BUY ----
        Write-Host "`n[5] Ejecutando BUY con proposal_id..." -ForegroundColor Cyan
        $buyMsg = (@{ buy = $proposalId; price = $askPrice } | ConvertTo-Json -Compress)
        WsSend $ws $cts $buyMsg
        $resp2 = WsReceive $ws $cts
        Write-Host "  << $resp2" -ForegroundColor Yellow
    } else {
        Write-Host "`n  No se obtuvo proposal_id. Revisa el mensaje de arriba." -ForegroundColor Red
        if ($respJson.error) {
            Write-Host "  ERROR: $($respJson.error.message) [$($respJson.error.code)]" -ForegroundColor Red
            Write-Host "  Detalles: $($respJson.error.details)" -ForegroundColor Red
        }
    }
} catch {
    Write-Host "Error parseando respuesta: $_" -ForegroundColor Red
}

$ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "done", $cts.Token).Wait()
Write-Host "`n[OK] Conexion cerrada." -ForegroundColor Cyan
