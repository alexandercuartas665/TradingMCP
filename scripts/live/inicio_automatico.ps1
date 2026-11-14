# Arranque automatico de la app en modo autonomo al iniciar sesion en Windows.
# Crea (o quita) un acceso directo en la carpeta Inicio del usuario: no necesita
# permisos de administrador y no toca configuracion del sistema.
#
#   pwsh -File scripts\live\inicio_automatico.ps1 -Accion instalar
#   pwsh -File scripts\live\inicio_automatico.ps1 -Accion estado
#   pwsh -File scripts\live\inicio_automatico.ps1 -Accion quitar
#
# Requisitos para que funcione tras un reinicio:
#   - Docker Desktop debe arrancar al iniciar sesion (Settings > General >
#     "Start Docker Desktop when you sign in"). El contenedor de PostgreSQL tiene
#     restart: unless-stopped y arranca con Docker.
#   - La app usa la compilacion de bin\Debug\net48: para recompilar, cierra antes la app.
param([ValidateSet('instalar','quitar','estado')][string]$Accion = 'estado')

$exe = (Resolve-Path (Join-Path $PSScriptRoot '..\..\WpfDerivClientVB\bin\Debug\net48\WpfDerivClientVB.exe') -ErrorAction SilentlyContinue)
$inicio = [Environment]::GetFolderPath('Startup')
$acceso = Join-Path $inicio 'Traiding modo autonomo.lnk'
$argumentos = '--heavy-api --minimized --autonomo'

switch ($Accion) {
  'instalar' {
    if (-not $exe) { throw 'No se encuentra WpfDerivClientVB.exe en bin\Debug\net48. Compila la app primero.' }
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($acceso)
    $lnk.TargetPath = $exe.Path
    $lnk.Arguments = $argumentos
    $lnk.WorkingDirectory = Split-Path $exe.Path
    $lnk.WindowStyle = 7   # minimizada
    $lnk.Description = 'Traiding: modo autonomo (forward test, vigilancia y mantenimiento diario)'
    $lnk.Save()
    Write-Output "Instalado: $acceso"
    Write-Output "Arranca: $($exe.Path) $argumentos"
  }
  'quitar' {
    if (Test-Path $acceso) { Remove-Item $acceso -Confirm:$false; Write-Output "Quitado: $acceso" }
    else { Write-Output 'No estaba instalado.' }
  }
  'estado' {
    if (Test-Path $acceso) {
      $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut($acceso)
      Write-Output "Instalado: $acceso"
      Write-Output "Arranca: $($lnk.TargetPath) $($lnk.Arguments)"
    } else {
      Write-Output 'No instalado. Usa -Accion instalar.'
    }
  }
}
