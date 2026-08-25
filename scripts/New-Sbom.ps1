[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$components = [ordered]@{}

foreach ($relativeLockPath in @(
    'src\CodexUsageMonitor.Core\packages.lock.json',
    'src\CodexUsageMonitor\packages.lock.json')) {
    $lockPath = Join-Path $repositoryRoot $relativeLockPath
    $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
    foreach ($framework in $lock.dependencies.PSObject.Properties) {
        foreach ($dependency in $framework.Value.PSObject.Properties) {
            $resolved = [string]$dependency.Value.resolved
            if ([string]::IsNullOrWhiteSpace($resolved)) { continue }
            $key = "$($dependency.Name)@$resolved"
            if (-not $components.Contains($key)) {
                $licenseId = if ($dependency.Name -like 'SQLitePCLRaw*') { 'Apache-2.0' } else { 'MIT' }
                $components[$key] = [ordered]@{
                    type = 'library'
                    name = $dependency.Name
                    version = $resolved
                    scope = 'required'
                    purl = "pkg:nuget/$([System.Uri]::EscapeDataString($dependency.Name))@$resolved"
                    licenses = @([ordered]@{ license = [ordered]@{ id = $licenseId } })
                }
            }
        }
    }
}

foreach ($runtime in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
    $components["$runtime@8.0.30"] = [ordered]@{
        type = 'framework'
        name = $runtime
        version = '8.0.30'
        scope = 'required'
        licenses = @([ordered]@{ license = [ordered]@{ id = 'MIT' } })
    }
}

$fontPath = Join-Path $repositoryRoot 'src\CodexUsageMonitor\Assets\FluentSystemIcons-Regular.ttf'
$fontHash = (Get-FileHash -LiteralPath $fontPath -Algorithm SHA256).Hash.ToUpperInvariant()
$components['Fluent UI System Icons font'] = [ordered]@{
    type = 'file'
    name = 'FluentSystemIcons-Regular.ttf'
    version = 'fb047fb395f45ccf1129f8eaee672c9dfa99152e'
    scope = 'required'
    hashes = @([ordered]@{ alg = 'SHA-256'; content = $fontHash })
    licenses = @([ordered]@{ license = [ordered]@{ id = 'MIT' } })
    externalReferences = @([ordered]@{ type = 'vcs'; url = 'https://github.com/microsoft/fluentui-system-icons/tree/fb047fb395f45ccf1129f8eaee672c9dfa99152e' })
}

$document = [ordered]@{
    bomFormat = 'CycloneDX'
    specVersion = '1.5'
    version = 1
    metadata = [ordered]@{
        component = [ordered]@{
            type = 'application'
            name = 'Codex Usage Monitor'
            version = $Version
            licenses = @([ordered]@{ license = [ordered]@{ id = 'MIT' } })
        }
        tools = @([ordered]@{ vendor = 'Codex Usage Monitor contributors'; name = 'scripts/New-Sbom.ps1'; version = $Version })
    }
    components = @($components.Values | Sort-Object { $_.name }, { $_.version })
}

$directory = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutputPath))
[System.IO.Directory]::CreateDirectory($directory) | Out-Null
[System.IO.File]::WriteAllText(
    [System.IO.Path]::GetFullPath($OutputPath),
    ($document | ConvertTo-Json -Depth 12),
    [System.Text.UTF8Encoding]::new($false))
