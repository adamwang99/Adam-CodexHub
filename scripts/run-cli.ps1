param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

$ErrorActionPreference = "Stop"

dotnet run --project "$PSScriptRoot\..\src\AdamCodexHub.Cli\AdamCodexHub.Cli.csproj" -- @Arguments
