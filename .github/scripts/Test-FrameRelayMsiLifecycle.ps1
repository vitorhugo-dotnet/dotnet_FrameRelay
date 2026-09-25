[CmdletBinding()]
param(
    [string]$FirstMsiPath,
    [string]$SecondMsiPath,
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'

function Get-FrameRelayMsiMetadata {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "MSI does not exist: $fullPath"
    }

    $installer = $null
    $database = $null
    $view = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($fullPath, 0)
        $view = $database.OpenView('SELECT `Property`, `Value` FROM `Property`')
        $view.Execute()
        $properties = @{}
        while ($record = $view.Fetch()) {
            $properties[$record.StringData(1)] = $record.StringData(2)
        }
        $record = $null

        return [pscustomobject]@{
            Path = $fullPath
            ProductName = $properties.ProductName
            ProductVersion = $properties.ProductVersion
            Version = [version]$properties.ProductVersion
            ProductCode = $properties.ProductCode
            UpgradeCode = $properties.UpgradeCode
            AllUsers = $properties.ALLUSERS
            Platform = [string]$database.SummaryInformation(0).Property(7)
        }
    }
    finally {
        $record = $null
        if ($view) { $view.Close() }
        $view = $null
        $database = $null
        $installer = $null
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
    }
}

function Get-FrameRelayUninstallEntries {
    $locations = @(
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($location in $locations) {
        Get-ItemProperty -Path $location -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -eq 'FrameRelay' } |
            ForEach-Object {
                [pscustomobject]@{
                    DisplayName = $_.DisplayName
                    DisplayVersion = $_.DisplayVersion
                    ProductCode = $_.PSChildName
                }
            }
    }
}

function Invoke-FrameRelayMsiExec {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$LogPath
    )

    $executable = Join-Path $env:WINDIR 'System32/msiexec.exe'
    $quotedArguments = @(
        $Arguments | ForEach-Object {
            if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' }
            else { $_ }
        }
    )
    $process = Start-Process -FilePath $executable -ArgumentList ($quotedArguments -join ' ') -Wait -PassThru -NoNewWindow
    $exitCode = $process.ExitCode
    if ($exitCode -ne 0) {
        throw "msiexec failed with exit code $exitCode. See log: $LogPath"
    }
}

function Start-FrameRelayForSmokeTest {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstallDirectory)

    $executable = Join-Path $InstallDirectory 'SonicDesktopRelay.App.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Installed application is missing: $executable"
    }

    $process = Start-Process -FilePath $executable -WorkingDirectory $InstallDirectory -PassThru
    Start-Sleep -Seconds 4
    $process.Refresh()
    if ($process.HasExited) {
        throw "FrameRelay exited during launch smoke test with exit code $($process.ExitCode)."
    }
    return $process
}

function Stop-FrameRelaySmokeTests {
    param([System.Collections.Generic.List[System.Diagnostics.Process]]$Processes)

    foreach ($process in $Processes) {
        try {
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
        }
        catch { }
    }
    $Processes.Clear()
}

function Test-FrameRelayMsiLifecycle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FirstMsiPath,
        [Parameter(Mandatory)][string]$SecondMsiPath,
        [switch]$PreflightOnly
    )

    $first = Get-FrameRelayMsiMetadata -Path $FirstMsiPath
    $second = Get-FrameRelayMsiMetadata -Path $SecondMsiPath
    foreach ($metadata in @($first, $second)) {
        if ($metadata.ProductName -cne 'FrameRelay') {
            throw "Expected FrameRelay MSI; found '$($metadata.ProductName)' at $($metadata.Path)."
        }
        if ($metadata.UpgradeCode -cne '{8A8D6B71-9AF1-4CB1-AC70-88BA688C734B}') {
            throw "MSI $($metadata.Path) has an unexpected UpgradeCode '$($metadata.UpgradeCode)'."
        }
        if ($metadata.AllUsers -ne '1' -or $metadata.Platform -notmatch '^x64;') {
            throw "MSI $($metadata.Path) is not a per-machine x64 package."
        }
    }
    if ($first.ProductCode -eq $second.ProductCode) {
        throw 'The two MSI packages must have different ProductCodes for a major upgrade.'
    }
    if ($second.Version -le $first.Version) {
        throw "The second MSI must be newer than the first; received $($first.Version) then $($second.Version)."
    }

    $preflight = [pscustomobject]@{
        IsValidUpgrade = $true
        FirstVersion = $first.Version
        SecondVersion = $second.Version
        UpgradeCode = $first.UpgradeCode
    }
    if ($PreflightOnly) {
        return $preflight
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The MSI lifecycle integration test requires an elevated administrator token to install into Program Files.'
    }

    if (@(Get-FrameRelayUninstallEntries).Count -gt 0) {
        throw 'A FrameRelay installation is already registered; refusing to alter an existing installation during the lifecycle test.'
    }

    $installDirectory = Join-Path $env:ProgramFiles 'FrameRelay'
    if (Test-Path -LiteralPath $installDirectory) {
        $existingFiles = @(Get-ChildItem -LiteralPath $installDirectory -Recurse -File -Force -ErrorAction SilentlyContinue)
        if ($existingFiles.Count -gt 0) {
            throw "Install directory already contains files; refusing to overwrite: $installDirectory"
        }
    }

    $runId = [guid]::NewGuid().ToString('N')
    $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "FrameRelayMsiLifecycle-$runId"
    $userDataDirectory = Join-Path $env:LOCALAPPDATA 'FrameRelay'
    $logDirectory = Join-Path $userDataDirectory 'logs'
    $markerPath = Join-Path $logDirectory "lifecycle-$runId.log"
    $shortcutPath = Join-Path $env:ProgramData 'Microsoft/Windows/Start Menu/Programs/FrameRelay/FrameRelay.lnk'
    $startedProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
    New-Item -ItemType Directory -Path $testRoot, $logDirectory -Force | Out-Null
    Set-Content -LiteralPath $markerPath -Value $runId

    $lifecyclePassed = $false
    try {
        $firstLog = Join-Path $testRoot 'install-first.log'
        Invoke-FrameRelayMsiExec -Arguments @('/i', $first.Path, '/qn', '/norestart', '/l*v', $firstLog) -LogPath $firstLog
        $entries = @(Get-FrameRelayUninstallEntries)
        if ($entries.Count -ne 1 -or $entries[0].DisplayVersion -ne [string]$first.Version) {
            throw "First install did not create exactly one FrameRelay uninstall entry at version $($first.Version)."
        }
        if (-not (Test-Path -LiteralPath $shortcutPath -PathType Leaf)) {
            throw "FrameRelay Start Menu shortcut was not installed: $shortcutPath"
        }
        $startedProcesses.Add((Start-FrameRelayForSmokeTest -InstallDirectory $installDirectory))
        Stop-FrameRelaySmokeTests -Processes $startedProcesses

        $upgradeLog = Join-Path $testRoot 'upgrade.log'
        Invoke-FrameRelayMsiExec -Arguments @('/i', $second.Path, '/qn', '/norestart', '/l*v', $upgradeLog) -LogPath $upgradeLog
        $entries = @(Get-FrameRelayUninstallEntries)
        if ($entries.Count -ne 1 -or $entries[0].DisplayVersion -ne [string]$second.Version) {
            throw "Major upgrade did not leave exactly one FrameRelay uninstall entry at version $($second.Version)."
        }
        $startedProcesses.Add((Start-FrameRelayForSmokeTest -InstallDirectory $installDirectory))
        Stop-FrameRelaySmokeTests -Processes $startedProcesses

        $uninstallLog = Join-Path $testRoot 'uninstall.log'
        Invoke-FrameRelayMsiExec -Arguments @('/x', $second.ProductCode, '/qn', '/norestart', '/l*v', $uninstallLog) -LogPath $uninstallLog
        if (@(Get-FrameRelayUninstallEntries).Count -ne 0) {
            throw 'FrameRelay uninstall left an Installed apps / Programs and Features entry.'
        }
        if (Test-Path -LiteralPath (Join-Path $installDirectory 'SonicDesktopRelay.App.exe')) {
            throw 'FrameRelay uninstall left the application executable behind.'
        }
        if (Test-Path -LiteralPath $shortcutPath) {
            throw 'FrameRelay uninstall left its Start Menu shortcut behind.'
        }
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or
            (Get-Content -Raw -LiteralPath $markerPath).Trim() -cne $runId) {
            throw 'FrameRelay uninstall removed or modified user-owned data under %LOCALAPPDATA%.'
        }

        Write-Host "FrameRelay MSI lifecycle passed: $($first.Version) -> $($second.Version), one ARP entry, both launches, uninstall, and user data preserved."
        $lifecyclePassed = $true
        return $preflight
    }
    finally {
        foreach ($process in $startedProcesses) {
            try {
                if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
            }
            catch { }
        }

        foreach ($entry in @(Get-FrameRelayUninstallEntries)) {
            $cleanupLog = Join-Path $testRoot "cleanup-$($entry.ProductCode).log"
            try {
                Invoke-FrameRelayMsiExec -Arguments @('/x', $entry.ProductCode, '/qn', '/norestart', '/l*v', $cleanupLog) -LogPath $cleanupLog
            }
            catch {
                Write-Warning "Lifecycle cleanup could not remove product $($entry.ProductCode): $($_.Exception.Message)"
            }
        }
        Remove-Item -LiteralPath $markerPath -Force -ErrorAction SilentlyContinue
        if ($lifecyclePassed) {
            Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
        else {
            Write-Warning "FrameRelay MSI lifecycle failed; diagnostic logs were retained at $testRoot"
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    if ([string]::IsNullOrWhiteSpace($FirstMsiPath) -or [string]::IsNullOrWhiteSpace($SecondMsiPath)) {
        throw 'FirstMsiPath and SecondMsiPath are required.'
    }

    Test-FrameRelayMsiLifecycle `
        -FirstMsiPath $FirstMsiPath `
        -SecondMsiPath $SecondMsiPath `
        -PreflightOnly:$PreflightOnly
}
