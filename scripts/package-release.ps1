[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0',

    [Parameter()]
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [Parameter()]
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [Parameter()]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repositoryRoot 'artifacts'
}
else {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}

$artifactsRoot = [System.IO.Path]::GetFullPath($artifactsRoot)
$packageName = "AdamCodexHub-v$Version-$RuntimeIdentifier"
$stagingDirectory = Join-Path $artifactsRoot $packageName
$zipPath = Join-Path $artifactsRoot "$packageName.zip"
$checksumPath = "$zipPath.sha256"

function Assert-WithinArtifacts {
    param([Parameter(Mandatory)][string]$Path)

    $candidate = [System.IO.Path]::GetFullPath($Path)
    $rootWithSeparator = $artifactsRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    if (-not $candidate.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the artifacts directory: $candidate"
    }
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

Assert-WithinArtifacts -Path $stagingDirectory
Assert-WithinArtifacts -Path $zipPath
Assert-WithinArtifacts -Path $checksumPath

if (Test-Path -LiteralPath $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}

foreach ($file in @($zipPath, $checksumPath)) {
    if (Test-Path -LiteralPath $file) {
        Remove-Item -LiteralPath $file -Force
    }
}

New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
$cliDirectory = Join-Path $stagingDirectory 'cli'

$publishProperties = @(
    "-p:Version=$Version",
    "-p:InformationalVersion=$Version",
    '-p:DebugSymbols=false',
    '-p:DebugType=None'
)

& dotnet publish `
    (Join-Path $repositoryRoot 'src\AdamCodexHub.App\AdamCodexHub.App.csproj') `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -o $stagingDirectory `
    @publishProperties

if ($LASTEXITCODE -ne 0) {
    throw "Desktop publish failed with exit code $LASTEXITCODE."
}

& dotnet publish `
    (Join-Path $repositoryRoot 'src\AdamCodexHub.Cli\AdamCodexHub.Cli.csproj') `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -o $cliDirectory `
    @publishProperties

if ($LASTEXITCODE -ne 0) {
    throw "CLI publish failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $stagingDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $stagingDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'SECURITY.md') -Destination $stagingDirectory

@(
    "Adam CodexHub $Version ($RuntimeIdentifier)",
    '',
    '1. Extract the entire ZIP archive.',
    '2. Run AdamCodexHub.App.exe.',
    '3. The CLI is available at cli\AdamCodexHub.Cli.exe.',
    '',
    'This package includes the .NET runtime and does not require a separate .NET installation.',
    'The binaries are currently unsigned, so Windows SmartScreen may display a warning.'
) | Set-Content -LiteralPath (Join-Path $stagingDirectory 'README-FIRST.txt') -Encoding utf8

$Version | Set-Content -LiteralPath (Join-Path $stagingDirectory 'VERSION') -Encoding ascii

Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash *$(Split-Path $zipPath -Leaf)" |
    Set-Content -LiteralPath $checksumPath -Encoding ascii

[pscustomobject]@{
    Package = $zipPath
    Checksum = $checksumPath
    Sha256 = $hash
}
