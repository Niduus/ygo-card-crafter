# Builds YOUR OWN copy of YGOCardCrafter.exe with your card textures baked in, so it shows them without
# loading anything. For your own use: the textures are game artwork, so don't upload this exe publicly.
#
#   powershell -ExecutionPolicy Bypass -File tools\build-my-exe.ps1 C:\path\to\ygo_want.zip
#
# Needs the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
param([Parameter(Mandatory = $true)][string]$TexturesZip)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path $TexturesZip)) { throw "Can't find $TexturesZip" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET 8 SDK isn't installed. Get it from https://dotnet.microsoft.com/download/dotnet/8.0 and run this again."
}

New-Item -ItemType Directory -Force -Path (Join-Path $root 'private') | Out-Null
Copy-Item $TexturesZip (Join-Path $root 'private\CardTextures.zip') -Force

$out = Join-Path $root 'publish-private'
dotnet publish (Join-Path $root 'src\CardCrafter.App') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
if ($LASTEXITCODE -ne 0) { throw "The build failed." }
Write-Host ""
Write-Host "Done: $out\YGOCardCrafter.exe has your textures built in." -ForegroundColor Green
