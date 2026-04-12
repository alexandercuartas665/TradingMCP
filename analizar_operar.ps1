param(
    [string]$Symbol   = "R_100",
    [double]$Amount   = 1.0,
    [int]   $Duration = 5,
    [string]$DurUnit  = "t",
    [switch]$AutoTrade
)

Write-Host "=== ANALISIS DE MERCADO ===" -ForegroundColor Cyan
Write-Host "Simbolo: $Symbol  Monto: $Amount  Duracion: $Duration $DurUnit"
Write-Host ""

# 1. Obtener velas via WebSocket publico Deriv
Write-Host "Obteniendo 50 velas de $Symbol..." -ForegroundColor Yellow
$ws = New-Object System.Net.WebSockets.ClientWebSocket
$ws.ConnectAsync([Uri]"wss://ws.derivws.com/websockets/v3?app_id=1089", [Threading.CancellationToken]::None).Wait()
$req   = "{`"ticks_history`":`"$Symbol`",`"count`":50,`"end`":`"latest`",`"granularity`":60,`"style`":`"candles`"}"
$bytes = [Text.Encoding]::UTF8.GetBytes($req)
$ws.SendAsync([ArraySegment[byte]]::new($bytes), "Text", $true, [Threading.CancellationToken]::None).Wait()
$buf = New-Object byte[] 65536
$r   = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), [Threading.CancellationToken]::None).Result
$raw = [Text.Encoding]::UTF8.GetString($buf, 0, $r.Count)
$ws.Dispose()

$data    = $raw | ConvertFrom-Json
$candles = $data.candles
if (-not $candles) {
    Write-Host "ERROR: no se recibieron velas." -ForegroundColor Red
    Write-Host $raw
    exit 1
}
Write-Host "Velas recibidas: $($candles.Count)" -ForegroundColor Green

# 2. Arrays de precios
$closes = $candles | ForEach-Object { [decimal]$_.close }
$opens  = $candles | ForEach-Object { [decimal]$_.open  }

# 3. EMA
function Get-EMA([decimal[]]$p, [int]$n) {
    if ($p.Count -lt $n) { return [decimal]0 }
    $k = [decimal](2.0 / ($n + 1))
    $s = [decimal]0
    for ($i = 0; $i -lt $n; $i++) { $s += $p[$i] }
    $e = $s / $n
    for ($i = $n; $i -lt $p.Count; $i++) { $e = ($p[$i] - $e) * $k + $e }
    return [math]::Round($e, 5)
}
$ema5  = Get-EMA $closes 5
$ema10 = Get-EMA $closes 10

# 4. RSI(14)
$gains = @(); $losses = @()
for ($i = 1; $i -lt $closes.Count; $i++) {
    $d = $closes[$i] - $closes[$i-1]
    if ($d -gt 0) { $gains += $d; $losses += [decimal]0 }
    else          { $gains += [decimal]0; $losses += [math]::Abs($d) }
}
$ag = [decimal]0; $al = [decimal]0
for ($i = 0; $i -lt 14; $i++) { $ag += $gains[$i]; $al += $losses[$i] }
$ag /= 14; $al /= 14
for ($i = 14; $i -lt $gains.Count; $i++) {
    $ag = ($ag * 13 + $gains[$i]) / 14
    $al = ($al * 13 + $losses[$i]) / 14
}
$rsi = if ($al -eq 0) { [decimal]100 } else { [math]::Round(100 - (100 / (1 + $ag / $al)), 2) }

# 5. Score
$score = 0
if    ($ema5 -gt $ema10) { $score++ }
elseif($ema5 -lt $ema10) { $score-- }
if    ($rsi -lt 30) { $score += 2 }
elseif($rsi -gt 70) { $score -= 2 }
$last3 = $candles | Select-Object -Last 3
foreach ($c in $last3) {
    $cl = [decimal]$c.close; $op = [decimal]$c.open
    if    ($cl -gt $op) { $score++ }
    elseif($cl -lt $op) { $score-- }
}
$conf = [math]::Round([math]::Abs($score) / 5.0 * 100, 0)
$dir  = if ($score -gt 0) { "CALL" } elseif ($score -lt 0) { "PUT" } else { "NEUTRAL" }

# 6. Mostrar resultado
Write-Host ""
Write-Host "EMA(5)  : $ema5"
Write-Host "EMA(10) : $ema10"
if ($rsi -lt 30)      { Write-Host "RSI(14) : $rsi  <- SOBREVENTA" -ForegroundColor Green }
elseif ($rsi -gt 70)  { Write-Host "RSI(14) : $rsi  <- SOBRECOMPRA" -ForegroundColor Red }
else                  { Write-Host "RSI(14) : $rsi" }
$scoreStr = if ($score -gt 0) { "+$score" } else { "$score" }
Write-Host "Score   : $scoreStr / 5"
Write-Host ""

# Ultimas 5 velas
Write-Host "Ultimas 5 velas (O / H / L / C):" -ForegroundColor DarkCyan
$candles | Select-Object -Last 5 | ForEach-Object {
    $cl = [decimal]$_.close; $op = [decimal]$_.open
    $arrow = if ($cl -gt $op) { "^" } else { "v" }
    Write-Host ("  " + $arrow + "  O:" + $_.open + "  H:" + $_.high + "  L:" + $_.low + "  C:" + $_.close)
}
Write-Host ""

if ($dir -eq "NEUTRAL") {
    Write-Host "NEUTRAL - Sin senal clara. No se opera." -ForegroundColor Yellow
    exit 0
}

$col = if ($dir -eq "CALL") { "Green" } else { "Red" }
Write-Host ("==> SENAL: " + $dir + "  " + $conf + " pct confianza") -ForegroundColor $col
Write-Host ""

# 7. Ejecutar si se pide
if ($AutoTrade) {
    if ($conf -lt 40) {
        Write-Host ("Confianza " + $conf + " pct insuficiente. Minimo 40 pct.") -ForegroundColor Yellow
        exit 0
    }
    Write-Host ("Enviando orden " + $dir + " " + $Symbol + " $" + $Amount + " x " + $Duration + $DurUnit + "...") -ForegroundColor $col

    $body = '{"contract_type":"' + $dir + '","symbol":"' + $Symbol + '","amount":' + $Amount + ',"basis":"stake","currency":"USD","duration":' + $Duration + ',"duration_unit":"' + $DurUnit + '"}'
    try {
        $resp = Invoke-RestMethod -Uri "http://localhost:8765/api/trade" -Method POST -Body $body -ContentType "application/json"
        if ($resp.ok) {
            Write-Host ""
            Write-Host "OK - OPERACION EJECUTADA" -ForegroundColor Green
            Write-Host ("   Contrato : " + $resp.contract_id)
            Write-Host ("   Tipo     : " + $resp.tipo)
            Write-Host ("   Simbolo  : " + $resp.simbolo)
            Write-Host ("   Monto    : $" + $resp.monto)
            Write-Host ("   Hora     : " + $resp.hora)
        } else {
            Write-Host ("ERROR: " + $resp.error) -ForegroundColor Red
        }
    } catch {
        Write-Host ("Excepcion: " + $_.Exception.Message) -ForegroundColor Red
    }
} else {
    Write-Host "Agrega -AutoTrade para ejecutar la operacion." -ForegroundColor DarkGray
}
