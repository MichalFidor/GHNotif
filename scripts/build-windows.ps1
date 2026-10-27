param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64"
)
$ErrorActionPreference = "Stop"
Push-Location (Join-Path $PSScriptRoot "..")
try {
    $output = "build/GHNotif-$Runtime"
    dotnet publish windows/GHNotif.Windows/GHNotif.Windows.csproj -c Release -r $Runtime `
        --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $output
    if ($LASTEXITCODE -ne 0) { throw "Windows publish failed." }
    Compress-Archive -Path "$output/*" -DestinationPath "build/GHNotif-$Runtime.zip" -Force
    Write-Host "Gotowe: build/GHNotif-$Runtime.zip"
} finally {
    Pop-Location
}
