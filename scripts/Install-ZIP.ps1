param([switch] $Elevated)

$ErrorActionPreference = 'Stop'
$package = Join-Path $PSScriptRoot 'ZIP-win11-x64.msix'
if (!(Test-Path -LiteralPath $package -PathType Leaf)) {
    throw "Missing Windows 11 package: $package"
}
if (!$Elevated) {
    $shell = Join-Path $PSHOME 'powershell.exe'
    $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"{0}"' -f $PSCommandPath),'-Elevated')
    $process = Start-Process -FilePath $shell -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installation failed ($($process.ExitCode))." }
    Get-AppxPackage -Name 'QuickZip.Desktop' | Select-Object Name, Version, Status
    return
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $manifest = $archive.GetEntry('AppxManifest.xml')
    if (!$manifest) { throw 'The package does not contain AppxManifest.xml.' }
    $reader = [IO.StreamReader]::new($manifest.Open())
    try { $xml = [xml]$reader.ReadToEnd() }
    finally { $reader.Dispose() }
    $identity = $xml.DocumentElement.SelectSingleNode("*[local-name()='Identity']")
}
finally { $archive.Dispose() }
$name = $identity.GetAttribute('Name')
$publisher = $identity.GetAttribute('Publisher')
$version = [version]$identity.GetAttribute('Version')
if ($name -ne 'QuickZip.Desktop' -or $publisher -notmatch '2\.25\.311729368913984317654407730594956997722') {
    throw 'This script is for the unsigned ZIP test package.'
}
Add-AppxPackage -Path $package -AllowUnsigned -ErrorAction Stop
$installed = Get-AppxPackage -Name $name | Where-Object Publisher -eq $publisher
if (!$installed -or $installed.Version -ne $version) {
    throw "ZIP $version could not be verified after installation."
}
