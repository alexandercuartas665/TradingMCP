# ============================================================
# backup_bd.ps1
#
# Genera un backup completo de la BD PostgreSQL del proyecto.
# Lee la configuracion de bin\Debug\net48\db_config.json y usa
# pg_dump.exe (incluido con PostgreSQL).
#
# Uso:
#   .\backup_bd.ps1                          # backup plain SQL (default)
#   .\backup_bd.ps1 -Formato custom          # binario comprimido (.dump)
#   .\backup_bd.ps1 -Carpeta C:\backups      # carpeta destino especifica
#   .\backup_bd.ps1 -SoloDatos               # solo datos, sin schema
#   .\backup_bd.ps1 -SoloSchema              # solo schema, sin datos
# ============================================================

[CmdletBinding()]
param(
    [ValidateSet("plain", "custom", "tar")]
    [string]$Formato = "plain",

    [string]$Carpeta = "",

    [switch]$SoloDatos = $false,
    [switch]$SoloSchema = $false
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# 1) Cargar config
$dbConfigPath = Join-Path $scriptDir "bin\Debug\net48\db_config.json"
if (-not (Test-Path $dbConfigPath)) {
    Write-Host "ERROR: No se encontro $dbConfigPath" -ForegroundColor Red
    exit 1
}
$cfg = Get-Content $dbConfigPath -Raw | ConvertFrom-Json
Write-Host "BD: $($cfg.Host):$($cfg.Port)/$($cfg.Database) (user: $($cfg.Username))" -ForegroundColor Cyan

# 2) Detectar si la BD corre en un contenedor Docker en este host
$dockerContainer = $null
$pgDump = $null
$useDocker = $false

if ($cfg.Host -eq "localhost" -or $cfg.Host -eq "127.0.0.1") {
    $dockerOk = Get-Command docker -ErrorAction SilentlyContinue
    if ($dockerOk) {
        # Buscar contenedor que mapee al puerto configurado
        $containers = docker ps --format "{{.Names}}|{{.Image}}|{{.Ports}}" 2>$null
        foreach ($c in $containers) {
            $partes = $c -split '\|'
            $name = $partes[0]
            $image = $partes[1]
            $ports = $partes[2]
            if ($image -match "postgres" -and $ports -match ":$($cfg.Port)->5432") {
                $dockerContainer = $name
                $useDocker = $true
                break
            }
        }
    }
}

if ($useDocker) {
    Write-Host "Docker container detectado: $dockerContainer" -ForegroundColor Green
    Write-Host "Usaremos pg_dump dentro del contenedor (no requiere instalar nada local)." -ForegroundColor DarkGray
} else {
    # Fallback: buscar pg_dump.exe local
    $candidatos = @()
    $enPath = Get-Command pg_dump.exe -ErrorAction SilentlyContinue
    if ($enPath) { $candidatos += $enPath.Source }
    foreach ($ver in @("17","16","15","14","13","12","11","10","9.6","9.5")) {
        $p = "C:\Program Files\PostgreSQL\$ver\bin\pg_dump.exe"
        if (Test-Path $p) { $candidatos += $p }
        $p2 = "C:\Program Files (x86)\PostgreSQL\$ver\bin\pg_dump.exe"
        if (Test-Path $p2) { $candidatos += $p2 }
    }
    if ($candidatos.Count -eq 0) {
        Write-Host "ERROR: No hay pg_dump local ni contenedor Docker postgres en puerto $($cfg.Port)." -ForegroundColor Red
        Write-Host "Opciones:" -ForegroundColor Yellow
        Write-Host "  1. Instala PostgreSQL client tools (https://www.postgresql.org/download/windows/)" -ForegroundColor Yellow
        Write-Host "  2. Asegurate de que Docker este corriendo y el contenedor traiding_postgres este activo" -ForegroundColor Yellow
        exit 1
    }
    $pgDump = $candidatos[0]
    Write-Host "pg_dump local: $pgDump" -ForegroundColor DarkGray
}

# 3) Carpeta destino
if (-not $Carpeta) {
    $Carpeta = Join-Path $scriptDir "backups"
}
if (-not (Test-Path $Carpeta)) {
    New-Item -Path $Carpeta -ItemType Directory | Out-Null
}

# 4) Nombre de archivo
$timestamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
$ext = switch ($Formato) {
    "plain"  { ".sql" }
    "custom" { ".dump" }
    "tar"    { ".tar" }
}
$sufijo = ""
if ($SoloDatos)  { $sufijo = "_data" }
if ($SoloSchema) { $sufijo = "_schema" }
$archivo = Join-Path $Carpeta "$($cfg.Database)_$timestamp$sufijo$ext"

Write-Host "Destino: $archivo" -ForegroundColor Cyan
Write-Host ""

# 5) Ejecutar pg_dump (en Docker o local)
$sw = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "Ejecutando pg_dump..." -ForegroundColor Yellow

$flagFormato = $Formato.Substring(0,1)   # p / c / t
$flagsExtra = @()
if ($SoloDatos)  { $flagsExtra += "--data-only" }
if ($SoloSchema) { $flagsExtra += "--schema-only" }
if ($Formato -eq "plain" -and -not $SoloDatos) {
    $flagsExtra += "--clean"
    $flagsExtra += "--if-exists"
}

if ($useDocker) {
    # Dump dentro del contenedor a /tmp y copiar al host con docker cp
    $tmpEnContainer = "/tmp/dump_$timestamp$ext"
    $dockerArgs = @(
        "exec",
        "-e", "PGPASSWORD=$($cfg.Password)",
        $dockerContainer,
        "pg_dump",
        "-U", $cfg.Username,
        "-d", $cfg.Database,
        "-F", $flagFormato,
        "-f", $tmpEnContainer
    ) + $flagsExtra
    & docker @dockerArgs 2>&1 | Where-Object { $_ -match "^pg_dump:" } | ForEach-Object {
        Write-Host "  $_" -ForegroundColor DarkGray
    }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: pg_dump (docker exec) termino con codigo $LASTEXITCODE" -ForegroundColor Red
        exit 1
    }
    # Copiar del contenedor al host
    & docker cp "${dockerContainer}:${tmpEnContainer}" "$archivo" 2>&1 | Out-Null
    if (-not (Test-Path $archivo)) {
        Write-Host "ERROR: No se pudo copiar el dump del contenedor al host." -ForegroundColor Red
        exit 1
    }
    # Limpieza del archivo temporal en el contenedor
    & docker exec $dockerContainer rm -f $tmpEnContainer 2>&1 | Out-Null
} else {
    $args = @(
        "-h", $cfg.Host,
        "-p", $cfg.Port,
        "-U", $cfg.Username,
        "-d", $cfg.Database,
        "-F", $flagFormato,
        "-v",
        "-f", $archivo
    ) + $flagsExtra

    $env:PGPASSWORD = $cfg.Password
    try {
        & $pgDump @args 2>&1 | ForEach-Object {
            if ($_ -match "^pg_dump:" -and $_ -notmatch "saving|reading|finding|dumping") {
                Write-Host "  $_" -ForegroundColor DarkGray
            }
        }
        if ($LASTEXITCODE -ne 0) {
            Write-Host "ERROR: pg_dump termino con codigo $LASTEXITCODE" -ForegroundColor Red
            exit 1
        }
    } finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    }
}
$sw.Stop()

# 7) Reporte final
$sizeMB = [math]::Round((Get-Item $archivo).Length / 1MB, 2)
Write-Host ""
Write-Host "OK. Backup completado en $($sw.Elapsed.TotalSeconds.ToString('F1'))s" -ForegroundColor Green
Write-Host "Archivo: $archivo" -ForegroundColor Green
Write-Host "Tamano:  $sizeMB MB" -ForegroundColor Green
Write-Host ""
Write-Host "Para restaurar en otro servidor:" -ForegroundColor Cyan
if ($Formato -eq "plain") {
    Write-Host "  psql -h <host> -U <user> -d <basedatos> -f `"$archivo`"" -ForegroundColor White
} else {
    Write-Host "  pg_restore -h <host> -U <user> -d <basedatos> --clean --if-exists `"$archivo`"" -ForegroundColor White
}
