param(
    [Parameter(Mandatory = $true)]
    [string]$Msi,
    [string]$PublishDirectory = 'artifacts\installer\payload',
    [switch]$Extract,
    [string]$PreviousMsi
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not [IO.Path]::IsPathRooted($Msi)) { $Msi = Join-Path $repoRoot $Msi }
if (-not [IO.Path]::IsPathRooted($PublishDirectory)) { $PublishDirectory = Join-Path $repoRoot $PublishDirectory }
$Msi = (Get-Item -LiteralPath $Msi).FullName
$PublishDirectory = (Get-Item -LiteralPath $PublishDirectory).FullName.TrimEnd('\')
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($Msi, 0)
function Get-Rows([string]$Query, [int]$Columns) {
    $view = $database.OpenView($Query)
    $null = $view.Execute()
    try {
        while ($record = $view.Fetch()) {
            $values = @(for ($column = 1; $column -le $Columns; $column++) { $record.StringData($column) })
            ,$values
        }
    } finally { $null = $view.Close() }
}
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
$properties = @{}
Get-Rows 'SELECT `Property`, `Value` FROM `Property`' 2 | ForEach-Object { $properties[$_[0]] = $_[1] }
Assert ($properties.ProductName -eq 'TickRiver') 'Wrong product name.'
Assert ($properties.UpgradeCode -eq '{74C59C29-0743-4D2A-B150-0969EE3DC307}') 'Upgrade identity changed.'
Assert (-not $properties.ContainsKey('ALLUSERS')) 'Installer must be per user.'
Assert ($properties.MSIRESTARTMANAGERCONTROL -eq 'DisableShutdown') 'Installer must not shut down AI hosts or timers.'
$summary = $database.SummaryInformation(0)
Assert ($summary.Property(7) -eq 'x64;1033') 'Installer must target Windows x64.'
Assert (($summary.Property(15) -band 8) -ne 0) 'Installer must not require elevation.'
$payload = @(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File)
$fileRows = @(Get-Rows 'SELECT `FileName` FROM `File`' 1)
Assert ($fileRows.Count -eq $payload.Count) 'MSI file count differs from the published payload.'
$fileNames = @($fileRows | ForEach-Object { ($_[0] -split '\|')[-1] })
foreach ($file in @('TickRiver.exe', 'TickRiver.Mcp.exe', 'TickRiver.dll', 'TickRiver.Mcp.dll', 'TickRiver.Core.dll',
    'TickRiver.pdb', 'TickRiver.Mcp.pdb', 'TickRiver.Core.pdb', 'hostfxr.dll', 'coreclr.dll')) {
    Assert ($fileNames -contains $file) "Missing payload file: $file."
}
Assert (-not ($fileNames | Where-Object { $_ -match '(?i)\.(sqlite(?:3)?(?:-(?:wal|shm))?|db|pfx|p12|pem|key|cer|crt|log|bak)$' })) 'MSI contains data, a key, a certificate or a log.'
$registry = @(Get-Rows 'SELECT `Root`, `Key` FROM `Registry`' 2)
foreach ($row in $registry) { Assert ($row[0] -eq '1' -and $row[1].StartsWith('Software\TickRiver\Installer\')) 'Unexpected registry entry.' }
$shortcuts = @(Get-Rows 'SELECT `Directory_`, `WkDir` FROM `Shortcut`' 2)
Assert ($shortcuts.Count -eq 2) 'Expected desktop and Start menu shortcuts.'
foreach ($row in $shortcuts) {
    Assert ($row[0] -in @('DesktopFolder', 'ProgramMenuFolder') -and $row[1] -eq 'INSTALLFOLDER') 'Wrong shortcut folder or working directory.'
}
$events = @(Get-Rows 'SELECT `Dialog_`, `Control_`, `Event`, `Argument` FROM `ControlEvent`' 4)
Assert ([bool]($events | Where-Object { $_[0] -eq 'WelcomeDlg' -and $_[1] -eq 'Next' -and $_[2] -eq 'NewDialog' -and $_[3] -eq 'VerifyReadyDlg' })) 'Welcome dialog cannot reach installation.'
Assert (-not ($events | Where-Object { $_[3] -eq 'LicenseAgreementDlg' })) 'Installer must not assert an unchosen license.'
$removals = @(Get-Rows 'SELECT `FileName`, `DirProperty` FROM `RemoveFile`' 2)
Assert (-not ($removals | Where-Object { $_[0] })) 'Uninstall must remove only explicit MSI files and empty installation folders.'
$upgrades = @(Get-Rows 'SELECT `ActionProperty` FROM `Upgrade`' 1)
Assert ([bool]($upgrades | Where-Object { $_[0] -eq 'WIX_UPGRADE_DETECTED' })) 'Missing major upgrade detection.'
Assert ([bool]($upgrades | Where-Object { $_[0] -eq 'WIX_DOWNGRADE_DETECTED' })) 'Missing downgrade protection.'

if ($Extract) {
    $extractRoot = Join-Path $repoRoot ('work\installer-extract-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $extractRoot | Out-Null
    $log = Join-Path $extractRoot 'extract.log'
    $process = Start-Process msiexec.exe -WindowStyle Hidden -ArgumentList @('/a', "`"$Msi`"", '/qn', '/norestart',
        "TARGETDIR=`"$extractRoot`"", '/l*v', "`"$log`"") -PassThru -Wait
    Assert ($process.ExitCode -eq 0) "Administrative extraction failed ($($process.ExitCode)); see $log."
    $executable = @(Get-ChildItem -LiteralPath $extractRoot -Filter TickRiver.exe -Recurse -File)
    Assert ($executable.Count -eq 1) 'Expected exactly one extracted application.'
    $extractedPayload = $executable[0].DirectoryName
    foreach ($file in $payload) {
        $relative = $file.FullName.Substring($PublishDirectory.Length + 1)
        $target = Join-Path $extractedPayload $relative
        Assert (Test-Path -LiteralPath $target -PathType Leaf) "Missing extracted file: $relative."
        Assert ((Get-FileHash -LiteralPath $file.FullName).Hash -eq (Get-FileHash -LiteralPath $target).Hash) "Extracted bytes differ: $relative."
    }
    Write-Host "Verified all $($payload.Count) extracted files against $PublishDirectory."
}
Write-Host "PASS: TickRiver MSI metadata, payload, shortcuts, UI navigation, per-user scope and upgrade/uninstall policy."

if ($PreviousMsi) {
    if (-not [IO.Path]::IsPathRooted($PreviousMsi)) { $PreviousMsi = Join-Path $repoRoot $PreviousMsi }
    $PreviousMsi = (Get-Item -LiteralPath $PreviousMsi).FullName
    $related = @($installer.RelatedProducts($properties.UpgradeCode))
    Assert ($related.Count -eq 0) 'Lifecycle tests require a user account with no TickRiver MSI installed. Use a test account or uninstall the MSI first.'
    $previousDatabase = $installer.OpenDatabase($PreviousMsi, 0)
    $view = $previousDatabase.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductCode''')
    $null = $view.Execute(); $previousCode = $view.Fetch().StringData(1); $null = $view.Close()
    $view = $previousDatabase.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductVersion''')
    $null = $view.Execute(); $previousVersion = $view.Fetch().StringData(1); $null = $view.Close()
    Assert ([Version]$previousVersion -lt [Version]$properties.ProductVersion) 'PreviousMsi must have an older product version.'
    $view = $previousDatabase.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''UpgradeCode''')
    $null = $view.Execute(); $previousUpgrade = $view.Fetch().StringData(1); $null = $view.Close()
    Assert ($previousUpgrade -eq $properties.UpgradeCode) 'PreviousMsi must belong to TickRiver.'
    $testRoot = Join-Path $repoRoot ('work\installer-lifecycle-' + [Guid]::NewGuid().ToString('N'))
    $oldFolder = Join-Path $testRoot 'previous'
    $newFolder = Join-Path $testRoot 'current'
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    # Add only a uniquely named fixture beside the user's data; never change the database or client configs.
    $dataFolder = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TickRiver'
    New-Item -ItemType Directory -Path $dataFolder -Force | Out-Null
    $sentinel = Join-Path $dataFolder ('installer-test-' + [Guid]::NewGuid().ToString('N') + '.txt')
    Set-Content -LiteralPath $sentinel -Value 'Installer must preserve user data.'
    $shortcutsBefore = @{}
    foreach ($folder in @([Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('Programs'))) {
        $link = Join-Path $folder 'TickRiver.lnk'
        $shortcutsBefore[$link] = if (Test-Path -LiteralPath $link) { (Get-FileHash -LiteralPath $link).Hash } else { '' }
    }
    function Invoke-Msi([string[]]$MsiArguments, [string]$LogName, [int]$ExpectedCode = 0) {
        $log = Join-Path $testRoot "$LogName.log"
        $process = Start-Process msiexec.exe -WindowStyle Hidden -PassThru -Wait -ArgumentList ($MsiArguments + @('/qn', '/norestart', '/l*v', "`"$log`""))
        Assert ($process.ExitCode -eq $ExpectedCode) "$LogName returned $($process.ExitCode); see $log."
    }
    $oldInstalled = $false; $newInstalled = $false
    try {
        Invoke-Msi @('/i', "`"$PreviousMsi`"", 'ADDLOCAL=ApplicationFeature', "INSTALLFOLDER=`"$oldFolder`"") 'install-previous'
        $oldInstalled = $true
        Assert (Test-Path -LiteralPath (Join-Path $oldFolder 'TickRiver.exe')) 'Initial installation did not deploy the UI.'
        $retained = Join-Path $oldFolder 'retained-user-file.txt'
        Set-Content -LiteralPath $retained -Value 'Unowned files must survive upgrade.'
        # This folder was created by this test, under the known work directory.
        Remove-Item -LiteralPath (Join-Path $oldFolder 'TickRiver.Mcp.dll')
        Invoke-Msi @('/fa', "`"$PreviousMsi`"") 'repair-previous'
        Assert (Test-Path -LiteralPath (Join-Path $oldFolder 'TickRiver.Mcp.dll')) 'Repair failed to restore a missing MCP assembly.'
        Invoke-Msi @('/i', "`"$Msi`"", 'ADDLOCAL=ApplicationFeature', "INSTALLFOLDER=`"$newFolder`"") 'upgrade'
        $newInstalled = $true; $oldInstalled = $false
        Assert (-not (Test-Path -LiteralPath (Join-Path $oldFolder 'TickRiver.exe'))) 'Upgrade left the older MSI application installed.'
        Assert (Test-Path -LiteralPath $retained) 'Upgrade removed an unowned file.'
        foreach ($file in $payload) {
            $target = Join-Path $newFolder $file.FullName.Substring($PublishDirectory.Length + 1)
            Assert ((Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash) "Upgrade changed payload bytes: $($file.Name)."
        }
        Invoke-Msi @('/i', "`"$PreviousMsi`"", 'ADDLOCAL=ApplicationFeature', "INSTALLFOLDER=`"$oldFolder`"") 'downgrade' 1603
        Assert ([bool](Select-String -LiteralPath (Join-Path $testRoot 'downgrade.log') -SimpleMatch 'A newer version of TickRiver is already installed.')) 'Downgrade failed for an unexpected reason.'
        Invoke-Msi @('/x', $properties.ProductCode) 'uninstall'
        $newInstalled = $false
        Assert (-not (Test-Path -LiteralPath (Join-Path $newFolder 'TickRiver.exe'))) 'Uninstall left an MSI-owned application file.'
        Assert (Test-Path -LiteralPath $sentinel) 'Uninstall removed user data.'
        Assert (@($installer.RelatedProducts($properties.UpgradeCode)).Count -eq 0) 'Test MSI is still registered.'
        foreach ($link in $shortcutsBefore.Keys) {
            $hash = if (Test-Path -LiteralPath $link) { (Get-FileHash -LiteralPath $link).Hash } else { '' }
            Assert ($hash -eq $shortcutsBefore[$link]) 'Shortcut-free lifecycle test changed an existing shortcut.'
        }
        Write-Host "PASS: install, missing-file repair, major upgrade, downgrade rejection, uninstall and retained user data. Logs: $testRoot"
    } finally {
        if ($newInstalled) { Invoke-Msi @('/x', $properties.ProductCode) 'cleanup-current' }
        if ($oldInstalled) { Invoke-Msi @('/x', $previousCode) 'cleanup-previous' }
        if (Test-Path -LiteralPath $sentinel) { Remove-Item -LiteralPath $sentinel }
    }
}
