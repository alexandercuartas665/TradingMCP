# ============================================================
# importar_historico.ps1
#
# Importa datos OHLC historicos (hasta 10 anos) desde fuentes
# externas gratuitas e inserta en la misma tabla PostgreSQL.
#
# Fuentes:
#   Yahoo Finance -> 1d (hasta 10 anos, range=10y)
#   Yahoo Finance -> 1h (hasta 730 dias, 2 bloques de 365d)
#   4h se calcula agregando velas de 1h
#   5m no disponible historicamente (usar Deriv para datos recientes)
#
# Uso:
#   .\importar_historico.ps1                        (descarga solo lo que falta)
#   .\importar_historico.ps1 -SoloActivo "EURUSD" -SoloTF "1d"
#   .\importar_historico.ps1 -SoloTF "1h"          (solo 1h y 4h)
#   .\importar_historico.ps1 -Forzar               (re-descarga aunque ya este completo)
# ============================================================
param(
    [string]$SoloActivo  = "",
    [string]$SoloTF      = "",
    [string]$Desde       = "2019-01-01",
    [string]$Hasta       = (Get-Date -Format "yyyy-MM-dd"),
    [int]$DelaySegundos  = 2,
    [switch]$Forzar,      # Forzar descarga aunque la cobertura ya sea completa
    [switch]$SoloVer
)

# ============================================================
# LIMITES DE COBERTURA MAXIMA POR TIMEFRAME
# Si la BD ya cubre el rango completo (con tolerancia), se salta.
# ============================================================
$COBERTURA = @{
    "1d" = @{
        MaxAnios     = 10           # Yahoo provee hasta 10 anos de datos 1d
        TolerDias    = 60           # Margen: fines de semana + feriados
        Descripcion  = "10 anos"
    }
    "1h" = @{
        MaxDias      = 730          # Yahoo provee hasta 730 dias de datos 1h
        TolerDias    = 30           # Margen: ~1 mes de tolerancia
        Descripcion  = "730 dias"
    }
    "4h" = @{
        MaxDias      = 730          # Igual que 1h (derivado de el)
        TolerDias    = 30
        Descripcion  = "730 dias"
    }
}

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
$ErrorActionPreference = "Continue"

$ConfigFile = "C:\DesarrolloIA\Traiding\WpfDerivClientVB\bin\Debug\net48\db_config.json"

# ============================================================
# CATALOGO: Yahoo symbol, Stooq symbol, DB symbol
# ============================================================
$CATALOGO = @(
    @{ Nombre="EURUSD"; Stooq="eurusd";   Yahoo="EURUSD=X"; DbSymbol="frxEURUSD" }
    @{ Nombre="GBPUSD"; Stooq="gbpusd";   Yahoo="GBPUSD=X"; DbSymbol="frxGBPUSD" }
    @{ Nombre="USDCHF"; Stooq="usdchf";   Yahoo="USDCHF=X"; DbSymbol="frxUSDCHF" }
    @{ Nombre="USDJPY"; Stooq="usdjpy";   Yahoo="USDJPY=X"; DbSymbol="frxUSDJPY" }
    @{ Nombre="GBPJPY"; Stooq="gbpjpy";   Yahoo="GBPJPY=X"; DbSymbol="frxGBPJPY" }
    @{ Nombre="XAUUSD"; Stooq="xauusd";   Yahoo="GC=F";     DbSymbol="frxXAUUSD" }
    @{ Nombre="XAGUSD"; Stooq="xagusd";   Yahoo="SI=F";     DbSymbol="frxXAGUSD" }
    @{ Nombre="OIL_US"; Stooq="cl.f";     Yahoo="CL=F";     DbSymbol="frxUSOIL"  }
    @{ Nombre="NAS100"; Stooq="^ndx";     Yahoo="NQ=F";     DbSymbol="OTC_NDX"   }
    @{ Nombre="SP500";  Stooq="^spx";     Yahoo="ES=F";     DbSymbol="OTC_SPC"   }
)

if ($SoloActivo -ne "") {
    $CATALOGO = $CATALOGO | Where-Object { $_.Nombre -eq $SoloActivo -or $_.DbSymbol -eq $SoloActivo }
}

# Timeframes a importar por fuente
$TFS_STOOQ = @("1d")
$TFS_YAHOO = @("1h", "4h")   # 4h se agrega a partir de 1h
if ($SoloTF -ne "") {
    $TFS_STOOQ = @($TFS_STOOQ | Where-Object { $_ -eq $SoloTF })
    $TFS_YAHOO = @($TFS_YAHOO | Where-Object { $_ -eq $SoloTF -or ($SoloTF -eq "4h" -and $_ -eq "1h") })
}

# ============================================================
# CONEXION A POSTGRESQL (igual que reporte_bd.ps1)
# ============================================================
if (-not (Test-Path $ConfigFile)) {
    Write-Host "[ERROR] No se encontro db_config.json: $ConfigFile" -ForegroundColor Red; exit 1
}
$dbConf = Get-Content $ConfigFile -Raw | ConvertFrom-Json
$DB_HOST = $dbConf.Host; $DB_PORT = $dbConf.Port
$DB_NAME = $dbConf.Database; $DB_USER = $dbConf.Username; $DB_PASS = $dbConf.Password

$psqlExe = $null
foreach ($p in @("psql","C:\Program Files\PostgreSQL\16\bin\psql.exe","C:\Program Files\PostgreSQL\15\bin\psql.exe","C:\Program Files\PostgreSQL\14\bin\psql.exe")) {
    if (Get-Command $p -ErrorAction SilentlyContinue) { $psqlExe = $p; break }
    if (Test-Path $p) { $psqlExe = $p; break }
}

$useDocker = $false; $dockerContainer = ""
if (-not $psqlExe) {
    $portFilter = docker ps --format "{{.Names}} {{.Ports}}" 2>$null
    foreach ($line in $portFilter) {
        if ($line -match ":${DB_PORT}->") {
            $dockerContainer = ($line -split " ")[0].Trim()
            $testPsql = docker exec $dockerContainer psql --version 2>$null
            if ($LASTEXITCODE -eq 0) { $useDocker = $true; break }
        }
    }
    if (-not $useDocker) { Write-Host "[ERROR] No se encontro psql ni Docker." -ForegroundColor Red; exit 1 }
}

function Invoke-Psql([string]$Sql) {
    try {
        if ($useDocker) {
            # Pasar SQL por stdin (-i) para evitar limites de longitud de argumentos
            # y el error "StandardOutputEncoding" de PowerShell embedded
            $r = ($Sql | docker exec -i -e "PGPASSWORD=$DB_PASS" $dockerContainer `
                psql -h "127.0.0.1" -p 5432 -U $DB_USER -d $DB_NAME -t -A)
        } else {
            $env:PGPASSWORD = $DB_PASS
            $r = & $psqlExe -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME -t -A -c $Sql 2>&1
            $env:PGPASSWORD = ""
        }
    } catch {
        $r = @()
    }
    return $r
}

# ============================================================
# INSERT en lotes de 500 filas
# ============================================================
function Import-Candles {
    param($DbSymbol, $Timeframe, $Candles)

    if ($Candles.Count -eq 0) { return 0 }

    $platform = "deriv"    # misma plataforma para que el backtesting los encuentre
    $inserted  = 0
    $batchSize = 500
    $idx       = 0

    while ($idx -lt $Candles.Count) {
        $lote = $Candles[$idx .. ([Math]::Min($idx + $batchSize - 1, $Candles.Count - 1))]
        $idx += $batchSize

        $values = $lote | ForEach-Object {
            # Validar OHLC antes de generar SQL (protege contra NaN de futuros)
            try {
                $ov = [double]$_.Open; $hv = [double]$_.High
                $lv = [double]$_.Low;  $cv = [double]$_.Close
            } catch { return }
            if ([double]::IsNaN($ov) -or [double]::IsNaN($hv) -or [double]::IsNaN($lv) -or [double]::IsNaN($cv)) { return }
            if ($ov -eq 0 -or $cv -eq 0) { return }
            $openTime = (([datetime]"1970-01-01 00:00:00Z").AddSeconds($_.Epoch).ToUniversalTime()).ToString("yyyy-MM-dd HH:mm:ss")
            "('$platform','$DbSymbol','$Timeframe',$($_.Epoch),'$openTime',$ov,$hv,$lv,$cv,true)"
        }
        $values = @($values | Where-Object { $_ -ne $null -and $_ -ne "" })

        if ($values.Count -eq 0) { continue }

        $sql = "INSERT INTO public.candles (platform,symbol,timeframe,epoch,open_time,open_price,high_price,low_price,close_price,is_closed) VALUES " +
               ($values -join ",") +
               " ON CONFLICT (platform,symbol,timeframe,epoch) DO NOTHING;"

        Invoke-Psql $sql | Out-Null
        $inserted += $values.Count
    }
    return $inserted
}

# ============================================================
# COBERTURA: consulta COUNT + MIN + MAX de la BD
# Devuelve hashtable: Count, MinEpoch, MaxEpoch, MinFecha, MaxFecha
# ============================================================
function Get-Cobertura($DbSymbol, $Timeframe) {
    # LOWER(platform) para tolerar 'Deriv' (API) vs 'deriv' (importador)
    $sql = "SELECT COUNT(*), COALESCE(MIN(epoch),0), COALESCE(MAX(epoch),0) " +
           "FROM public.candles WHERE LOWER(platform)='deriv' AND symbol='$DbSymbol' AND timeframe='$Timeframe';"
    $r = Invoke-Psql $sql
    $line = $r | Where-Object { $_ -match '^\d+\|' } | Select-Object -First 1
    if (-not $line) { return @{ Count=0; MinEpoch=0; MaxEpoch=0; MinFecha=$null; MaxFecha=$null } }
    $parts = $line -split '\|'
    $cnt   = [long]$parts[0]
    $minE  = [long]$parts[1]
    $maxE  = [long]$parts[2]
    $epoch0 = [datetime]"1970-01-01 00:00:00Z"
    return @{
        Count    = $cnt
        MinEpoch = $minE
        MaxEpoch = $maxE
        MinFecha = if ($minE -gt 0) { $epoch0.AddSeconds($minE).ToLocalTime() } else { $null }
        MaxFecha = if ($maxE -gt 0) { $epoch0.AddSeconds($maxE).ToLocalTime() } else { $null }
    }
}

# ============================================================
# DECIDE si hace falta descargar segun cobertura en BD
#
# Se consideran DOS condiciones independientes:
#
#   A) Cobertura historica: MinFecha <= (hoy - MaxDias + TolerDias)
#      -> Verifica que tenemos el pasado suficiente
#
#   B) Datos recientes: MaxFecha >= (hoy - MaxHuecoReciente)
#      -> Verifica que no nos quedamos sin datos recientes
#      -> Detecta casos como XAGUSD donde una descarga parcial
#         previa dejo datos hasta hace 1 ano y no mas
#
# Retorna $true si HAY QUE descargar, $false si ya esta completo.
# ============================================================
$MAX_HUECO_RECIENTE = @{
    "1d" = 7    # permitir hasta 7 dias sin la vela mas reciente (fines de semana)
    "1h" = 3    # hasta 3 dias de hueco en 1h
    "4h" = 3    # igual que 1h
}

function Test-NecesitaDescarga($Timeframe, $Cob) {
    if ($Forzar) { return $true }
    if ($Cob.Count -eq 0) { return $true }

    $hoy = [datetime]::Today
    $cfg = $COBERTURA[$Timeframe]
    if (-not $cfg) { return $true }

    # --- Condicion A: cobertura historica (MinFecha suficientemente atras) ---
    if ($cfg.ContainsKey("MaxAnios")) {
        $limiteDesde = $hoy.AddYears(-$cfg.MaxAnios).AddDays($cfg.TolerDias)
    } else {
        $limiteDesde = $hoy.AddDays(-$cfg.MaxDias).AddDays($cfg.TolerDias)
    }
    if ($Cob.MinFecha -eq $null -or $Cob.MinFecha -gt $limiteDesde) {
        return $true   # faltan datos historicos al inicio
    }

    # --- Condicion B: datos recientes (MaxFecha no demasiado vieja) ---
    $huecoMax = $MAX_HUECO_RECIENTE[$Timeframe]
    if ($huecoMax -and $Cob.MaxFecha -ne $null) {
        $limiteHasta = $hoy.AddDays(-$huecoMax)
        if ($Cob.MaxFecha -lt $limiteHasta) {
            return $true   # datos demasiado viejos, hay hueco reciente
        }
    }

    return $false   # ambas condiciones OK -> completo
}

# Compatibilidad: Get-Count para conteo simple
function Get-Count($DbSymbol, $Timeframe) {
    # Contar solo los insertados por este script (platform='deriv')
    $sql = "SELECT COUNT(*) FROM public.candles WHERE platform='deriv' AND symbol='$DbSymbol' AND timeframe='$Timeframe';"
    $r = Invoke-Psql $sql
    return [long]($r | Where-Object { $_ -match '^\d+$' } | Select-Object -First 1)
}

# ============================================================
# YAHOO FINANCE: descarga generica OHLC
#
# Limites verificados:
#   1d  -> range=10y  funciona (hasta 10 anos, ~2610 velas para EURUSD)
#   1h  -> max 730 dias desde hoy, se pide en 2 chunks de 365d
#   5m  -> max 60 dias desde hoy
#
# Requiere headers especificos (Chrome UA + Referer).
# ============================================================
$YH = @{
    "User-Agent"      = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
    "Accept"          = "*/*"
    "Accept-Language" = "en-US,en;q=0.9"
    "Origin"          = "https://finance.yahoo.com"
    "Referer"         = "https://finance.yahoo.com/"
}

function Invoke-YahooChart($YahooSymbol, $Interval, $Range, $Period1 = 0, $Period2 = 0) {
    $sym = [uri]::EscapeDataString($YahooSymbol)
    if ($Period1 -gt 0 -and $Period2 -gt 0) {
        $url = "https://query1.finance.yahoo.com/v8/finance/chart/$sym`?interval=$Interval&period1=$Period1&period2=$Period2&includePrePost=false"
    } else {
        $url = "https://query1.finance.yahoo.com/v8/finance/chart/$sym`?interval=$Interval&range=$Range&includePrePost=false"
    }

    try {
        $resp = Invoke-RestMethod -Uri $url -Headers $YH -UseBasicParsing -TimeoutSec 90 -ErrorAction Stop
        $result = $resp.chart.result
        if (-not $result -or $result.Count -eq 0) { return @() }

        $ts     = $result[0].timestamp
        $quotes = $result[0].indicators.quote[0]
        if (-not $ts -or $ts.Count -eq 0) { return @() }

        $candles = @()
        for ($i = 0; $i -lt $ts.Count; $i++) {
            try {
                $o = $quotes.open[$i]; $h = $quotes.high[$i]
                $l = $quotes.low[$i];  $c = $quotes.close[$i]
                if ($o -eq $null -or $h -eq $null -or $l -eq $null -or $c -eq $null) { continue }
                try { $od = [double]$o; $hd = [double]$h; $ld = [double]$l; $cd = [double]$c } catch { continue }
                # Filtrar NaN (aparece en futuros durante rollover) y ceros
                if ([double]::IsNaN($od) -or [double]::IsNaN($hd) -or [double]::IsNaN($ld) -or [double]::IsNaN($cd)) { continue }
                if ($od -eq 0 -or $cd -eq 0 -or $hd -eq 0 -or $ld -eq 0) { continue }
                $candles += @{
                    Epoch = [long]$ts[$i]
                    Open  = [Math]::Round($od, 6)
                    High  = [Math]::Round($hd, 6)
                    Low   = [Math]::Round($ld, 6)
                    Close = [Math]::Round($cd, 6)
                }
            } catch { continue }
        }
        return $candles
    } catch {
        $msg = $_.ToString()
        if ($msg -match "within the last 730") {
            Write-Host "    [LIMITE] 1h solo disponible en los ultimos 730 dias" -ForegroundColor DarkGray
        } else {
            Write-Host "    [ERROR] Yahoo Finance ($YahooSymbol $Interval): $msg" -ForegroundColor Red
        }
        return @()
    }
}

function Get-YahooDiario($YahooSymbol) {
    # 10 anos de datos diarios (verificado: ~2610 velas desde 2016)
    Write-Host " range=10y..." -NoNewline
    return Invoke-YahooChart $YahooSymbol "1d" "10y"
}

function Get-YahooHorario($YahooSymbol) {
    # Yahoo limita 1h a los ultimos 730 dias.
    # Se pide en 2 bloques de 365d para evitar timeouts.
    $now   = [long]([datetime]::UtcNow - [datetime]"1970-01-01Z").TotalSeconds
    $d365  = $now - (365 * 86400)
    $d730  = $now - (730 * 86400)

    Write-Host " bloque1(365d)..." -NoNewline
    $c1 = @(Invoke-YahooChart $YahooSymbol "60m" "" $d365 $now)
    Write-Host " $($c1.Count) velas, bloque2(365-730d)..." -NoNewline
    Start-Sleep -Seconds 1
    $c2 = @(Invoke-YahooChart $YahooSymbol "60m" "" $d730 $d365)
    Write-Host " $($c2.Count) velas." -NoNewline

    # Combinar y deduplicar por epoch
    $merged = @{}
    foreach ($c in ($c1 + $c2)) { $merged[$c.Epoch] = $c }
    return @($merged.Values | Sort-Object { $_.Epoch })
}

# ============================================================
# AGREGAR: convertir velas 1h en 4h
# Limites 4h UTC: 00:00 04:00 08:00 12:00 16:00 20:00
# ============================================================
function Aggregate-4h($candles1h) {
    if ($candles1h.Count -eq 0) { return @() }

    $grupos = @{}
    foreach ($c in $candles1h) {
        $key = [long]([Math]::Floor($c.Epoch / 14400) * 14400)
        if (-not $grupos.ContainsKey($key)) { $grupos[$key] = @() }
        $grupos[$key] += $c
    }

    $candles4h = @()
    foreach ($key in ($grupos.Keys | Sort-Object)) {
        $g = $grupos[$key] | Sort-Object { $_.Epoch }
        if ($g.Count -lt 2) { continue }   # descartar si muy pocos candles en el bloque

        $candles4h += @{
            Epoch = $key
            Open  = $g[0].Open
            High  = ($g | Measure-Object -Property High -Maximum).Maximum
            Low   = ($g | Measure-Object -Property Low  -Minimum).Minimum
            Close = $g[-1].Close
        }
    }
    return $candles4h
}

# ============================================================
# INICIO
# ============================================================
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  IMPORTADOR DE DATOS HISTORICOS" -ForegroundColor Cyan
Write-Host "  Fuentes: Yahoo Finance (1d/10y + 1h/730d + 4h agregado)" -ForegroundColor Cyan
Write-Host "  Periodo solicitado: $Desde -> $Hasta" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

if ($useDocker) {
    Write-Host "  DB via docker: $dockerContainer" -ForegroundColor DarkGray
} else {
    Write-Host "  DB: $DB_HOST`:$DB_PORT / $DB_NAME" -ForegroundColor DarkGray
}

# Verificar conexion
$ping = Invoke-Psql "SELECT 1;"
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] No se pudo conectar a PostgreSQL." -ForegroundColor Red; exit 1
}
Write-Host "  [OK] Conexion PostgreSQL" -ForegroundColor Green
Write-Host ""

$totalInsertado = 0

foreach ($activo in $CATALOGO) {
    Write-Host "------------------------------------------------------------" -ForegroundColor DarkCyan
    Write-Host "  $($activo.Nombre)  (DB: $($activo.DbSymbol))" -ForegroundColor White
    Write-Host "------------------------------------------------------------" -ForegroundColor DarkCyan

    # ---- 1D desde Yahoo Finance ----
    if ("1d" -in $TFS_STOOQ) {
        $cob1d = Get-Cobertura $activo.DbSymbol "1d"
        $minStr = if ($cob1d.MinFecha) { $cob1d.MinFecha.ToString("yyyy-MM-dd") } else { "---" }
        $maxStr = if ($cob1d.MaxFecha) { $cob1d.MaxFecha.ToString("yyyy-MM-dd") } else { "---" }

        Write-Host "  [1d] BD: $($cob1d.Count) registros ($minStr -> $maxStr)" -NoNewline -ForegroundColor DarkGray

        if (-not (Test-NecesitaDescarga "1d" $cob1d)) {
            Write-Host "  [OK completo - saltando]" -ForegroundColor Green
        } else {
            Write-Host "  [descargando...]" -ForegroundColor Yellow
            $candles = @(Get-YahooDiario $activo.Yahoo)

            if ($candles.Count -gt 0) {
                $n       = Import-Candles $activo.DbSymbol "1d" $candles
                $despues = Get-Count $activo.DbSymbol "1d"
                $nuevas  = $despues - $cob1d.Count
                Write-Host "        $($candles.Count) recibidas / $nuevas nuevas (total BD: $despues)" -ForegroundColor Green
                $totalInsertado += $nuevas
            } else {
                Write-Host "        Sin datos de Yahoo." -ForegroundColor Yellow
            }
            Start-Sleep -Seconds $DelaySegundos
        }
    }

    # ---- 1H desde Yahoo Finance ----
    $candles1h = @()
    if ("1h" -in $TFS_YAHOO -or "4h" -in $TFS_YAHOO) {
        $cob1h = Get-Cobertura $activo.DbSymbol "1h"
        $min1hStr = if ($cob1h.MinFecha) { $cob1h.MinFecha.ToString("yyyy-MM-dd") } else { "---" }
        $max1hStr = if ($cob1h.MaxFecha) { $cob1h.MaxFecha.ToString("yyyy-MM-dd") } else { "---" }

        Write-Host "  [1h] BD: $($cob1h.Count) registros ($min1hStr -> $max1hStr)" -NoNewline -ForegroundColor DarkGray

        if (-not (Test-NecesitaDescarga "1h" $cob1h)) {
            Write-Host "  [OK completo - saltando]" -ForegroundColor Green
            # Para 4h: cargar desde BD en vez de re-descargar 1h solo para agregar
            # (se chequea 4h por separado abajo)
        } else {
            Write-Host "  [descargando...]" -ForegroundColor Yellow
            $candles1h = @(Get-YahooHorario $activo.Yahoo)

            if ($candles1h.Count -gt 0) {
                if ("1h" -in $TFS_YAHOO) {
                    $n       = Import-Candles $activo.DbSymbol "1h" $candles1h
                    $despues = Get-Count $activo.DbSymbol "1h"
                    $nuevas  = $despues - $cob1h.Count
                    Write-Host "        $($candles1h.Count) recibidas / $nuevas nuevas (total BD: $despues)" -ForegroundColor Green
                    $totalInsertado += $nuevas
                } else {
                    Write-Host "        $($candles1h.Count) recibidas (base para 4h)" -ForegroundColor DarkGray
                }
            } else {
                Write-Host "        Sin datos de Yahoo." -ForegroundColor Yellow
            }
            Start-Sleep -Seconds $DelaySegundos
        }
    }

    # ---- 4H agregado desde 1H ----
    if ("4h" -in $TFS_YAHOO) {
        $cob4h = Get-Cobertura $activo.DbSymbol "4h"
        $min4hStr = if ($cob4h.MinFecha) { $cob4h.MinFecha.ToString("yyyy-MM-dd") } else { "---" }
        $max4hStr = if ($cob4h.MaxFecha) { $cob4h.MaxFecha.ToString("yyyy-MM-dd") } else { "---" }

        Write-Host "  [4h] BD: $($cob4h.Count) registros ($min4hStr -> $max4hStr)" -NoNewline -ForegroundColor DarkGray

        if (-not (Test-NecesitaDescarga "4h" $cob4h)) {
            Write-Host "  [OK completo - saltando]" -ForegroundColor Green
        } elseif ($candles1h.Count -gt 0) {
            # Velas 1h descargadas en esta sesion -> agregar en memoria
            Write-Host "  [agregando desde $($candles1h.Count) velas 1h descargadas...]" -ForegroundColor Yellow
            $candles4h = @(Aggregate-4h $candles1h)
            if ($candles4h.Count -gt 0) {
                $n       = Import-Candles $activo.DbSymbol "4h" $candles4h
                $despues = Get-Count $activo.DbSymbol "4h"
                $nuevas  = $despues - $cob4h.Count
                Write-Host "        $($candles4h.Count) velas 4h / $nuevas nuevas (total BD: $despues)" -ForegroundColor Green
                $totalInsertado += $nuevas
            } else {
                Write-Host "        Sin velas 4h." -ForegroundColor Yellow
            }
        } else {
            # 1h fue saltado (ya completo) pero 4h esta incompleto.
            # Agregar 4h directamente en SQL desde los datos 1h que ya estan en BD.
            Write-Host "  [generando 4h desde 1h existente en BD...]" -ForegroundColor Yellow
            $sym = $activo.DbSymbol
            $sql4h = @"
INSERT INTO public.candles
    (platform, symbol, timeframe, epoch, open_time,
     open_price, high_price, low_price, close_price, is_closed)
SELECT
    'deriv', '$sym', '4h',
    (epoch / 14400) * 14400,
    to_timestamp((epoch / 14400) * 14400) AT TIME ZONE 'UTC',
    (array_agg(open_price  ORDER BY epoch))[1],
    MAX(high_price),
    MIN(low_price),
    (array_agg(close_price ORDER BY epoch DESC))[1],
    true
FROM public.candles
WHERE LOWER(platform) = 'deriv'
  AND symbol    = '$sym'
  AND timeframe = '1h'
GROUP BY (epoch / 14400) * 14400
HAVING COUNT(*) >= 2
ON CONFLICT (platform, symbol, timeframe, epoch) DO NOTHING;
"@
            Invoke-Psql $sql4h | Out-Null
            $despues = Get-Count $activo.DbSymbol "4h"
            $nuevas  = $despues - $cob4h.Count
            Write-Host "        $nuevas nuevas velas 4h (total BD: $despues)" -ForegroundColor Green
            $totalInsertado += $nuevas
        }
    }

    Write-Host ""
}

# ============================================================
# RESUMEN FINAL
# ============================================================
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  IMPORTACION COMPLETADA" -ForegroundColor Green
Write-Host "  Total nuevas velas en BD: $totalInsertado" -ForegroundColor Green
Write-Host ""
Write-Host "  Cobertura por fuente:" -ForegroundColor DarkGray
Write-Host "    1d  -> Yahoo Finance, hasta 10 anos de historia" -ForegroundColor DarkGray
Write-Host "    1h  -> Yahoo Finance, ultimos 730 dias (2 bloques de 365d)" -ForegroundColor DarkGray
Write-Host "    4h  -> Agregado desde 1h (misma cobertura)" -ForegroundColor DarkGray
Write-Host "    5m  -> Solo via Deriv (~380 dias). Ejecuta descargar_masivo.ps1" -ForegroundColor DarkGray
Write-Host "============================================================" -ForegroundColor Cyan
