$TOKEN      = "pat_b825f02dc6ac349dc381afbde872ede597c0030c5d262a5a42d8effa52aba552"
$ACCOUNT_ID = "DOT90355933"
$APP_ID     = "32SQld9iNe8Q4FJx0BEJP"

# OTP
$wc = New-Object System.Net.WebClient
$wc.Headers.Add("Authorization", "Bearer $TOKEN")
$wc.Headers.Add("deriv-app-id", $APP_ID)
$otpResponse = $wc.UploadString("https://api.derivws.com/trading/v1/options/accounts/$ACCOUNT_ID/otp", "POST", "")
$wsUrl = ($otpResponse | ConvertFrom-Json).data.url
Write-Host "Conectando a: $wsUrl" -ForegroundColor Cyan

$ws  = New-Object System.Net.WebSockets.ClientWebSocket
$cts = New-Object System.Threading.CancellationTokenSource
$ws.ConnectAsync([System.Uri]$wsUrl, $cts.Token).Wait()

function WsSend($msg) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($msg)
    $ws.SendAsync((New-Object System.ArraySegment[byte](,$bytes)), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token).Wait()
}
function WsReceive() {
    $buf = New-Object byte[](65536)
    $sb  = New-Object System.Text.StringBuilder
    do {
        $r = $ws.ReceiveAsync((New-Object System.ArraySegment[byte](,$buf)), $cts.Token).Result
        $sb.Append([System.Text.Encoding]::UTF8.GetString($buf, 0, $r.Count)) | Out-Null
    } while (-not $r.EndOfMessage)
    return $sb.ToString()
}
function TestProposal($label, $json) {
    Write-Host "`n--- $label ---" -ForegroundColor Yellow
    WsSend $json
    $r = WsReceive | ConvertFrom-Json
    if ($r.proposal) {
        Write-Host "  OK  ask=$($r.proposal.ask_price) payout=$($r.proposal.payout) id=$($r.proposal.id)" -ForegroundColor Green
        Write-Host "  longcode: $($r.proposal.longcode)" -ForegroundColor Gray
    } elseif ($r.error) {
        Write-Host "  ERR [$($r.error.code)] $($r.error.message)" -ForegroundColor Red
        if ($r.error.details) { Write-Host "  details: $($r.error.details | ConvertTo-Json -Compress)" -ForegroundColor DarkRed }
    }
}

# ---- Probar distintos tipos de contrato ----

# 1. Rise/Fall básico
TestProposal "CALL stake 5t" '{"proposal":1,"amount":1,"basis":"stake","contract_type":"CALL","currency":"USD","duration":5,"duration_unit":"t","underlying_symbol":"R_100"}'
TestProposal "PUT stake 5t"  '{"proposal":1,"amount":1,"basis":"stake","contract_type":"PUT","currency":"USD","duration":5,"duration_unit":"t","underlying_symbol":"R_100"}'

# 2. Basis = payout
TestProposal "CALL payout 5t" '{"proposal":1,"amount":2,"basis":"payout","contract_type":"CALL","currency":"USD","duration":5,"duration_unit":"t","underlying_symbol":"R_100"}'

# 3. Duraciones distintas
TestProposal "CALL 1m" '{"proposal":1,"amount":1,"basis":"stake","contract_type":"CALL","currency":"USD","duration":1,"duration_unit":"m","underlying_symbol":"R_100"}'
TestProposal "CALL 1h" '{"proposal":1,"amount":1,"basis":"stake","contract_type":"CALL","currency":"USD","duration":1,"duration_unit":"h","underlying_symbol":"R_100"}'
TestProposal "CALL 1d" '{"proposal":1,"amount":1,"basis":"stake","contract_type":"CALL","currency":"USD","duration":1,"duration_unit":"d","underlying_symbol":"R_100"}'

# 4. Touch contracts
TestProposal "ONETOUCH +1.5" '{"proposal":1,"amount":1,"basis":"stake","contract_type":"ONETOUCH","currency":"USD","duration":5,"duration_unit":"t","underlying_symbol":"R_100","barrier":"+1.5"}'
TestProposal "NOTOUCH +1.5"  '{"proposal":1,"amount":1,"basis":"stake","contract_type":"NOTOUCH","currency":"USD","duration":5,"duration_unit":"t","underlying_symbol":"R_100","barrier":"+1.5"}'

# 5. In/Out (Range)
TestProposal "EXPIRYRANGE"  '{"proposal":1,"amount":1,"basis":"stake","contract_type":"EXPIRYRANGE","currency":"USD","duration":5,"duration_unit":"t","underlying_symbol":"R_100","barrier":"+1.5","barrier2":"-1.5"}'
TestProposal "EXPIRYMISS"   '{"proposal":1,"amount":1,"basis":"stake","contract_type":"EXPIRYMISS","currency":"USD","duration":5,"duration_unit":"t","underlying_symbol":"R_100","barrier":"+1.5","barrier2":"-1.5"}'

# 6. Digit contracts
TestProposal "DIGITEVEN"    '{"proposal":1,"amount":1,"basis":"stake","contract_type":"DIGITEVEN","currency":"USD","duration":5,"duration_unit":"t","underlying_symbol":"R_100"}'
TestProposal "DIGITODD"     '{"proposal":1,"amount":1,"basis":"stake","contract_type":"DIGITODD","currency":"USD","duration":5,"duration_unit":"t","underlying_symbol":"R_100"}'
TestProposal "DIGITOVER 5"  '{"proposal":1,"amount":1,"basis":"stake","contract_type":"DIGITOVER","currency":"USD","duration":5,"duration_unit":"t","underlying_symbol":"R_100","barrier":5}'
TestProposal "DIGITUNDER 5" '{"proposal":1,"amount":1,"basis":"stake","contract_type":"DIGITUNDER","currency":"USD","duration":5,"duration_unit":"t","underlying_symbol":"R_100","barrier":5}'
TestProposal "DIGITMATCH 7" '{"proposal":1,"amount":1,"basis":"stake","contract_type":"DIGITMATCH","currency":"USD","duration":5,"duration_unit":"t","underlying_symbol":"R_100","barrier":7}'
TestProposal "DIGITDIFF 7"  '{"proposal":1,"amount":1,"basis":"stake","contract_type":"DIGITDIFF","currency":"USD","duration":5,"duration_unit":"t","underlying_symbol":"R_100","barrier":7}'

# 7. Otros símbolos
TestProposal "CALL R_50"    '{"proposal":1,"amount":1,"basis":"stake","contract_type":"CALL","currency":"USD","duration":5,"duration_unit":"t","underlying_symbol":"R_50"}'
TestProposal "CALL R_75"    '{"proposal":1,"amount":1,"basis":"stake","contract_type":"CALL","currency":"USD","duration":5,"duration_unit":"t","underlying_symbol":"R_75"}'
TestProposal "CALL 1HZ100V" '{"proposal":1,"amount":1,"basis":"stake","contract_type":"CALL","currency":"USD","duration":5,"duration_unit":"t","underlying_symbol":"1HZ100V"}'

$ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "done", $cts.Token).Wait()
Write-Host "`n[DONE]" -ForegroundColor Cyan
