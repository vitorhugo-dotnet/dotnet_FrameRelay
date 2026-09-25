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

    Set-Content -LiteralPath (Join-Path $temp 'SonicDesktopRelay.App.exe') -Value 'fixture'
    $script:dotnetArguments = @()
    function global:dotnet {
        $script:dotnetArguments = @($args)
        $outputArgument = $script:dotnetArguments | Where-Object { $_ -like '-p:OutputPath=*' }
        $buildOutput = $outputArgument.Substring('-p:OutputPath='.Length)
        New-Item -ItemType Directory -Path $buildOutput -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $buildOutput 'FrameRelay.msi') -Value 'fixture MSI'
        $global:LASTEXITCODE = 0
    }

    $msi = Invoke-FrameRelayMsiBuild -PublishDirectory $temp -ReleaseVersion '2.3.4' -OutputDirectory $temp
    Assert-Equal 'FrameRelay-win-x64-2.3.4.msi' (Split-Path -Leaf $msi) 'Builder names package from full release version'
    Assert-Equal 'fixture MSI' (Get-Content -Raw -LiteralPath $msi).Trim() 'Builder copies WiX output'
    Assert-Equal $true ($script:dotnetArguments -contains '-t:Rebuild') 'Builder forces WiX to rebuild release-specific metadata'
    Assert-Equal $true ($script:dotnetArguments -contains '-p:ProductVersion=2.3.4') 'Builder passes normalized product version to WiX'
    Remove-Item Function:\dotnet -ErrorAction SilentlyContinue
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}

Write-Host 'All MSI build contract tests passed.'

$workflowPath = Join-Path (Split-Path -Parent $PSScriptRoot) '.github/workflows/release.yml'
$workflow = Get-Content -Raw -LiteralPath $workflowPath
$wixProjectPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'packaging/FrameRelay/FrameRelay.wixproj'
$wixProject = Get-Content -Raw -LiteralPath $wixProjectPath
$publishIndex = $workflow.IndexOf('name: Publish single-file EXE', [StringComparison]::Ordinal)
$msiIndex = $workflow.IndexOf('name: Build and verify MSI', [StringComparison]::Ordinal)
$assetIndex = $workflow.IndexOf('name: Create ZIP, EXE, and checksums', [StringComparison]::Ordinal)
Assert-Equal $true ($publishIndex -ge 0 -and $msiIndex -gt $publishIndex -and $assetIndex -gt $msiIndex) 'MSI build runs after app publish and before release assets'
Assert-Equal $true ($wixProject.Contains('<Project Sdk="WixToolset.Sdk/5.0.1">')) 'WiX Toolset v5 is pinned'
Assert-Equal $true ($workflow.Contains('./.github/scripts/Build-FrameRelayMsi.ps1')) 'Release workflow builds the MSI'
Assert-Equal $true ($workflow.Contains('./.github/scripts/Test-FrameRelayMsi.ps1')) 'Release workflow validates MSI metadata and payload'
Assert-Equal $true ($workflow.Contains("Where-Object { `$_.Extension -in @('.zip', '.exe', '.msi') }")) 'Checksum generation includes the MSI'
Assert-Equal $true ($workflow.Contains("'`${{ steps.msi.outputs.path }}'")) 'GitHub Release uploads the MSI asset'
Assert-Equal $true ($workflow.Contains("`$extraArgs += '--verify-tag'") -and $workflow.Contains("`$extraArgs += '--latest'") -and $workflow.Contains("`$extraArgs += '--prerelease'")) 'Existing stable and prerelease release flags remain'
Write-Host 'All MSI release workflow contract tests passed.'
