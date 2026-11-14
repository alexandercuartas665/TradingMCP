# publicar-maqueta.ps1
# Sirve la maqueta HTML en http://localhost:8090 y la expone por un
# Cloudflare Quick Tunnel (URL https://xxxx.trycloudflare.com).
# Uso:  powershell -ExecutionPolicy Bypass -File .\publicar-maqueta.ps1
# Ctrl+C para detener el tunel; el servidor web se cierra al salir.

param([int]$Port = 8090)

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Write-Host "[maqueta] Sirviendo $dir en http://localhost:$Port"

$web = Start-Process python -ArgumentList "-m", "http.server", "$Port", "--bind", "127.0.0.1", "--directory", "`"$dir`"" `
       -WindowStyle Hidden -PassThru

try {
    Start-Sleep -Seconds 1
    Write-Host "[maqueta] Abriendo tunel Cloudflare... busca la linea https://*.trycloudflare.com"
    cloudflared tunnel --url "http://localhost:$Port" --no-autoupdate
}
finally {
    if ($web -and -not $web.HasExited) { Stop-Process -Id $web.Id -Force }
    Write-Host "[maqueta] Servidor detenido."
}
