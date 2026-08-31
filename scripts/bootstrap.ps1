$ErrorActionPreference = "Stop"

Write-Host "Adam CodexHub - restore/build/test" -ForegroundColor Yellow

dotnet --info
dotnet restore "$PSScriptRoot\..\AdamCodexHub.sln"
dotnet build "$PSScriptRoot\..\AdamCodexHub.sln" -c Debug --no-restore
dotnet test "$PSScriptRoot\..\AdamCodexHub.sln" -c Debug --no-build

Write-Host "Baseline build completed." -ForegroundColor Green
