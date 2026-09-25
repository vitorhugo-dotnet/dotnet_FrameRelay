param(
    [string]$MsiPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/release/FrameRelay-win-x64-0.0.0-alpha.pr42.110.msi'),
    [string]$PublishDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/publish/win-x64')
)

$ErrorActionPreference = 'Stop'
$verifier = Join-Path (Split-Path -Parent $PSScriptRoot) '.github/scripts/Test-FrameRelayMsi.ps1'
if (-not (Test-Path -LiteralPath $verifier)) {
    throw "MSI verifier is missing: $verifier"
}

$testMsiPath = $MsiPath
$testPublishDirectory = $PublishDirectory
. $verifier
$MsiPath = $testMsiPath
$PublishDirectory = $testPublishDirectory

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

Test-FrameRelayMsi -MsiPath $MsiPath -PublishDirectory $PublishDirectory -ExpectedProductVersion '0.0.0'
Write-Host 'PASS: valid MSI identity and complete payload'

Assert-Throws {
    Test-FrameRelayMsi -MsiPath $MsiPath -PublishDirectory $PublishDirectory -ExpectedProductName 'WrongName'
} 'ProductName' 'Wrong product name is rejected'

Assert-Throws {
    Test-FrameRelayMsi -MsiPath $MsiPath -PublishDirectory $PublishDirectory -ExpectedUpgradeCode '{11111111-1111-1111-1111-111111111111}'
} 'UpgradeCode' 'Changed UpgradeCode is rejected'

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
Copy-Item -LiteralPath $PublishDirectory -Destination $temp -Recurse
try {
    Remove-Item -LiteralPath (Join-Path $temp 'SonicDesktopRelay.App.exe') -Force
    Assert-Throws {
        Test-FrameRelayMsi -MsiPath $MsiPath -PublishDirectory $temp -ExpectedProductVersion '0.0.0'
    } 'payload|missing|SonicDesktopRelay\.App\.exe' 'Missing application payload is rejected'

    Copy-Item -LiteralPath $PublishDirectory -Destination (Join-Path $temp 'with-codec') -Recurse
    Set-Content -LiteralPath (Join-Path $temp 'with-codec/avcodec-test.dll') -Value 'forbidden codec'
    Assert-Throws {
        Test-FrameRelayMsi -MsiPath $MsiPath -PublishDirectory (Join-Path $temp 'with-codec') -ExpectedProductVersion '0.0.0'
    } 'codec|avcodec' 'Removed codec artifact is rejected'
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}

Write-Host 'All MSI verification tests passed.'
