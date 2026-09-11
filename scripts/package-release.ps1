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

# Inno Setup remembers where it landed, and that is not always the default folder (a toolchain or
# portable install keeps it wherever the user put it -- measured 2026-09-11: winget reported 6.7.3 as
# installed while all three default paths were empty, so the installer was silently skipped and the
# release came out ZIP-only). Whatever the uninstall entry names is a real ISCC.
foreach ($key in @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1')) {
    if (Test-Path $key) {
        $installed = (Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue).InstallLocation
        if (-not [string]::IsNullOrWhiteSpace($installed)) {
            $installerCandidates += (Join-Path $installed 'ISCC.exe')
        }
    }
}

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
    Write-Warning 'Inno Setup 6 not found -- installer skipped, ZIP-only package produced.'
}

# --- Small update package -------------------------------------------------
# Only the files that change between releases: the hub's own assemblies, its assets, and the CLI.
# Measured 2026-09-11 against this publish output -- 6.6 MB of a 293.5 MB folder, the other 88% being
# the .NET runtime, which never changes between patches. The user is on a line that managed 525 KB/min,
# which makes the 91 MB installer roughly a three-hour download and this one a few minutes.
#
# The full installer is NOT replaced by this: it stays the way to install on a clean machine and the
# way to repair a broken one. An update package only assumes a working install of an earlier version.
$updateBaseName = "AdamCodexHub-update-v$Version-$RuntimeIdentifier"
$updateZipPath = Join-Path $artifactsRoot "$updateBaseName.zip"
$updateChecksumPath = "$updateZipPath.sha256"
$updateStaging = Join-Path $artifactsRoot "$updateBaseName-files"

Assert-WithinArtifacts -Path $updateZipPath
Assert-WithinArtifacts -Path $updateChecksumPath
Assert-WithinArtifacts -Path $updateStaging

foreach ($path in @($updateZipPath, $updateChecksumPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}

$updateFiles = @(Get-ChildItem -LiteralPath $stagingDirectory -Recurse -File | Where-Object {
    $relative = $_.FullName.Substring($stagingDirectory.Length).TrimStart('\')
    $relative -like 'AdamCodexHub.*' -or
    $relative -like 'cli\AdamCodexHub.*' -or
    $relative -like 'Assets\*'
})

if ($updateFiles.Count -eq 0) {
    throw "No updatable files were found in $stagingDirectory -- the update package would be empty."
}

# The manifest is the contract the applier trusts: version, target runtime, and a digest per file. It
# is written with forward slashes so the same file reads identically on any platform.
$manifestFiles = @(
    foreach ($file in $updateFiles) {
        $relative = $file.FullName.Substring($stagingDirectory.Length).TrimStart('\')
        [ordered]@{
            path   = $relative.Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            size   = $file.Length
        }
    }
)

$manifest = [ordered]@{
    version           = $Version
    runtimeIdentifier = $RuntimeIdentifier
    generated         = [DateTimeOffset]::UtcNow.ToString('O')
    files             = $manifestFiles
}

if (Test-Path -LiteralPath $updateStaging) {
    Remove-Item -LiteralPath $updateStaging -Recurse -Force
}

New-Item -ItemType Directory -Path $updateStaging -Force | Out-Null
foreach ($file in $updateFiles) {
    $relative = $file.FullName.Substring($stagingDirectory.Length).TrimStart('\')
    $destination = Join-Path $updateStaging $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}

# Written into the update staging only, after the full ZIP was built above, so the portable package is
# unaffected. WriteAllText with an explicit BOM-less UTF8Encoding on purpose: Set-Content -Encoding
# utf8 writes a byte-order mark on PowerShell 5.1, and a leading BOM is exactly the kind of
# byte-order detail that makes a perfectly good manifest fail to parse at the other end.
$manifestJson = $manifest | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText(
    (Join-Path $updateStaging 'update-manifest.json'),
    $manifestJson,
    (New-Object System.Text.UTF8Encoding($false)))

Compress-Archive -Path (Join-Path $updateStaging '*') -DestinationPath $updateZipPath -CompressionLevel Optimal

$updateHash = (Get-FileHash -LiteralPath $updateZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$updateHash *$(Split-Path $updateZipPath -Leaf)" |
    Set-Content -LiteralPath $updateChecksumPath -Encoding ascii

$updateSizeMb = [math]::Round((Get-Item -LiteralPath $updateZipPath).Length / 1MB, 1)
Write-Host "Update package: $updateBaseName.zip ($updateSizeMb MB, $($updateFiles.Count) file(s))"

[pscustomobject]@{
    Package  = $zipPath
    Checksum = $checksumPath
    Sha256   = $hash
    Installer = if ($iscc) { $setupPath } else { $null }
    InstallerChecksum = if ($iscc) { $setupChecksumPath } else { $null }
    UpdatePackage = $updateZipPath
    UpdateChecksum = $updateChecksumPath
    UpdateSha256 = $updateHash
    UpdateBytes = (Get-Item -LiteralPath $updateZipPath).Length
}
