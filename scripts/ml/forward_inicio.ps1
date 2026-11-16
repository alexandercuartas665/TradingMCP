# Forward test del modelo LightGBM (docs/validacion/forward_ml_plan.md): arranque y estado.
#
#   pwsh -File scripts\ml\forward_inicio.ps1 -Accion arrancar   # lanza el bucle oculto (si no corre)
#   pwsh -File scripts\ml\forward_inicio.ps1 -Accion instalar   # acceso directo en Inicio + arrancar
#   pwsh -File scripts\ml\forward_inicio.ps1 -Accion estado     # proceso + informe
#   pwsh -File scripts\ml\forward_inicio.ps1 -Accion detener
#   pwsh -File scripts\ml\forward_inicio.ps1 -Accion quitar     # quita el acceso directo
#
# Modo papel (no compra). Para compras DEMO: -ComprarDemo (lo decide el usuario).
param(
  [ValidateSet('arrancar', 'instalar', 'estado', 'detener', 'quitar')][string]$Accion = 'estado',
  [switch]$ComprarDemo
)
$py = 'D:\traiding_ml\venv\Scripts\pythonw.exe'
$script = (Resolve-Path (Join-Path $PSScriptRoot 'forward_lightgbm.py')).Path
$raiz = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$argumentos = '-I "{0}"{1}' -f $script, $(if ($ComprarDemo) { ' --comprar-demo' } else { '' })
$acceso = Join-Path ([Environment]::GetFolderPath('Startup')) 'Traiding forward ML.lnk'

function Procesos { Get-CimInstance Win32_Process -Filter "Name like 'python%'" | Where-Object { $_.CommandLine -like '*forward_lightgbm.py*' -and $_.CommandLine -notlike '*--resumen*' -and $_.CommandLine -notlike '*--una-vez*' } }

switch ($Accion) {
  'arrancar' {
    if (Procesos) { Write-Output 'El forward test ya esta corriendo.'; break }
    Start-Process -FilePath $py -ArgumentList $argumentos -WorkingDirectory $raiz -WindowStyle Hidden
    Start-Sleep -Seconds 3
    if (Procesos) { Write-Output ('Forward test lanzado ({0}).' -f $(if ($ComprarDemo) { 'COMPRA DEMO' } else { 'papel' })) } else { throw 'No arranco: revisa D:\traiding_ml\forward\forward.log' }
  }
  'instalar' {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($acceso)
    $lnk.TargetPath = $py
    $lnk.Arguments = $argumentos
    $lnk.WorkingDirectory = $raiz
    $lnk.WindowStyle = 7
    $lnk.Description = 'Traiding: forward test del modelo LightGBM (papel)'
    $lnk.Save()
    Write-Output ('Acceso directo creado: {0}' -f $acceso)
    & $PSCommandPath -Accion arrancar -ComprarDemo:$ComprarDemo
  }
  'estado' {
    $p = @(Procesos)
    Write-Output ('Proceso: {0}' -f $(if ($p.Count) { ($p | ForEach-Object { 'PID {0} {1}' -f $_.ProcessId, $(if ($_.CommandLine -like '*--comprar-demo*') { 'COMPRA DEMO' } else { 'papel' }) }) -join ', ' } else { 'NO corre' }))
    Write-Output ('Inicio automatico: {0}' -f $(if (Test-Path $acceso) { 'instalado' } else { 'no' }))
    & 'D:\traiding_ml\venv\Scripts\python.exe' -I $script --resumen
  }
  'detener' {
    $p = @(Procesos)
    foreach ($x in $p) { Stop-Process -Id $x.ProcessId -Force }
    Write-Output ('Detenidos: {0}' -f $p.Count)
  }
  'quitar' {
    if (Test-Path $acceso) { Remove-Item $acceso; Write-Output 'Acceso directo quitado.' } else { Write-Output 'No habia acceso directo.' }
  }
}
