[CmdletBinding()]
param(
    [string]$PublishDirectory,
    [string]$ReleaseVersion,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$script:RepositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function ConvertTo-MsiVersion {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Version)

    $identifier = '(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)'
    $pattern = "^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-$identifier(?:\.$identifier)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
    if ($Version -notmatch $pattern) {
        throw "Release version '$Version' must be a semantic version without a leading v."
    }

    $major = [int]$Matches.major
    $minor = [int]$Matches.minor
    $patch = [int]$Matches.patch
    if ($major -gt 255 -or $minor -gt 255 -or $patch -gt 65535) {
        throw "Release version '$Version' has a numeric field outside the Windows Installer range (major/minor 0-255, patch 0-65535)."
    }

    return "$major.$minor.$patch"
}

function Get-FrameRelayMsiFileName {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Version)

    $null = ConvertTo-MsiVersion $Version
    return "FrameRelay-win-x64-$Version.msi"
}

function Get-NextMsiVersion {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Version)

    $numericVersion = ConvertTo-MsiVersion $Version
    $parts = @($numericVersion.Split('.') | ForEach-Object { [int]$_ })
    if ($parts[2] -lt 65535) {
        $parts[2]++
    }
    elseif ($parts[1] -lt 255) {
        $parts[1]++
        $parts[2] = 0
    }
    elseif ($parts[0] -lt 255) {
        $parts[0]++
        $parts[1] = 0
        $parts[2] = 0
    }
    else {
        throw "MSI ProductVersion '$numericVersion' cannot be incremented within the Windows Installer range."
    }

    return ($parts -join '.')
}

function Invoke-FrameRelayMsiBuild {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PublishDirectory,
        [Parameter(Mandatory)][string]$ReleaseVersion,
        [Parameter(Mandatory)][string]$OutputDirectory
    )

    $msiVersion = ConvertTo-MsiVersion $ReleaseVersion
    $publishPath = [System.IO.Path]::GetFullPath($PublishDirectory)
    $appPath = Join-Path $publishPath 'SonicDesktopRelay.App.exe'
    if (-not (Test-Path -LiteralPath $publishPath -PathType Container)) {
        throw "Publish directory does not exist: $publishPath"
    }
    if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) {
        throw "Publish directory is missing SonicDesktopRelay.App.exe: $publishPath"
    }

    $outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
    $buildPath = Join-Path $outputPath '.wix-build'
    $projectPath = Join-Path $script:RepositoryRoot 'packaging/FrameRelay/FrameRelay.wixproj'
    $arguments = @(
        'build'
        '-t:Rebuild'
        $projectPath
        "-p:PublishDirectory=$publishPath"
        "-p:ProductVersion=$msiVersion"
        "-p:OutputPath=$buildPath"
        '-p:Platform=x64'
    )

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "WiX build failed with exit code $LASTEXITCODE."
    }

    $builtMsi = Join-Path $buildPath 'FrameRelay.msi'
    if (-not (Test-Path -LiteralPath $builtMsi -PathType Leaf)) {
        throw "WiX build succeeded but did not create the expected MSI: $builtMsi"
    }

    $destination = Join-Path $outputPath (Get-FrameRelayMsiFileName $ReleaseVersion)
    Copy-Item -LiteralPath $builtMsi -Destination $destination -Force
    return $destination
}

if ($MyInvocation.InvocationName -ne '.') {
    if ([string]::IsNullOrWhiteSpace($ReleaseVersion)) {
        throw 'ReleaseVersion is required.'
    }
    if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
        $PublishDirectory = Join-Path $script:RepositoryRoot 'artifacts/publish/win-x64'
    }
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = Join-Path $script:RepositoryRoot 'artifacts/release'
    }

    Invoke-FrameRelayMsiBuild `
        -PublishDirectory $PublishDirectory `
        -ReleaseVersion $ReleaseVersion `
        -OutputDirectory $OutputDirectory
}
