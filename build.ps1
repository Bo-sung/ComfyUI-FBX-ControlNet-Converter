# Rebuild the native CLI and refresh ./bin (the bundled binary the node calls).
# Requires the .NET 8 SDK. Run from the repo root.
$ErrorActionPreference = "Stop"
dotnet publish cli/src/Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o bin
Write-Host "Done. bin/fbxcontrolnet.exe refreshed."
