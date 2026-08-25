[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$ZipPath,
    [Parameter(Mandatory = $true)] [string]$PackageDirectory,
    [Parameter(Mandatory = $true)] [string]$PublishedDirectory,
    [Parameter(Mandatory = $true)] [string]$PackageRootName,
    [Parameter(Mandatory = $true)] [string]$Version,
    [Parameter(Mandatory = $true)] [string]$RuntimeVersion,
    [Parameter(Mandatory = $true)] [string]$Rid,
    [Parameter(Mandatory = $true)] [string]$CommitSha,
    [Parameter(Mandatory = $true)] [string]$SdkVersion,
    [Parameter(Mandatory = $true)] [string]$SqlitePclRawVersion,
    [Parameter(Mandatory = $true)] [bool]$ExpectedSourceDirty,
    [Parameter(Mandatory = $true)] [bool]$IsPreview,
    [Parameter(Mandatory = $true)] [string]$HashPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-StreamHash([System.IO.Stream]$Stream) {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($algorithm.ComputeHash($Stream)).ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}

function Read-ZipText([System.IO.Compression.ZipArchiveEntry]$Entry) {
    $stream = $Entry.Open()
    try {
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true, 4096, $false)
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Test-SafeZipEntryPath([string]$EntryName, [string]$ExpectedRoot) {
    if ([string]::IsNullOrWhiteSpace($EntryName)) { return $false }
    $normalized = $EntryName.Replace('\', '/')
    if ($normalized.StartsWith('/') -or $normalized -match '\A[A-Za-z]:') { return $false }
    $segments = @($normalized.Split('/'))
    if ($segments.Count -lt 2 -or $segments[0] -cne $ExpectedRoot) { return $false }
    if (@($segments | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }).Count -gt 0) { return $false }
    return $true
}

function Test-ForbiddenPackageFile([string]$EntryName) {
    $name = @($EntryName.Replace('\', '/').Split('/'))[-1]
    return $name -match '(?i)\A(?:\.env(?:\..*)?|auth\.json|credentials\.json|cookie\.json|session\.json|secrets\.json|id_rsa(?:\..*)?)\z' -or
        $name -match '(?i)\.(?:pdb|jsonl|pem|key|pfx|p12|snk|db(?:-(?:wal|shm))?|sqlite|sqlite3|log)\z'
}

if (-not (Test-SafeZipEntryPath "$PackageRootName/sub/file.dll" $PackageRootName)) {
    throw 'ZIP path validator rejected its valid fixture.'
}
foreach ($unsafeFixture in @(
    "$PackageRootName/sub\..\evil.dll",
    "$PackageRootName//evil.dll",
    "$PackageRootName/./evil.dll",
    "/$PackageRootName/evil.dll",
    'C:/evil.dll',
    '\\server\share\evil.dll',
    'other-root/evil.dll')) {
    if (Test-SafeZipEntryPath $unsafeFixture $PackageRootName) {
        throw "ZIP path validator accepted an unsafe fixture: $unsafeFixture"
    }
}

$required = @(
    'CodexUsageMonitor.exe', 'CodexUsageMonitor.dll', 'CodexUsageMonitor.deps.json',
    'CodexUsageMonitor.runtimeconfig.json', 'README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md',
    'sbom.cdx.json', 'RELEASE-METADATA.json', 'licenses/Apache-2.0.txt',
    'licenses/Fluent-UI-System-Icons-LICENSE.txt', 'licenses/Fluent-UI-System-Icons-NOTICE.txt',
    'licenses/.NET-Runtime-LICENSE.txt', 'licenses/.NET-Runtime-THIRD-PARTY-NOTICES.txt',
    'licenses/Windows-Desktop-Runtime-LICENSE.txt',
    'licenses/Windows-Desktop-Runtime-THIRD-PARTY-NOTICES.txt'
)

$archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $allEntries = @($archive.Entries)
    $entries = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
    foreach ($entry in $allEntries) {
        if (-not (Test-SafeZipEntryPath $entry.FullName $PackageRootName)) {
            throw "Unsafe ZIP entry path: $($entry.FullName)"
        }
    }
    $entryNames = @($entries | ForEach-Object { $_.FullName.Replace('\', '/') })

    if ($entryNames.Count -ne ($entryNames | Sort-Object -Unique).Count) {
        throw 'Release ZIP contains duplicate entry names.'
    }
    foreach ($relative in $required) {
        if ($entryNames -notcontains "$PackageRootName/$relative") {
            throw "Release ZIP is missing required entry: $PackageRootName/$relative"
        }
    }
    if ($entryNames | Where-Object { Test-ForbiddenPackageFile $_ }) {
        throw 'Release ZIP contains a forbidden diagnostic, credential, or private-key file.'
    }

    $packageFiles = @(Get-ChildItem -LiteralPath $PackageDirectory -File -Recurse)
    $zippedHashes = @{}
    if ($entries.Count -ne $packageFiles.Count) {
        throw "ZIP/package file-count mismatch: ZIP=$($entries.Count), package=$($packageFiles.Count)"
    }
    foreach ($file in $packageFiles) {
        $relative = [System.IO.Path]::GetRelativePath($PackageDirectory, $file.FullName).Replace('\', '/')
        $entry = $archive.GetEntry("$PackageRootName/$relative")
        if ($null -eq $entry) { throw "Package file was not archived: $relative" }
        $stream = $entry.Open()
        try { $zippedHash = Get-StreamHash $stream }
        finally { $stream.Dispose() }
        $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($zippedHash -ne $sourceHash) { throw "Archived file differs from package staging file: $relative" }
        $zippedHashes[$relative] = $zippedHash
    }

    $publishedFiles = @(Get-ChildItem -LiteralPath $PublishedDirectory -File -Recurse)
    if ($publishedFiles.Count -lt 2 -or -not ($publishedFiles | Where-Object Extension -eq '.dll')) {
        throw 'Publish output is not the required multi-file layout.'
    }
    foreach ($file in $publishedFiles) {
        $relative = [System.IO.Path]::GetRelativePath($PublishedDirectory, $file.FullName).Replace('\', '/')
        $stagedPath = Join-Path $PackageDirectory $relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $stagedPath -PathType Leaf) -or -not $zippedHashes.ContainsKey($relative)) {
            throw "Published file was not included in package staging and ZIP: $relative"
        }
        $publishedHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $stagedHash = (Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($publishedHash -ne $stagedHash -or $publishedHash -ne $zippedHashes[$relative]) {
            throw "Publish, package staging and ZIP differ: $relative"
        }
    }

    $metadata = Read-ZipText ($archive.GetEntry("$PackageRootName/RELEASE-METADATA.json")) | ConvertFrom-Json
    $fileVersion = (($Version -split '[-+]', 2)[0]) + '.0'
    if ($metadata.version -isnot [string] -or $metadata.version -ne $Version -or
        $metadata.fileVersion -isnot [string] -or $metadata.fileVersion -ne $fileVersion -or
        $metadata.commit -isnot [string] -or $metadata.commit -ne $CommitSha -or $metadata.commit -notmatch '\A[0-9a-f]{40}\z' -or
        $metadata.sourceDirty -isnot [bool] -or $metadata.sourceDirty -ne $ExpectedSourceDirty -or
        $metadata.sdk -isnot [string] -or $metadata.sdk -ne $SdkVersion -or
        $metadata.runtime -isnot [string] -or $metadata.runtime -ne $RuntimeVersion -or
        $metadata.rid -isnot [string] -or $metadata.rid -ne $Rid -or
        $metadata.layout -isnot [string] -or $metadata.layout -ne 'multi-file' -or
        (($metadata.publishFileCount -isnot [int]) -and ($metadata.publishFileCount -isnot [long])) -or
        [long]$metadata.publishFileCount -ne $publishedFiles.Count -or
        $metadata.sqlitePclRaw -isnot [string] -or $metadata.sqlitePclRaw -ne $SqlitePclRawVersion -or
        $metadata.authenticode -isnot [string] -or [string]::IsNullOrWhiteSpace($metadata.authenticode) -or
        $metadata.preview -isnot [bool] -or $metadata.preview -ne $IsPreview) {
        throw 'Release metadata fields or types do not match the requested build.'
    }
    if (-not $IsPreview -and $metadata.sourceDirty) {
        throw 'Formal release metadata cannot declare a dirty source tree.'
    }

    $sbom = Read-ZipText ($archive.GetEntry("$PackageRootName/sbom.cdx.json")) | ConvertFrom-Json
    if ($sbom.metadata.component.version -ne $Version) { throw 'SBOM version does not match release version.' }
    $sbomComponents = @($sbom.components | ForEach-Object { "$($_.name)@$($_.version)" })
    if ($sbomComponents -notcontains "SQLitePCLRaw.lib.e_sqlite3@$SqlitePclRawVersion" -or
        $sbomComponents -notcontains "Microsoft.NETCore.App.Runtime.win-x64@$RuntimeVersion" -or
        $sbomComponents | Where-Object { $_ -match '\A(?:MSTest\.|Microsoft\.NET\.Test\.Sdk@)' }) {
        throw 'SBOM is not the expected release/runtime dependency inventory.'
    }

    $runtimeConfig = Read-ZipText ($archive.GetEntry("$PackageRootName/CodexUsageMonitor.runtimeconfig.json")) | ConvertFrom-Json
    $frameworkVersions = @($runtimeConfig.runtimeOptions.includedFrameworks | ForEach-Object { [string]$_.version })
    if ($frameworkVersions.Count -eq 0 -or @($frameworkVersions | Where-Object { $_ -ne $RuntimeVersion }).Count -gt 0) {
        throw 'runtimeconfig does not contain only the required self-contained runtime version.'
    }

    $deps = Read-ZipText ($archive.GetEntry("$PackageRootName/CodexUsageMonitor.deps.json")) | ConvertFrom-Json
    if ([string]$deps.runtimeTarget.name -notmatch "/$([Regex]::Escape($Rid))$") {
        throw 'deps.json runtime target does not match the requested RID.'
    }
}
finally { $archive.Dispose() }

$publishedExecutable = Join-Path $PublishedDirectory 'CodexUsageMonitor.exe'
$stagedExecutable = Join-Path $PackageDirectory 'CodexUsageMonitor.exe'
$zipValidationDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("CodexUsageMonitor-ZipValidation-" + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($zipValidationDirectory) | Out-Null
$zippedExecutable = Join-Path $zipValidationDirectory 'CodexUsageMonitor.exe'
try {
    $validationArchive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $zippedExecutableEntry = $validationArchive.GetEntry("$PackageRootName/CodexUsageMonitor.exe")
        if ($null -eq $zippedExecutableEntry) { throw 'ZIP executable is missing.' }
        $input = $zippedExecutableEntry.Open()
        $output = [System.IO.File]::Create($zippedExecutable)
        try { $input.CopyTo($output) }
        finally { $output.Dispose(); $input.Dispose() }
    }
    finally { $validationArchive.Dispose() }

    $identityHashes = @()
    foreach ($executableIdentity in @(
        @{ Label = 'publish'; Path = $publishedExecutable },
        @{ Label = 'package staging'; Path = $stagedExecutable },
        @{ Label = 'ZIP'; Path = $zippedExecutable })) {
        $versionInfo = (Get-Item -LiteralPath $executableIdentity.Path).VersionInfo
        if ($versionInfo.FileVersion -ne $fileVersion -or $versionInfo.ProductVersion -ne $Version) {
            throw "$($executableIdentity.Label) executable version mismatch: FileVersion=$($versionInfo.FileVersion), ProductVersion=$($versionInfo.ProductVersion)"
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $executableIdentity.Path
        if ([string]$signature.Status -ne [string]$metadata.authenticode) {
            throw "$($executableIdentity.Label) Authenticode status does not match release metadata."
        }
        $identityHashes += (Get-FileHash -LiteralPath $executableIdentity.Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if (($identityHashes | Sort-Object -Unique).Count -ne 1) {
        throw 'Publish, package staging and ZIP executable hashes differ.'
    }
}
finally {
    Remove-Item -LiteralPath $zipValidationDirectory -Recurse -Force
}

$hashLines = @(Get-Content -LiteralPath $HashPath)
$zipName = [System.IO.Path]::GetFileName($ZipPath)
$zipHash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hashLines.Count -ne 1 -or $hashLines[0] -ne "$zipHash  $zipName") {
    throw 'SHA256SUMS.txt does not exactly match the final ZIP filename and hash.'
}

$exeHash = (Get-FileHash -LiteralPath $stagedExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Package files: $($packageFiles.Count); published files: $($publishedFiles.Count)"
Write-Host "Packaged EXE SHA-256: $exeHash"
