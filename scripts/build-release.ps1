[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Version,
    [string]$OutputRoot,
    [string]$DotNetPath = 'dotnet',
    [switch]$Preview,
    [string]$SignToolPath,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$semVer = '\A(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z'
foreach ($validFixture in @('0.1.0', '0.1.0-review.1+audit.1')) {
    if ($validFixture -notmatch $semVer) { throw 'Internal SemVer validator rejected a valid fixture.' }
}
foreach ($invalidFixture in @("0.1.0`n", "0.1.0`r", '0.1.0 ', '01.0.0')) {
    if ($invalidFixture -match $semVer) { throw 'Internal SemVer validator accepted an invalid fixture.' }
}
if ($Version -notmatch $semVer) { throw 'Version must be a valid SemVer 2.0 value without a v prefix.' }

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Get-RelativeChildPath([string]$Root, [string]$Path) {
    $normalizedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('\', '/'))
    $normalizedPath = [System.IO.Path]::GetFullPath($Path)
    $rootPrefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $normalizedPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Path is outside the expected root.'
    }
    return $normalizedPath.Substring($rootPrefix.Length)
}

function Get-SourceState {
    $head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -notmatch '\A[0-9a-f]{40}\z') { throw 'Unable to resolve release commit SHA.' }
    $changes = @(& git -C $repositoryRoot status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect Git working tree.' }
    return [pscustomobject]@{ Commit = $head; Changes = @($changes) }
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repositoryRoot 'dist' }
$outputDirectory = [System.IO.Path]::GetFullPath($OutputRoot)
if ($outputDirectory -eq $repositoryRoot -or $repositoryRoot.StartsWith($outputDirectory + [System.IO.Path]::DirectorySeparatorChar)) {
    throw "Refusing unsafe output root: $outputDirectory"
}

$sdkVersion = (& $DotNetPath --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersion -ne '8.0.424') {
    throw "Release requires .NET SDK 8.0.424; found '$sdkVersion'."
}

$sourceState = Get-SourceState
$dirty = @($sourceState.Changes)
if (-not $Preview -and $dirty.Count -gt 0) {
    throw 'Formal release packaging requires a clean Git working tree. Use -Preview for local validation only.'
}
if ($Preview) {
    Write-Warning 'PREVIEW build: dirty working tree is allowed; artifacts are not release-ready.'
}

$commitSha = $sourceState.Commit
$fileVersion = (($Version -split '[-+]', 2)[0]) + '.0'
$packageRootName = "CodexUsageMonitor-$Version-win-x64"
$workDirectory = Join-Path $outputDirectory ".work-$Version"
$publishDirectory = Join-Path $workDirectory 'publish'
$packageDirectory = Join-Path $workDirectory $packageRootName
$zipName = "$packageRootName.zip"
$zipPath = Join-Path $outputDirectory $zipName
$hashPath = Join-Path $outputDirectory 'SHA256SUMS.txt'

foreach ($target in @($workDirectory, $zipPath, $hashPath)) {
    $fullTarget = [System.IO.Path]::GetFullPath($target)
    if (-not $fullTarget.StartsWith($outputDirectory + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside output root: $fullTarget"
    }
    if (Test-Path -LiteralPath $fullTarget) { Remove-Item -LiteralPath $fullTarget -Recurse -Force }
}
[System.IO.Directory]::CreateDirectory($publishDirectory) | Out-Null
[System.IO.Directory]::CreateDirectory($packageDirectory) | Out-Null

$project = Join-Path $repositoryRoot 'src\CodexUsageMonitor\CodexUsageMonitor.csproj'

& $DotNetPath restore $project -r win-x64 --locked-mode
if ($LASTEXITCODE -ne 0) { throw "locked restore failed: $LASTEXITCODE" }

& $DotNetPath publish $project -c Release -r win-x64 --self-contained true --no-restore `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Deterministic=true `
    -p:ContinuousIntegrationBuild=true `
    -p:RuntimeFrameworkVersion=8.0.30 `
    -p:TargetLatestRuntimePatch=false `
    -p:Version=$Version `
    -p:FileVersion=$fileVersion `
    -p:InformationalVersion=$Version `
    "-p:PathMap=$repositoryRoot=/_/" `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "publish failed: $LASTEXITCODE" }

$assetsPath = Join-Path $repositoryRoot 'src\CodexUsageMonitor\obj\project.assets.json'
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$downloads = @($assets.project.frameworks.PSObject.Properties.Value.downloadDependencies)
foreach ($requiredRuntime in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
    $entry = $downloads | Where-Object { $_.name -eq $requiredRuntime }
    if ($null -eq $entry -or [string]$entry.version -ne '[8.0.30, 8.0.30]') {
        throw "$requiredRuntime is not locked to runtime 8.0.30 in final restore assets."
    }
}

$executable = Join-Path $publishDirectory 'CodexUsageMonitor.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Publish did not produce CodexUsageMonitor.exe.' }
if (Get-ChildItem -LiteralPath $publishDirectory -Filter '*.pdb' -File -Recurse) { throw 'Publish output contains PDB files.' }
$publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse)
if ($publishedFiles.Count -lt 2 -or -not ($publishedFiles | Where-Object Extension -eq '.dll')) {
    throw 'Publish output is not the required multi-file portable layout.'
}

$versionInfo = (Get-Item -LiteralPath $executable).VersionInfo
if ($versionInfo.FileVersion -ne $fileVersion -or $versionInfo.ProductVersion -ne $Version) {
    throw "Executable version mismatch: FileVersion=$($versionInfo.FileVersion), ProductVersion=$($versionInfo.ProductVersion)"
}

if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    if ([string]::IsNullOrWhiteSpace($SignToolPath) -or -not [System.IO.Path]::IsPathFullyQualified($SignToolPath)) {
        throw 'Signing requires an absolute -SignToolPath.'
    }
    & $SignToolPath sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $executable
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed: $LASTEXITCODE" }
}

$signature = Get-AuthenticodeSignature -LiteralPath $executable
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint) -and $signature.Status -ne 'Valid') {
    throw "Signed executable verification failed: $($signature.Status)"
}
if ($signature.Status -eq 'NotSigned') {
    Write-Warning 'Authenticode status: NotSigned. This is a Preview/SmartScreen-risk artifact until signed.'
} else {
    Write-Host "Authenticode status: $($signature.Status)"
}

foreach ($file in Get-ChildItem -LiteralPath $publishDirectory) {
    Copy-Item -LiteralPath $file.FullName -Destination $packageDirectory -Recurse
}
foreach ($document in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $document) -Destination $packageDirectory
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'licenses') -Destination $packageDirectory -Recurse

$sbomPath = Join-Path $packageDirectory 'sbom.cdx.json'
& (Join-Path $PSScriptRoot 'New-Sbom.ps1') -Version $Version -OutputPath $sbomPath

$finalSourceState = Get-SourceState
if ($finalSourceState.Commit -ne $commitSha) {
    throw 'Git HEAD changed while the release candidate was being built.'
}
$dirty = @($finalSourceState.Changes)
if (-not $Preview -and $dirty.Count -gt 0) {
    throw 'Git working tree changed while the formal release candidate was being built.'
}

$metadata = [ordered]@{
    version = $Version
    fileVersion = $fileVersion
    commit = $commitSha
    sourceDirty = [bool]($dirty.Count -gt 0)
    sdk = $sdkVersion
    runtime = '8.0.30'
    rid = 'win-x64'
    layout = 'multi-file'
    publishFileCount = $publishedFiles.Count
    sqlitePclRaw = '2.1.13'
    authenticode = [string]$signature.Status
    preview = [bool]$Preview
}
[System.IO.File]::WriteAllText(
    (Join-Path $packageDirectory 'RELEASE-METADATA.json'),
    ($metadata | ConvertTo-Json),
    [System.Text.UTF8Encoding]::new($false))

$allowedSecretFixture = 'sk-' + 'proj-' + 'abcdefghijklmnopqrstuvwxyz' + '0123456789'
$credentialPatterns = @(
    'sk-(?:(?:proj|svcacct)-)?[A-Za-z0-9_-]{32,200}(?![A-Za-z0-9_-])',
    'gh[pousr]_[A-Za-z0-9]{36,255}(?![A-Za-z0-9])',
    'github_pat_[A-Za-z0-9_]{50,255}(?![A-Za-z0-9_])',
    '(?:AKIA|ASIA)[0-9A-Z]{16}',
    'AIza[0-9A-Za-z_-]{35}',
    '(?i:"(?:access_token|refresh_token|token|password|client_secret|api_key)"\s*:\s*"[^"\r\n]{8,}")',
    '(?i:Authorization\s*:\s*Bearer\s+[A-Za-z0-9._~-]{10,4096}(?![A-Za-z0-9._~-]))',
    '(?i:(?:^|[;\s])(?:session|sessionid|__Secure-next-auth\.session-token)=[^;\s]{10,4096})',
    '(?i:(?:Server|Data Source)\s*=[^;\r\n\0]{1,512};\s*(?:User Id|UID)\s*=[^;\r\n\0]{1,256};\s*(?:Password|PWD)\s*=[^;\r\n\0]{1,512}(?:;|$))',
    '-----BEGIN [A-Z ]*PRIVATE KEY-----',
    'https://(?:chatgpt\.com|chat\.openai\.com)/backend-api')

function Test-ContainsPrivateBuildData([string]$text) {
    $text = $text.Replace($allowedSecretFixture, '')
    foreach ($pattern in $credentialPatterns) {
        if ($text -cmatch $pattern) { return $true }
    }
    return $false
}

if (Test-ContainsPrivateBuildData $allowedSecretFixture) {
    throw 'Secret scanner rejected its fixed non-secret test fixture.'
}
foreach ($secretFixture in @(
    ('sk-' + ('A' * 40)),
    ('github_pat_' + ('A' * 60)),
    ('AKIA' + ('A' * 16)),
    ('AIza' + ('A' * 35)),
    ('Authorization: Bearer ' + ('A' * 20)),
    ('{"password":"' + ('A' * 12) + '"}'),
    ('Server=example;User Id=user;Password=' + ('A' * 12) + ';'))) {
    if (-not (Test-ContainsPrivateBuildData $secretFixture)) {
        throw 'Secret scanner failed an internal detection fixture.'
    }
}

function Assert-NoPrivateBuildData([string]$root) {
    $files = @(Get-ChildItem -LiteralPath $root -File -Recurse)
    $privateBuildPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in @(
        'C:\Users\',
        $repositoryRoot,
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData),
        [Environment]::GetEnvironmentVariable('NUGET_PACKAGES'))) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            [void]$privateBuildPaths.Add($candidate)
        }
    }

    foreach ($file in $files) {
        $relative = Get-RelativeChildPath $root $file.FullName
        if ($file.Name -match '(?i)\A(?:\.env(?:\..*)?|auth\.json|credentials\.json|cookie\.json|session\.json|secrets\.json|id_rsa(?:\..*)?)\z' -or
            $file.Name -match '(?i)\.(?:pdb|jsonl|pem|key|pfx|p12|snk|db(?:-(?:wal|shm))?|sqlite|sqlite3|log)\z') {
            throw "Forbidden file in package: $relative"
        }

        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        $representations = @(
            [System.Text.Encoding]::ASCII.GetString($bytes),
            [System.Text.Encoding]::UTF8.GetString($bytes),
            [System.Text.Encoding]::Unicode.GetString($bytes),
            [System.Text.Encoding]::BigEndianUnicode.GetString($bytes))
        foreach ($text in $representations) {
            foreach ($literal in $privateBuildPaths) {
                if ($text.IndexOf($literal, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    throw "Private build path detected in package file: $relative"
                }
            }
            if (Test-ContainsPrivateBuildData $text) {
                throw "Credential or private endpoint data detected in package file: $relative"
            }
        }
    }
    Write-Host "Privacy scan: $($files.Count) packaged files checked as ASCII/UTF-8/UTF-16/binary strings."
}

Assert-NoPrivateBuildData $packageDirectory

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $fixedTimestamp = [System.DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
    foreach ($file in Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | Sort-Object FullName) {
        $relative = (Get-RelativeChildPath $packageDirectory $file.FullName).Replace('\', '/')
        $entry = $archive.CreateEntry("$packageRootName/$relative", [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $fixedTimestamp
        $input = $file.OpenRead()
        $output = $entry.Open()
        try { $input.CopyTo($output) }
        finally { $output.Dispose(); $input.Dispose() }
    }
}
finally { $archive.Dispose() }

$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllLines($hashPath, @("$zipHash  $zipName"), [System.Text.UTF8Encoding]::new($false))

& (Join-Path $PSScriptRoot 'Test-ReleasePackage.ps1') `
    -ZipPath $zipPath `
    -PackageDirectory $packageDirectory `
    -PublishedDirectory $publishDirectory `
    -PackageRootName $packageRootName `
    -Version $Version `
    -RuntimeVersion '8.0.30' `
    -Rid 'win-x64' `
    -CommitSha $commitSha `
    -SdkVersion $sdkVersion `
    -SqlitePclRawVersion '2.1.13' `
    -ExpectedSourceDirty ([bool]($dirty.Count -gt 0)) `
    -IsPreview ([bool]$Preview) `
    -HashPath $hashPath

Remove-Item -LiteralPath $workDirectory -Recurse -Force
Write-Host "Release candidate: $zipPath"
Write-Host "ZIP SHA-256: $zipHash"
