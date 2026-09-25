[CmdletBinding()]
param(
    [string]$MsiPath,
    [string]$PublishDirectory,
    [string]$ExpectedProductName = 'FrameRelay',
    [string]$ExpectedManufacturer = 'vitorhugo-dotnet',
    [string]$ExpectedProductVersion,
    [string]$ExpectedUpgradeCode = '{8A8D6B71-9AF1-4CB1-AC70-88BA688C734B}'
)

$ErrorActionPreference = 'Stop'

function Test-FrameRelayMsi {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$MsiPath,
        [Parameter(Mandatory)][string]$PublishDirectory,
        [string]$ExpectedProductName = 'FrameRelay',
        [string]$ExpectedManufacturer = 'vitorhugo-dotnet',
        [string]$ExpectedProductVersion,
        [string]$ExpectedUpgradeCode = '{8A8D6B71-9AF1-4CB1-AC70-88BA688C734B}'
    )

    $msi = [System.IO.Path]::GetFullPath($MsiPath)
    $publish = [System.IO.Path]::GetFullPath($PublishDirectory)
    if (-not (Test-Path -LiteralPath $msi -PathType Leaf)) {
        throw "MSI does not exist: $msi"
    }
    if (-not (Test-Path -LiteralPath $publish -PathType Container)) {
        throw "Publish directory does not exist: $publish"
    }

    $publishFiles = @(Get-ChildItem -LiteralPath $publish -Recurse -File)
    if (-not ($publishFiles | Where-Object Name -CEQ 'SonicDesktopRelay.App.exe')) {
        throw 'Publish payload is missing SonicDesktopRelay.App.exe.'
    }

    $legacyTokens = @('avcodec-', 'avutil-', 'swscale-', 'swresample-', 'FFmpeg.AutoGen')
    $legacyFiles = @(
        $publishFiles | Where-Object {
            $name = $_.Name
            $legacyTokens | Where-Object { $name.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 }
        }
    )
    if ($legacyFiles.Count -gt 0) {
        throw "Publish payload contains removed codec artifacts: $($legacyFiles.Name -join ', ')."
    }

    $installer = $null
    $database = $null
    $views = [System.Collections.Generic.List[object]]::new()
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($msi, 0)

        $propertyView = $database.OpenView('SELECT `Property`, `Value` FROM `Property`')
        $views.Add($propertyView)
        $propertyView.Execute()
        $properties = @{}
        while ($record = $propertyView.Fetch()) {
            $properties[$record.StringData(1)] = $record.StringData(2)
        }
        $record = $null

        foreach ($pair in @(
            @{ Name = 'ProductName'; Expected = $ExpectedProductName }
            @{ Name = 'Manufacturer'; Expected = $ExpectedManufacturer }
            @{ Name = 'UpgradeCode'; Expected = $ExpectedUpgradeCode }
        )) {
            if ($properties[$pair.Name] -cne $pair.Expected) {
                throw "MSI $($pair.Name) is '$($properties[$pair.Name])'; expected '$($pair.Expected)'."
            }
        }
        if ($properties.ALLUSERS -ne '1') {
            throw "MSI ALLUSERS is '$($properties.ALLUSERS)'; expected a per-machine installation."
        }
        if (-not [string]::IsNullOrWhiteSpace($ExpectedProductVersion) -and
            $properties.ProductVersion -cne $ExpectedProductVersion) {
            throw "MSI ProductVersion is '$($properties.ProductVersion)'; expected '$ExpectedProductVersion'."
        }

        $summary = $database.SummaryInformation(0)
        $template = [string]$summary.Property(7)
        if ($template -notmatch '^x64;') {
            throw "MSI platform summary is '$template'; expected x64."
        }

        $directoryView = $database.OpenView('SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`')
        $views.Add($directoryView)
        $directoryView.Execute()
        $directories = @{}
        while ($record = $directoryView.Fetch()) {
            $directories[$record.StringData(1)] = @{
                Parent = $record.StringData(2)
                Name = $record.StringData(3)
            }
        }
        $record = $null

        $componentView = $database.OpenView('SELECT `Component`, `Directory_` FROM `Component`')
        $views.Add($componentView)
        $componentView.Execute()
        $componentDirectories = @{}
        while ($record = $componentView.Fetch()) {
            $componentDirectories[$record.StringData(1)] = $record.StringData(2)
        }
        $record = $null

        $fileView = $database.OpenView('SELECT `File`, `Component_`, `FileName` FROM `File`')
        $views.Add($fileView)
        $fileView.Execute()
        $packagedFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $packagedNames = [System.Collections.Generic.List[string]]::new()
        while ($record = $fileView.Fetch()) {
            $component = $record.StringData(2)
            $directoryId = $componentDirectories[$component]
            if ([string]::IsNullOrWhiteSpace($directoryId)) {
                throw "MSI file component '$component' has no target directory."
            }

            $segments = [System.Collections.Generic.List[string]]::new()
            while ($directoryId -and $directoryId -cne 'INSTALLFOLDER') {
                $directory = $directories[$directoryId]
                if ($null -eq $directory) {
                    throw "MSI file references an unknown directory '$directoryId'."
                }
                $targetName = ($directory.Name -split ':')[-1]
                $targetName = ($targetName -split '\|')[-1]
                if ($targetName -cne '.') {
                    $segments.Add($targetName)
                }
                $directoryId = $directory.Parent
            }
            if ($directoryId -cne 'INSTALLFOLDER') {
                throw "MSI file does not install beneath INSTALLFOLDER (directory '$($componentDirectories[$component])')."
            }

            $fileName = ($record.StringData(3) -split '\|')[-1]
            $segmentsArray = $segments.ToArray()
            [Array]::Reverse($segmentsArray)
            $relativePath = @($segmentsArray) + $fileName
            $null = $packagedFiles.Add(($relativePath -join '/'))
            $packagedNames.Add($fileName)
        }
        $record = $null

        $expectedFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $publishFiles) {
            $relativePath = [System.IO.Path]::GetRelativePath($publish, $file.FullName).Replace('\', '/')
            $null = $expectedFiles.Add($relativePath)
        }
        $missing = @($expectedFiles | Where-Object { -not $packagedFiles.Contains($_) })
        $unexpected = @($packagedFiles | Where-Object { -not $expectedFiles.Contains($_) })
        if ($missing.Count -gt 0 -or $unexpected.Count -gt 0 -or $expectedFiles.Count -ne $packagedFiles.Count) {
            throw "MSI payload differs from publish output. Missing: $($missing -join ', '). Unexpected: $($unexpected -join ', ')."
        }
        if (-not ($packagedNames | Where-Object { $_ -CEQ 'SonicDesktopRelay.App.exe' })) {
            throw 'MSI payload is missing SonicDesktopRelay.App.exe.'
        }

        $shortcutView = $database.OpenView('SELECT `Name`, `Directory_` FROM `Shortcut`')
        $views.Add($shortcutView)
        $shortcutView.Execute()
        $hasStartMenuShortcut = $false
        while ($record = $shortcutView.Fetch()) {
            if ($record.StringData(1) -match 'FrameRelay' -and $record.StringData(2) -eq 'FrameRelayStartMenuFolder') {
                $hasStartMenuShortcut = $true
            }
        }
        $record = $null
        if (-not $hasStartMenuShortcut) {
            throw 'MSI is missing the FrameRelay Start Menu shortcut.'
        }

        Write-Host "MSI verified: $($properties.ProductName) $($properties.ProductVersion), $($packagedFiles.Count) payload files, x64, per-machine."
    }
    finally {
        $record = $null
        foreach ($view in $views) {
            try { $view.Close() } catch { }
        }
        $views.Clear()
        $database = $null
        $installer = $null
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    if ([string]::IsNullOrWhiteSpace($MsiPath)) {
        throw 'MsiPath is required.'
    }
    if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
        throw 'PublishDirectory is required.'
    }

    Test-FrameRelayMsi `
        -MsiPath $MsiPath `
        -PublishDirectory $PublishDirectory `
        -ExpectedProductName $ExpectedProductName `
        -ExpectedManufacturer $ExpectedManufacturer `
        -ExpectedProductVersion $ExpectedProductVersion `
        -ExpectedUpgradeCode $ExpectedUpgradeCode
}
