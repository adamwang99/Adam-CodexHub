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

foreach ($file in @(
    'PRIVACY.md',
    'DISCLAIMER.md',
    'TRADEMARKS.md',
    'THIRD-PARTY-NOTICES.md'
)) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $file) -Destination $stagingDirectory
}

$packageDocsDirectory = Join-Path $stagingDirectory 'docs'
$packageLicensesDirectory = Join-Path $stagingDirectory 'licenses'
New-Item -ItemType Directory -Path $packageDocsDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $packageLicensesDirectory -Force | Out-Null
Copy-Item `
    -LiteralPath (Join-Path $repositoryRoot 'docs\PROVIDER-DATA-DISCLOSURES.md') `
    -Destination $packageDocsDirectory
Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'licenses') -File |
    Copy-Item -Destination $packageLicensesDirectory

$dotnetCommand = Get-Command dotnet -ErrorAction Stop
$dotnetNoticesPath = Join-Path (Split-Path -Parent $dotnetCommand.Source) 'ThirdPartyNotices.txt'
if (-not (Test-Path -LiteralPath $dotnetNoticesPath)) {
    throw "The .NET third-party notices file was not found beside $($dotnetCommand.Source)."
}

Copy-Item `
    -LiteralPath $dotnetNoticesPath `
    -Destination (Join-Path $stagingDirectory 'DOTNET-THIRD-PARTY-NOTICES.txt')

$dependencyInventory = [System.Collections.Generic.List[string]]::new()
foreach ($project in @(
    'src\AdamCodexHub.App\AdamCodexHub.App.csproj',
    'src\AdamCodexHub.Cli\AdamCodexHub.Cli.csproj'
)) {
    $dependencyInventory.Add("## $project")
    $packageList = & dotnet list (Join-Path $repositoryRoot $project) package --include-transitive 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Dependency inventory failed for $project with exit code $LASTEXITCODE."
    }

    foreach ($line in $packageList) {
        $dependencyInventory.Add($line.ToString())
    }
    $dependencyInventory.Add('')
}

$dependencyInventory | Set-Content `
    -LiteralPath (Join-Path $stagingDirectory 'THIRD-PARTY-PACKAGES.txt') `
    -Encoding utf8

$sbomComponents = [System.Collections.Generic.List[object]]::new()
$sbomReferences = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)

foreach ($depsPath in @(
    (Join-Path $stagingDirectory 'AdamCodexHub.App.deps.json'),
    (Join-Path $cliDirectory 'AdamCodexHub.Cli.deps.json')
)) {
    $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
    foreach ($library in $deps.libraries.PSObject.Properties) {
        if ($library.Value.type -notin @('package', 'runtimepack')) {
            continue
        }

        $name, $packageVersion = $library.Name -split '/', 2
        if ($library.Value.type -eq 'runtimepack') {
            $name = $name -replace '^runtimepack\.', ''
        }

        $packageUrl = "pkg:nuget/$([Uri]::EscapeDataString($name))@$packageVersion"
        if (-not $sbomReferences.Add($packageUrl)) {
            continue
        }

        $sbomComponents.Add([ordered]@{
            type = 'library'
            'bom-ref' = $packageUrl
            name = $name
            version = $packageVersion
            purl = $packageUrl
            scope = 'required'
        })
    }
}

$sbom = [ordered]@{
    bomFormat = 'CycloneDX'
    specVersion = '1.5'
    serialNumber = "urn:uuid:$([Guid]::NewGuid())"
    version = 1
    metadata = [ordered]@{
        timestamp = [DateTimeOffset]::UtcNow.ToString('O')
        component = [ordered]@{
            type = 'application'
            name = 'Adam CodexHub'
            version = $Version
        }
    }
    components = @($sbomComponents | Sort-Object name, version)
}

$sbom | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath (Join-Path $stagingDirectory 'SBOM.cdx.json') -Encoding utf8

@(
    "Adam CodexHub $Version ($RuntimeIdentifier)",
    '',
    '1. Extract the entire ZIP archive.',
    '2. Run AdamCodexHub.App.exe.',
    '3. The CLI is available at cli\AdamCodexHub.Cli.exe.',
    '4. Read PRIVACY.md, DISCLAIMER.md and docs\PROVIDER-DATA-DISCLOSURES.md before using a remote provider.',
    '5. SBOM.cdx.json and THIRD-PARTY-PACKAGES.txt describe bundled dependencies.',
    '',
    'This package includes the .NET runtime and does not require a separate .NET installation.',
    'The binaries are currently unsigned, so Windows SmartScreen may display a warning.',
    'Compatibility probes, retries and failover make real provider requests and may incur charges.'
) | Set-Content -LiteralPath (Join-Path $stagingDirectory 'README-FIRST.txt') -Encoding utf8

$Version | Set-Content -LiteralPath (Join-Path $stagingDirectory 'VERSION') -Encoding ascii

Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash *$(Split-Path $zipPath -Leaf)" |
    Set-Content -LiteralPath $checksumPath -Encoding ascii

# --- Windows installer (Inno Setup 6) -------------------------------------
# Present on GitHub Actions windows-latest runners; optional when running locally.
$installerCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $installerCandidates |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_) } |
    Select-Object -First 1

$setupBaseName = "AdamCodexHub-Setup-v$Version-$RuntimeIdentifier"
$setupPath = Join-Path $artifactsRoot "$setupBaseName.exe"
$setupChecksumPath = "$setupPath.sha256"

if ($iscc) {
    & $iscc `
        (Join-Path $repositoryRoot 'installer\adam-codexhub.iss') `
        "/DMyAppVersion=$Version" `
        "/DStagingDir=$stagingDirectory" `
        "/DOutputDir=$artifactsRoot" `
        "/DRepoRoot=$repositoryRoot" `
        "/DOutputBaseName=$setupBaseName"

    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compile failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $setupPath)) {
        throw "Inno Setup reported success but $setupPath was not created."
    }

    $setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$setupHash *$(Split-Path $setupPath -Leaf)" |
        Set-Content -LiteralPath $setupChecksumPath -Encoding ascii
}
else {
    Write-Warning 'Inno Setup 6 not found — installer skipped, ZIP-only package produced.'
}

[pscustomobject]@{
    Package  = $zipPath
    Checksum = $checksumPath
    Sha256   = $hash
    Installer = if ($iscc) { $setupPath } else { $null }
    InstallerChecksum = if ($iscc) { $setupChecksumPath } else { $null }
}
