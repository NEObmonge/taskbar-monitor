# Compila TaskBar Monitor con el compilador de C# que trae Windows (.NET Framework 4.x). No necesita Visual Studio.
# Uso:  .\build.ps1            → dist\TaskbarMonitor.exe
#       .\build.ps1 -Install   → además lo copia a %LOCALAPPDATA%\Programs\TaskbarMonitor y lo abre
param([switch]$Install)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'No encontré csc.exe de .NET Framework 4.x' }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$exe = Join-Path $dist 'TaskbarMonitor.exe'
& $csc /nologo /target:winexe /codepage:65001 /optimize "/win32icon:$(Join-Path $root 'app.ico')" "/out:$exe" `
    /r:System.Management.dll /r:System.Web.Extensions.dll /r:System.Security.dll (Join-Path $root 'TaskbarMonitor.cs')
if ($LASTEXITCODE -ne 0) { throw 'La compilación falló' }
Write-Host "Compilado: $exe"

if ($Install) {
    $dest = Join-Path $env:LOCALAPPDATA 'Programs\TaskbarMonitor'
    New-Item -ItemType Directory -Force $dest | Out-Null
    Get-Process TaskbarMonitor -ErrorAction SilentlyContinue | Stop-Process
    Start-Sleep -Milliseconds 500
    Copy-Item $exe $dest -Force
    $oauth = Join-Path $root 'google-oauth.json'
    if (Test-Path $oauth) { Copy-Item $oauth $dest -Force }
    Start-Process (Join-Path $dest 'TaskbarMonitor.exe')
    Write-Host "Instalado en $dest"
}
