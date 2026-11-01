# Builds EveryCopy-Setup-<version>.exe: publishes a self-contained release build, then compiles the Inno Setup script.
# Usage:  powershell -ExecutionPolicy Bypass -File build-installer.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

# Optional code signing: set EVERYCOPY_PFX (path to a .pfx) and EVERYCOPY_PFX_PASSWORD to sign the app and the installer.
$signtool = $null
if ($env:EVERYCOPY_PFX) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw 'EVERYCOPY_PFX is set but signtool.exe was not found. Install the Windows SDK signing tools.' }
}
function Sign-File($path) {
    if (-not $signtool) { return }
    & $signtool sign /f $env:EVERYCOPY_PFX /p $env:EVERYCOPY_PFX_PASSWORD /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $path" }
}

Get-Process EveryCopy -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item publish -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish EveryCopy.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Sign-File publish\EveryCopy.exe

& $iscc installer\EveryCopy.iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compile failed' }

Get-ChildItem installer\Output\*.exe | ForEach-Object { Sign-File $_.FullName }
Get-ChildItem installer\Output\*.exe | ForEach-Object { "{0}  ({1:N0} MB)" -f $_.FullName, ($_.Length / 1MB) }
