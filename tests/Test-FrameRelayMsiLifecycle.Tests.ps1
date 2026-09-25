param(
    [string]$FirstMsiPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/release/FrameRelay-win-x64-0.0.0-alpha.pr42.110.msi'),
    [string]$SecondMsiPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/lifecycle/FrameRelay-win-x64-0.0.1.msi'),
    [string]$SameVersionMsiPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/lifecycle/FrameRelay-win-x64-0.0.0-alpha.pr42.110-upgrade-test.msi')
)

$ErrorActionPreference = 'Stop'
$lifecycle = Join-Path (Split-Path -Parent $PSScriptRoot) '.github/scripts/Test-FrameRelayMsiLifecycle.ps1'
if (-not (Test-Path -LiteralPath $lifecycle)) {
    throw "MSI lifecycle verifier is missing: $lifecycle"
}

$testFirstMsiPath = $FirstMsiPath
$testSecondMsiPath = $SecondMsiPath
. $lifecycle
$FirstMsiPath = $testFirstMsiPath
$SecondMsiPath = $testSecondMsiPath

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

$result = Test-FrameRelayMsiLifecycle -FirstMsiPath $FirstMsiPath -SecondMsiPath $SecondMsiPath -PreflightOnly
if (-not $result.IsValidUpgrade -or $result.FirstVersion -ge $result.SecondVersion) {
    throw 'Lifecycle preflight did not confirm the higher-version same-product upgrade.'
}
Write-Host "PASS: upgrade preflight $($result.FirstVersion) -> $($result.SecondVersion)"

$sameVersionResult = Test-FrameRelayMsiLifecycle -FirstMsiPath $FirstMsiPath -SecondMsiPath $SameVersionMsiPath -PreflightOnly
if (-not $sameVersionResult.IsValidUpgrade -or $sameVersionResult.FirstVersion -ne $sameVersionResult.SecondVersion) {
    throw 'Lifecycle preflight did not accept same-version prerelease packages.'
}
Write-Host "PASS: same-version upgrade preflight $($sameVersionResult.FirstVersion) -> $($sameVersionResult.SecondVersion)"

Assert-Throws {
    Test-FrameRelayMsiLifecycle -FirstMsiPath $SecondMsiPath -SecondMsiPath $FirstMsiPath -PreflightOnly
} 'older|newer|higher|increase' 'A lower second MSI is rejected before installation'

Write-Host 'All MSI lifecycle contract tests passed.'
