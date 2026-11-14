# agent_auth.ps1
# Agrega el header X-Agent-Token a las llamadas a las APIs locales de la app
# (http://localhost:8765 y :8766). NO se envia a ningun otro host (Deriv, Yahoo...).
#
# Uso: en cada script, despues del bloque param(...):
#   . (Join-Path $PSScriptRoot '..\agent_auth.ps1')      # scripts en scripts\<carpeta>\
#
# Funciona definiendo Invoke-RestMethod / Invoke-WebRequest como funciones que
# envuelven a los cmdlets originales: si la URL es local agregan el header y
# luego llaman al cmdlet con los mismos argumentos.
#
# El token lo crea la app al arrancar en:
#   %LOCALAPPDATA%\WpfDerivClientVB\agent_token.txt

$global:AgentTokenPath = Join-Path $env:LOCALAPPDATA 'WpfDerivClientVB\agent_token.txt'

function Get-AgentApiToken {
    if (Test-Path $global:AgentTokenPath) { return (Get-Content $global:AgentTokenPath -Raw).Trim() }
    Write-Warning "agent_auth: no existe $global:AgentTokenPath. Abre la app una vez para que lo cree."
    return ''
}

function Test-AgentLocalUri {
    param([object[]] $Argumentos)
    foreach ($a in $Argumentos) {
        if (($a -is [string] -or $a -is [uri]) -and ([string]$a) -match '^https?://(localhost|127\.0\.0\.1):876[56](/|$)') { return $true }
    }
    return $false
}

# Se conserva @args intacto (asi PowerShell sigue reconociendo -Uri, -Method...).
# Si la URL es local, un $PSDefaultParameterValues LOCAL a esta funcion agrega
# -Headers solo cuando el llamador no paso los suyos.
function Invoke-RestMethod {
    if (Test-AgentLocalUri $args) {
        $PSDefaultParameterValues = @{ 'Invoke-RestMethod:Headers' = @{ 'X-Agent-Token' = (Get-AgentApiToken) } }
    }
    Microsoft.PowerShell.Utility\Invoke-RestMethod @args
}

function Invoke-WebRequest {
    if (Test-AgentLocalUri $args) {
        $PSDefaultParameterValues = @{ 'Invoke-WebRequest:Headers' = @{ 'X-Agent-Token' = (Get-AgentApiToken) } }
    }
    Microsoft.PowerShell.Utility\Invoke-WebRequest @args
}
