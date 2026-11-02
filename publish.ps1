# Builds the portable RELAY distribution (Windows x64, self-contained).
# Run from the repository root in PowerShell:   ./publish.ps1
# Requires the .NET 10 SDK. No administrator rights are needed.

$ErrorActionPreference = "Stop"
$out = Join-Path $PSScriptRoot "artifacts\publish\RELAY"
$zip = Join-Path $PSScriptRoot "artifacts\RELAY-win-x64-portable.zip"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet test (Join-Path $PSScriptRoot "tests\Relay.Tests") -c Release
dotnet publish (Join-Path $PSScriptRoot "src\Relay.App\Relay.App.csproj") `
    -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $out

# Trim developer-only files and mark the folder as portable (data is kept next to Relay.exe).
Get-ChildItem $out -Include *.pdb, *.xml -Recurse | Remove-Item -Force
New-Item -ItemType File -Path (Join-Path $out "relay.portable") -Force | Out-Null

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip
Write-Host "Portable build: $zip"
