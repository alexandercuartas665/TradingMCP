<# SP500 - 4h - Descarga 7 años de datos históricos (2019-04-11 → hoy) #>
& "$PSScriptRoot\_base.ps1" -Symbol "OTC_SPC" -Timeframe "4h" -Desde "2019-04-11"
# ADVERTENCIA: Verificar que el simbolo sea correcto en Deriv antes de ejecutar
