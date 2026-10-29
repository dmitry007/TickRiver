param(
    [ValidateSet('All', 'Publish', 'Package')]
    [string]$Stage = 'All',
    [string]$DotnetPath,
    [string]$WixBin,
    [string]$PublishDirectory = 'artifacts\installer\payload',
    [string]$OutputDirectory = 'artifacts\installer',
    [string]$ProductVersion,
    [switch]$RequireSignedPayload
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not [IO.Path]::IsPathRooted($PublishDirectory)) { $PublishDirectory = Join-Path $repoRoot $PublishDirectory }
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path $repoRoot $OutputDirectory }
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory).TrimEnd('\')
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if ($OutputDirectory.Equals($PublishDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $OutputDirectory.StartsWith($PublishDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be outside PublishDirectory.'
}
if (-not $ProductVersion) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props')
    $ProductVersion = $props.Project.PropertyGroup.Version
}
if ($ProductVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'MSI versions must have three numeric components.' }
$version = [Version]$ProductVersion
if ($version.Major -gt 255 -or $version.Minor -gt 255 -or $version.Build -gt 65535) { throw 'Version exceeds Windows Installer limits.' }

function Invoke-Tool([string]$Executable, [string[]]$ToolArguments) {
    & $Executable @ToolArguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

if ($Stage -ne 'Package') {
    if (-not $DotnetPath) {
        $DotnetPath = Join-Path $repoRoot 'work\tools\dotnet\dotnet.exe'
        if (-not (Test-Path -LiteralPath $DotnetPath)) { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
    }
    # Only disposable build directories may be recursively cleared, never source or an installation.
    $allowedRoots = @('artifacts', 'work') | ForEach-Object { (Join-Path $repoRoot $_) + '\' }
    if (-not ($allowedRoots | Where-Object { $PublishDirectory.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })) {
        throw 'PublishDirectory must be a subdirectory of this repository\artifacts or repository\work.'
    }
    if (Test-Path -LiteralPath $PublishDirectory) { Remove-Item -LiteralPath $PublishDirectory -Recurse -Force }
    New-Item -ItemType Directory -Path $PublishDirectory -Force | Out-Null
    Push-Location $repoRoot
    try {
        foreach ($project in @('src\TickRiver\TickRiver.csproj', 'src\TickRiver.Mcp\TickRiver.Mcp.csproj')) {
            Invoke-Tool $DotnetPath @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
                "-p:Version=$ProductVersion", "-p:PathMap=$repoRoot=/_/TickRiver", '-o', $PublishDirectory)
        }
    } finally { Pop-Location }
    if ($Stage -eq 'Publish') { Write-Host "Published TickRiver $ProductVersion to $PublishDirectory"; return }
}

if (-not $WixBin) {
    if ($env:WIX) { $WixBin = Join-Path $env:WIX 'bin' }
    else {
        $WixBin = Get-ChildItem -Path "${env:ProgramFiles(x86)}\WiX Toolset v3.*\bin" -Directory |
            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
}
foreach ($tool in @('candle.exe', 'light.exe', 'WixUIExtension.dll', 'WixUtilExtension.dll')) {
    if (-not $WixBin -or -not (Test-Path -LiteralPath (Join-Path $WixBin $tool))) { throw "Install WiX Toolset 3 or pass -WixBin (missing $tool)." }
}
$requiredFiles = @('TickRiver.exe', 'TickRiver.Mcp.exe', 'TickRiver.dll', 'TickRiver.Mcp.dll', 'TickRiver.Core.dll',
    'TickRiver.pdb', 'TickRiver.Mcp.pdb', 'TickRiver.Core.pdb', 'coreclr.dll', 'hostfxr.dll',
    'TickRiver.runtimeconfig.json', 'TickRiver.Mcp.runtimeconfig.json')
foreach ($name in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $name) -PathType Leaf)) { throw "Incomplete self-contained payload: $name is missing." }
}
foreach ($name in @('TickRiver', 'TickRiver.Mcp', 'TickRiver.Core')) {
    $assembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $PublishDirectory "$name.dll"))
    if ($assembly.Version.ToString(3) -ne $ProductVersion) { throw "Payload $name version does not match MSI version $ProductVersion." }
    if ($RequireSignedPayload) {
        $signedFiles = @("$name.dll")
        if ($name -ne 'TickRiver.Core') { $signedFiles += "$name.exe" }
        foreach ($file in $signedFiles) {
            $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $PublishDirectory $file)
            if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate) { throw "Missing valid timestamped signature: $file." }
        }
    }
}
$files = @(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File | Sort-Object FullName)
if ($files | Where-Object { $_.Name -match '(?i)\.(sqlite(?:3)?(?:-(?:wal|shm))?|db|pfx|p12|pem|key|cer|crt|log|bak)$|^\.env($|\.)' }) {
    throw 'Payload contains data, a certificate, a key, or a log. Publish to a clean build directory.'
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$intermediate = Join-Path $OutputDirectory 'obj'
New-Item -ItemType Directory -Path $intermediate -Force | Out-Null
$namespace = 'http://schemas.microsoft.com/wix/2006/wi'
$document = New-Object Xml.XmlDocument
$wix = $document.CreateElement('Wix', $namespace); $document.AppendChild($wix) | Out-Null
$fragment = $document.CreateElement('Fragment', $namespace); $wix.AppendChild($fragment) | Out-Null
function Add-Element($Parent, [string]$Name, [hashtable]$Attributes) {
    $element = $document.CreateElement($Name, $namespace)
    foreach ($key in $Attributes.Keys) { $element.SetAttribute($key, [string]$Attributes[$key]) }
    $Parent.AppendChild($element) | Out-Null
    return $element
}
function Get-Id([string]$Prefix, [string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Path.ToLowerInvariant())) }
    finally { $sha.Dispose() }
    return $Prefix + ([BitConverter]::ToString($hash)).Replace('-', '').Substring(0, 32)
}
$directories = @{ '' = 'INSTALLFOLDER' }
$directoryNodes = @{ '' = (Add-Element $fragment 'DirectoryRef' @{ Id = 'INSTALLFOLDER' }) }
$group = Add-Element $fragment 'ComponentGroup' @{ Id = 'PublishedFiles' }
foreach ($file in $files) {
    $relative = $file.FullName.Substring($PublishDirectory.Length + 1)
    $parent = [IO.Path]::GetDirectoryName($relative)
    $current = ''
    foreach ($part in ($parent -split '\\' | Where-Object { $_ })) {
        $next = if ($current) { "$current\$part" } else { $part }
        if (-not $directories.ContainsKey($next)) {
            $directories[$next] = Get-Id 'Dir' $next
            $directoryNodes[$next] = Add-Element $directoryNodes[$current] 'Directory' @{ Id = $directories[$next]; Name = $part }
            $component = Add-Element $group 'Component' @{ Id = (Get-Id 'Folder' $next); Guid = '*'; Directory = $directories[$next]; Win64 = 'yes' }
            Add-Element $component 'RemoveFolder' @{ Id = (Get-Id 'Remove' $next); On = 'uninstall' } | Out-Null
            Add-Element $component 'RegistryValue' @{ Root = 'HKCU'; Key = 'Software\TickRiver\Installer\$(var.ProductVersion)';
                Name = (Get-Id 'Folder' $next); Type = 'integer'; Value = '1'; KeyPath = 'yes' } | Out-Null
        }
        $current = $next
    }
    $id = Get-Id 'File' $relative
    $componentGuid = ([Guid](Get-Id '' "TickRiver|$ProductVersion|$relative")).ToString()
    $component = Add-Element $group 'Component' @{ Id = $id; Guid = $componentGuid; Directory = $directories[$parent]; Win64 = 'yes' }
    $fileId = if ($relative -eq 'TickRiver.exe') { 'TickRiverExe' } else { $id }
    Add-Element $component 'File' @{ Id = $fileId; Source = '$(var.PublishDir)\' + $relative; KeyPath = 'no' } | Out-Null
    Add-Element $component 'RegistryValue' @{ Root = 'HKCU'; Key = 'Software\TickRiver\Installer\$(var.ProductVersion)';
        Name = $id; Type = 'integer'; Value = '1'; KeyPath = 'yes' } | Out-Null
}
$generated = Join-Path $intermediate 'Files.wxs'
$document.Save($generated)
$extensions = @('-ext', (Join-Path $WixBin 'WixUIExtension.dll'), '-ext', (Join-Path $WixBin 'WixUtilExtension.dll'))
Invoke-Tool (Join-Path $WixBin 'candle.exe') (@('-nologo', '-wx', '-arch', 'x64', "-dProductVersion=$ProductVersion",
    "-dPublishDir=$PublishDirectory", "-dRepoRoot=$repoRoot\", '-out', "$intermediate\",
    (Join-Path $repoRoot 'installer\Product.wxs'), $generated) + $extensions)
$msi = Join-Path $OutputDirectory "TickRiver-$ProductVersion-win-x64.msi"
# ICE91 warns that profile files cannot serve an ALLUSERS install. This package is exclusively per user.
# ICE60 warns about the vendor's versioned, language-neutral e_sqlite3.dll, whose metadata is left intact.
# ICE61 warns about the intentional same-version rebuild upgrade policy.
Invoke-Tool (Join-Path $WixBin 'light.exe') (@('-nologo', '-wx', '-sice:ICE91', '-sice:ICE60', '-sice:ICE61', '-cultures:en-us', '-out', $msi,
    (Join-Path $intermediate 'Product.wixobj'), (Join-Path $intermediate 'Files.wixobj')) + $extensions)
Write-Host "Created $msi ($($files.Count) payload files)."
