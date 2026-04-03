$TOKEN      = "pat_b825f02dc6ac349dc381afbde872ede597c0030c5d262a5a42d8effa52aba552"
$ACCOUNT_ID = "DOT90355933"
$APP_ID     = "32SQld9iNe8Q4FJx0BEJP"

Write-Host "[1] Obteniendo OTP..." -ForegroundColor Cyan
$otpUrl = "https://api.derivws.com/trading/v1/options/accounts/$ACCOUNT_ID/otp"
try {
    $wc = New-Object System.Net.WebClient
    $wc.Headers.Add("Authorization", "Bearer $TOKEN")
    $wc.Headers.Add("deriv-app-id", $APP_ID)
    $otpResponse = $wc.UploadString($otpUrl, "POST", "")
    Write-Host "OTP Response: $otpResponse" -ForegroundColor Green
} catch [System.Net.WebException] {
    $errResp = $_.Exception.Response
    if ($errResp) {
        $stream = $errResp.GetResponseStream()
        $reader = New-Object System.IO.StreamReader($stream)
        $body   = $reader.ReadToEnd()
        Write-Host "ERROR OTP HTTP $([int]$errResp.StatusCode): $body" -ForegroundColor Red
    } else {
        Write-Host "ERROR OTP: $_" -ForegroundColor Red
    }
    exit 1
} catch {
    Write-Host "ERROR OTP: $_" -ForegroundColor Red
    exit 1
}

$otpJson = $otpResponse | ConvertFrom-Json
$wsUrl   = $otpJson.data.url
if (-not $wsUrl) { Write-Host "Sin WS URL. JSON: $otpResponse" -ForegroundColor Red; exit 1 }
Write-Host "WS URL: $wsUrl" -ForegroundColor Green

Write-Host "[2] Conectando WebSocket..." -ForegroundColor Cyan
$ws  = New-Object System.Net.WebSockets.ClientWebSocket
$cts = New-Object System.Threading.CancellationTokenSource
$ws.ConnectAsync([System.Uri]$wsUrl, $cts.Token).Wait()
Write-Host "Estado: $($ws.State)" -ForegroundColor Green

function WsSend($msg) {
    $bytes   = [System.Text.Encoding]::UTF8.GetBytes($msg)
    $segment = New-Object System.ArraySegment[byte](,$bytes)
    $ws.SendAsync($segment, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token).Wait()
    Write-Host ">> $msg" -ForegroundColor DarkCyan
}
function WsReceive() {
    $buffer  = New-Object byte[](65536)
    $segment = New-Object System.ArraySegment[byte](,$buffer)
    $sb      = New-Object System.Text.StringBuilder
    do {
        $result = $ws.ReceiveAsync($segment, $cts.Token).Result
        $sb.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count)) | Out-Null
    } while (-not $result.EndOfMessage)
    return $sb.ToString()
}

Write-Host "[3] Balance..." -ForegroundColor Cyan
WsSend '{"balance":1,"subscribe":1}'
$r = WsReceive
Write-Host "<< $r" -ForegroundColor Yellow

function TestProposal($label, $msg) {
    Write-Host "`n[$label]" -ForegroundColor Cyan
    WsSend $msg
    $r = WsReceive
    Write-Host "<< $r" -ForegroundColor Yellow
    $rj = $r | ConvertFrom-Json
    if ($rj.proposal) {
        Write-Host "  => OK! proposal_id=$($rj.proposal.id) ask=$($rj.proposal.ask_price)" -ForegroundColor Green
        return $rj
    } elseif ($rj.error) {
        Write-Host "  => ERROR: $($rj.error.message) [$($rj.error.code)]" -ForegroundColor Red
    }
    return $null
}

# Campo correcto: underlying_symbol
$r1 = TestProposal "4 underlying_symbol" '{"proposal":1,"amount":1,"basis":"stake","contract_type":"CALL","currency":"USD","duration":5,"duration_unit":"t","underlying_symbol":"R_100"}'

if ($r1) {
    $proposalId = $r1.proposal.id
    $askPrice   = $r1.proposal.ask_price
    Write-Host "`n[5] BUY proposal_id=$proposalId..." -ForegroundColor Cyan
    WsSend "{`"buy`":`"$proposalId`",`"price`":$askPrice}"
    $r2 = WsReceive
    Write-Host "<< $r2" -ForegroundColor Yellow
} else {
    Write-Host "`nNingún intento funcionó." -ForegroundColor Red
}

$ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "done", $cts.Token).Wait()
Write-Host "[DONE]" -ForegroundColor Cyan
