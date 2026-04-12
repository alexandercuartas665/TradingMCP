# ============================================================
# descargar_todos.ps1
# Descarga 7 anos de datos historicos para todos los activos
# y temporalidades configurados.
#
# Uso:
#   .\descargar_todos.ps1
#   .\descargar_todos.ps1 -SoloTimeframes @("1d","1h")
#   .\descargar_todos.ps1 -SoloActivos @("frxEURUSD","frxXAUUSD")
# ============================================================
param(
    [string[]]$SoloTimeframes = @(),
    [string[]]$SoloActivos    = @(),
    [string]$Desde            = "2019-04-11",
    [string]$Hasta            = (Get-Date -Format "yyyy-MM-dd")
)

# NOTA: Verificar simbolos marcados con [?] antes de ejecutar.
$ACTIVOS = @(
    @{ Symbol = "frxEURUSD"; Nombre = "EURUSD"         }
    @{ Symbol = "frxGBPUSD"; Nombre = "GBPUSD"         }
    @{ Symbol = "frxUSDCHF"; Nombre = "USDCHF"         }
    @{ Symbol = "frxUSDJPY"; Nombre = "USDJPY"         }
    @{ Symbol = "frxGBPJPY"; Nombre = "GBPJPY"         }
    @{ Symbol = "frxXAUUSD"; Nombre = "XAUUSD (Oro)"   }
    @{ Symbol = "frxXAGUSD"; Nombre = "XAGUSD (Plata)" }
    @{ Symbol = "frxUSOIL";  Nombre = "OIL_US [?]"     }
    @{ Symbol = "OTC_NDX";   Nombre = "NAS100 [?]"     }
    @{ Symbol = "OTC_SPC";   Nombre = "SP500 [?]"      }
)

$TIMEFRAMES = @("1d", "4h", "1h", "5m")

if ($SoloActivos.Count    -gt 0) { $ACTIVOS    = $ACTIVOS    | Where-Object { $SoloActivos    -contains $_.Symbol } }
if ($SoloTimeframes.Count -gt 0) { $TIMEFRAMES = $TIMEFRAMES | Where-Object { $SoloTimeframes -contains $_ } }

$total = $ACTIVOS.Count * $TIMEFRAMES.Count
$idx   = 0

Write-Host ""
Write-Host "======================================================" -ForegroundColor Magenta
Write-Host "  DESCARGA MASIVA -- $total combinaciones activo x temporalidad" -ForegroundColor Magenta
Write-Host "  Periodo: $Desde --> $Hasta" -ForegroundColor Magenta
Write-Host "======================================================" -ForegroundColor Magenta

$resumen = @()

foreach ($activo in $ACTIVOS) {
    foreach ($tf in $TIMEFRAMES) {
        $idx++
        Write-Host ""
        Write-Host "[ $idx / $total ]  $($activo.Nombre)  $tf" -ForegroundColor Magenta

        & "$PSScriptRoot\_base.ps1" `
            -Symbol    $activo.Symbol `
            -Timeframe $tf `
            -Desde     $Desde `
            -Hasta     $Hasta

        $resumen += "$($activo.Nombre) $tf"
    }
}

Write-Host ""
Write-Host "======================================================" -ForegroundColor Magenta
Write-Host "  DESCARGA MASIVA COMPLETADA" -ForegroundColor Magenta
Write-Host "------------------------------------------------------" -ForegroundColor Magenta
foreach ($r in $resumen) {
    Write-Host "  OK: $r" -ForegroundColor Green
}
Write-Host "======================================================" -ForegroundColor Magenta
