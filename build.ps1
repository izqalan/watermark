# Builds dist\PhotoWatermarker.exe (a single self-contained file, about 27 MB).
# Needs the .NET SDK (5.0 or newer). The exe runs on Windows 10/11 x64 with nothing else installed.
#
#   .\build.ps1                   # version 1.0.0
#   .\build.ps1 -Version 1.2.0    # stamp a specific version into the exe (used by the release workflow)
param(
    [string]$Version = "1.0.0"
)
$ErrorActionPreference = 'Stop'

dotnet build "$PSScriptRoot\src\PhotoWatermarker.csproj" -c Release "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed."
    exit $LASTEXITCODE
}

New-Item -ItemType Directory -Force "$PSScriptRoot\dist" | Out-Null
try {
    Copy-Item "$PSScriptRoot\src\bin\Release\net48\PhotoWatermarker.exe" "$PSScriptRoot\dist\" -Force
} catch [System.IO.IOException] {
    Write-Error "dist\PhotoWatermarker.exe is in use. Close Photo Watermarker and run build.ps1 again."
    exit 1
}
Write-Host "Built $PSScriptRoot\dist\PhotoWatermarker.exe (version $Version)"
