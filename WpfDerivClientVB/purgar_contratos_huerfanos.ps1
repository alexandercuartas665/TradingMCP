# ============================================================
# purgar_contratos_huerfanos.ps1
#
# Marca como "Expirado" todas las operaciones live en la BD
# que quedaron huerfanas (contract_id NULL/vacio o estado
# Pendiente/Abierto pero sin actividad reciente).
#
# Uso (PowerShell):
#   cd C:\DesarrolloIA\Traiding\WpfDerivClientVB
#   .\purgar_contratos_huerfanos.ps1
#
# Si quieres simular sin escribir, usa:
#   .\purgar_contratos_huerfanos.ps1 -DryRun
# ============================================================

[CmdletBinding()]
param(
    [switch]$DryRun = $false
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# 1) Cargar config de BD desde el archivo del proyecto
$dbConfigPath = Join-Path $scriptDir "bin\Debug\net48\db_config.json"
if (-not (Test-Path $dbConfigPath)) {
    Write-Host "ERROR: No se encontro $dbConfigPath" -ForegroundColor Red
    Write-Host "Compila el proyecto primero o ajusta la ruta." -ForegroundColor Yellow
    exit 1
}
$cfg = Get-Content $dbConfigPath -Raw | ConvertFrom-Json
$connStr = "Host=$($cfg.Host);Port=$($cfg.Port);Database=$($cfg.Database);Username=$($cfg.Username);Password=$($cfg.Password);"

# 2) Cargar Npgsql.dll y dependencias transitivas
$binDir = Join-Path $scriptDir "bin\Debug\net48"
$npgsqlPath = Join-Path $binDir "Npgsql.dll"
if (-not (Test-Path $npgsqlPath)) {
    Write-Host "ERROR: No se encontro $npgsqlPath" -ForegroundColor Red
    Write-Host "Compila el proyecto al menos una vez para que se restauren los paquetes NuGet." -ForegroundColor Yellow
    exit 1
}

# CRITICAL: PowerShell no aplica binding redirects del App.config.
# Npgsql 8 pide System.Runtime.CompilerServices.Unsafe v4.0.4.1 pero el bin tiene v6.0.0.0.
# Registramos un AssemblyResolve handler que carga la DLL del bin segun el SimpleName.
# Cache para evitar recursion en busquedas repetidas de satellite resources.
$script:asmCache = @{}
$resolver = [System.ResolveEventHandler] {
    param($sender, $e)
    $simpleName = ($e.Name -split ',')[0].Trim()

    # Ignorar satellite resources (.resources) — causan recursion en .NET 8/9.
    if ($simpleName.EndsWith('.resources')) { return $null }

    # Cache para evitar dispatch recursivo del mismo nombre.
    if ($script:asmCache.ContainsKey($simpleName)) {
        return $script:asmCache[$simpleName]
    }
    $script:asmCache[$simpleName] = $null   # marcar tentativo

    $dllPath = Join-Path $binDir "$simpleName.dll"
    if (Test-Path $dllPath) {
        try {
            $asm = [System.Reflection.Assembly]::LoadFrom($dllPath)
            $script:asmCache[$simpleName] = $asm
            return $asm
        } catch {
            return $null
        }
    }
    return $null
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)

# Pre-cargar dependencias transitivas que Npgsql 8 necesita.
# Cargamos PRIMERO las dependencias (orden importa) y al final Npgsql.
$depsOrdenadas = @(
    "System.Runtime.CompilerServices.Unsafe.dll",
    "System.Numerics.Vectors.dll",
    "System.Buffers.dll",
    "System.Memory.dll",
    "System.Threading.Tasks.Extensions.dll",
    "System.ValueTuple.dll",
    "System.Text.Encodings.Web.dll",
    "Microsoft.Bcl.AsyncInterfaces.dll",
    "System.Text.Json.dll",
    "System.Threading.Channels.dll"
)
foreach ($dep in $depsOrdenadas) {
    $depPath = Join-Path $binDir $dep
    if (Test-Path $depPath) {
        try { [System.Reflection.Assembly]::LoadFrom($depPath) | Out-Null } catch { }
    }
}
[System.Reflection.Assembly]::LoadFrom($npgsqlPath) | Out-Null

# 3) Conectar y contar antes
$conn = New-Object Npgsql.NpgsqlConnection($connStr)
$conn.Open()
Write-Host "Conectado a $($cfg.Host):$($cfg.Port)/$($cfg.Database)" -ForegroundColor Green

# Conteo de operaciones huerfanas: TODAS las ops live sin contract_id
# (incluye Pendiente, Abierto, Ganado, Perdido sin contract_id real — fantasmas).
$sqlCount = @"
SELECT COUNT(*) FROM public.backtest_operaciones o
JOIN public.backtest_sesiones s ON s.id = o.sesion_id
WHERE s.tipo_sesion = 'live'
  AND (o.contract_id IS NULL OR o.contract_id = '')
  AND o.estado <> 'Oculto';
"@
$cmd = $conn.CreateCommand()
$cmd.CommandText = $sqlCount
$count = [int]$cmd.ExecuteScalar()
Write-Host ""
Write-Host "Operaciones a marcar como Expirado (sin contract_id, estado Pendiente/Abierto): $count" -ForegroundColor Cyan

# Tambien mostrar las que SI tienen contract_id pero llevan tiempo "Pendiente"/"Abierto"
$sqlConContractId = @"
SELECT COUNT(*) FROM public.backtest_operaciones
WHERE estado IN ('Pendiente', 'Abierto')
  AND contract_id IS NOT NULL AND contract_id <> '';
"@
$cmd2 = $conn.CreateCommand()
$cmd2.CommandText = $sqlConContractId
$countConId = [int]$cmd2.ExecuteScalar()
Write-Host "Operaciones con contract_id que siguen Pendiente/Abierto (no se tocan): $countConId" -ForegroundColor DarkGray

if ($count -eq 0) {
    Write-Host ""
    Write-Host "No hay operaciones huerfanas para purgar." -ForegroundColor Green
    $conn.Close()
    exit 0
}

# Detectar tambien sesiones live mal taggeadas (replay BD que se guardo como 'live').
# Heuristica: una sesion live cuyas ops NUNCA tuvieron contract_id no es live real.
$sqlSesionesMaltagged = @"
SELECT COUNT(*) FROM public.backtest_sesiones s
WHERE s.tipo_sesion = 'live'
  AND NOT EXISTS (
    SELECT 1 FROM public.backtest_operaciones o
    WHERE o.sesion_id = s.id
      AND o.contract_id IS NOT NULL AND o.contract_id <> ''
  )
  AND EXISTS (
    SELECT 1 FROM public.backtest_operaciones o2
    WHERE o2.sesion_id = s.id
  );
"@
$cmd3 = $conn.CreateCommand()
$cmd3.CommandText = $sqlSesionesMaltagged
$countSesMal = [int]$cmd3.ExecuteScalar()
Write-Host "Sesiones live mal taggeadas (probables replays): $countSesMal" -ForegroundColor DarkYellow

# 4) Confirmar y ejecutar
if ($DryRun) {
    Write-Host ""
    Write-Host "[DRY RUN] No se modifica nada. Para ejecutar, corre el script sin -DryRun." -ForegroundColor Yellow
    $conn.Close()
    exit 0
}

Write-Host ""
$confirm = Read-Host "Marcar las $count operaciones como Expirado? (s/N)"
if ($confirm -ne 's' -and $confirm -ne 'S') {
    Write-Host "Cancelado." -ForegroundColor Yellow
    $conn.Close()
    exit 0
}

$sqlUpdate = @"
UPDATE public.backtest_operaciones o
SET estado = 'Oculto'
FROM public.backtest_sesiones s
WHERE s.id = o.sesion_id
  AND s.tipo_sesion = 'live'
  AND (o.contract_id IS NULL OR o.contract_id = '')
  AND o.estado <> 'Oculto';
"@
$cmdUpd = $conn.CreateCommand()
$cmdUpd.CommandText = $sqlUpdate
$rows = $cmdUpd.ExecuteNonQuery()
Write-Host ""
Write-Host "OK: $rows operaciones marcadas como 'Oculto'." -ForegroundColor Green

# Re-taggear sesiones que eran replay pero quedaron como 'live'
$sqlReclasifSes = @"
UPDATE public.backtest_sesiones s
SET tipo_sesion = 'backtest', activa = false
WHERE s.tipo_sesion = 'live'
  AND NOT EXISTS (
    SELECT 1 FROM public.backtest_operaciones o
    WHERE o.sesion_id = s.id
      AND o.contract_id IS NOT NULL AND o.contract_id <> ''
  )
  AND EXISTS (
    SELECT 1 FROM public.backtest_operaciones o2 WHERE o2.sesion_id = s.id
  );
"@
$cmdReclasif = $conn.CreateCommand()
$cmdReclasif.CommandText = $sqlReclasifSes
$rowsSes = $cmdReclasif.ExecuteNonQuery()
Write-Host "OK: $rowsSes sesiones re-clasificadas de 'live' a 'backtest'." -ForegroundColor Green

$conn.Close()
