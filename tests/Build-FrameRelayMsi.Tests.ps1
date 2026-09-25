$ErrorActionPreference = 'Stop'

$builder = Join-Path (Split-Path -Parent $PSScriptRoot) '.github/scripts/Build-FrameRelayMsi.ps1'
if (-not (Test-Path -LiteralPath $builder)) {
    throw "Build script is missing: $builder"
}

. $builder

function Assert-Equal {
    param([object]$Expected, [object]$Actual, [string]$Name)
    if ($Expected -cne $Actual) {
        throw "$Name`: expected '$Expected', got '$Actual'."
    }
    Write-Host "PASS: $Name"
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$MessagePattern, [string]$Name)
    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -match $MessagePattern) {
            Write-Host "PASS: $Name"
            return
        }
        throw "$Name`: unexpected error '$($_.Exception.Message)'."
    }
    throw "$Name`: expected an error matching '$MessagePattern'."
}

Assert-Equal '1.2.3' (ConvertTo-MsiVersion '1.2.3') 'Stable version maps to MSI version'
Assert-Equal '0.0.0' (ConvertTo-MsiVersion '0.0.0-alpha.pr42.110') 'Prerelease maps to numeric MSI version'
Assert-Equal 'FrameRelay-win-x64-0.0.0-alpha.pr42.110.msi' (Get-FrameRelayMsiFileName '0.0.0-alpha.pr42.110') 'Asset filename retains full release version'
Assert-Throws { ConvertTo-MsiVersion 'v1.2.3' } 'semantic version' 'Version with tag prefix is rejected'
Assert-Throws { ConvertTo-MsiVersion '1.2.65536' } 'range' 'Out-of-range MSI field is rejected'

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    Assert-Throws {
        Invoke-FrameRelayMsiBuild -PublishDirectory $temp -ReleaseVersion '1.2.3' -OutputDirectory $temp
    } 'SonicDesktopRelay\.App\.exe' 'Missing application executable fails before build'
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}

Write-Host 'All MSI build contract tests passed.'
