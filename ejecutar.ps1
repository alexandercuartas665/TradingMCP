# Script de PowerShell para inicializar y correr el conector a Deriv
Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "    Inicializando Conector API Deriv - Traiding    " -ForegroundColor Cyan
Write-Host "===================================================" -ForegroundColor Cyan

# 1. Verificar si Python existe
$pythonExists = Get-Command "python" -ErrorAction SilentlyContinue

if (-not $pythonExists) {
    Write-Host "[X] ERROR: No se encontró Python instalado o no está en el PATH." -ForegroundColor Red
    Write-Host "Por favor, instala Python desde python.org e intenta de nuevo." -ForegroundColor Yellow
    Pause
    Exit
}

# 2. Verificar o instalar la librería 'websockets'
Write-Host "[*] Verificando dependencias (librería websockets)..." -ForegroundColor Yellow
$websocketsCheck = python -c "import websockets" 2>$null

if ($LASTEXITCODE -ne 0) {
    Write-Host "[*] Instalando librería 'websockets'..." -ForegroundColor Yellow
    python -m pip install websockets
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[X] ERROR: Falló la instalación de la librería websockets." -ForegroundColor Red
        Pause
        Exit
    }
} else {
    Write-Host "[OK] Dependencias correctas." -ForegroundColor Green
}

# 3. Ejecutar el script principal
Write-Host "`n[*] Ejecutando consulta de símbolos activos..." -ForegroundColor Yellow
python .\get_active_symbols.py

Write-Host "`n[*] Script finalizado." -ForegroundColor Cyan
Pause
