param(
    [Parameter(Mandatory)] [string] $EngineDirectory,
    [string] $Compiler = 'g++.exe',
    [string] $MakeAppx,
    [string] $OutputDirectory,
    [string] $Version,
    [string] $SignTool,
    [string] $SigningCertificateThumbprint,
    [string] $TimestampUrl
)

$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectDir 'src\QuickZip.csproj'
if (!$Version) { $Version = ([xml][IO.File]::ReadAllText($projectFile)).Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') { throw 'Use a numeric version such as 0.4.1.' }
$packageVersion = [version]::Parse($Version)
if ($packageVersion.Revision -lt 0) { $packageVersion = [version]::new($packageVersion.Major, $packageVersion.Minor, $packageVersion.Build, 0) }
if (([bool]$SignTool -ne [bool]$SigningCertificateThumbprint) -or ($SignTool -and !$MakeAppx)) {
    throw 'Use -MakeAppx, -SignTool and -SigningCertificateThumbprint together to sign the package.'
}
$signingCertificate = $null
if ($SignTool) {
    if ($SigningCertificateThumbprint -notmatch '^[a-fA-F0-9]{40}$') { throw 'The signing certificate thumbprint is invalid.' }
    $signingCertificate = Get-Item -LiteralPath ("Cert:\CurrentUser\My\" + $SigningCertificateThumbprint)
    if (!$signingCertificate.HasPrivateKey) { throw 'The signing certificate must have an accessible private key.' }
}
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectDir 'dist' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$portable = Join-Path $OutputDirectory 'ZIP'
$assets = Join-Path $portable 'Assets'
$engine = Join-Path $portable 'engine'
New-Item -ItemType Directory -Force -Path $portable, $engine | Out-Null
& (Join-Path $PSScriptRoot 'Make-Assets.ps1') -Portrait (Join-Path $projectDir 'assets\ZIP-Icon-Source.png') -Assets $assets -Icon (Join-Path $projectDir 'src\ZIP.ico')

dotnet publish $projectFile -c Release -r win-x64 --self-contained false -o $portable "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

$cpp = Join-Path $projectDir 'shell\ExplorerCommand.cpp'
$dll = Join-Path $portable 'QuickZipShell.dll'
$compilerDirectory = Split-Path -Parent (Get-Command $Compiler -ErrorAction Stop).Source
$priorPath = $env:Path
try {
    $env:Path = "$compilerDirectory;$priorPath"
    & $Compiler -std=c++20 -O2 -s -Wall -Wextra -Wno-missing-field-initializers -finput-charset=UTF-8 -shared -static -static-libgcc -static-libstdc++ -o $dll $cpp -lole32 -lshlwapi -luuid
    if ($LASTEXITCODE -ne 0) { throw "Explorer command build failed ($LASTEXITCODE)." }
}
finally { $env:Path = $priorPath }

foreach ($file in '7z.exe','7z.dll','License.txt') {
    Copy-Item -LiteralPath (Join-Path $EngineDirectory $file) -Destination $engine -Force
}
foreach ($file in 'Shell-Integration.ps1','Enable-Context-Menu.cmd','Disable-Context-Menu.cmd') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $portable -Force
}
Copy-Item -LiteralPath (Join-Path $projectDir 'README.md') -Destination $portable -Force

$archive = Join-Path $OutputDirectory 'ZIP-win-x64.zip'
$portableItems = @('Assets', 'engine', 'ZIP.exe', 'ZIP.dll', 'ZIP.deps.json', 'ZIP.runtimeconfig.json',
    'QuickZipShell.dll', 'README.md', 'Shell-Integration.ps1', 'Enable-Context-Menu.cmd', 'Disable-Context-Menu.cmd')
Compress-Archive -LiteralPath ($portableItems | ForEach-Object { Join-Path $portable $_ }) -DestinationPath $archive -Force

if ($MakeAppx) {
    $bundle = Join-Path $OutputDirectory 'ZIP-win11-x64.msix'
    $manifest = Join-Path $OutputDirectory 'AppxManifest.build.xml'
    $xml = [xml][IO.File]::ReadAllText((Join-Path $projectDir 'shell\AppxManifest.xml'))
    $identity = $xml.DocumentElement.SelectSingleNode("*[local-name()='Identity']")
    $identity.SetAttribute('Version', $packageVersion.ToString())
    if ($signingCertificate) { $identity.SetAttribute('Publisher', $signingCertificate.Subject) }
    $xml.Save($manifest)
    $mapping = Join-Path $OutputDirectory 'AppxMapping.txt'
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('[Files]')
    $lines.Add('"{0}" "AppxManifest.xml"' -f $manifest)
    foreach ($item in Get-ChildItem -LiteralPath $portable -Recurse -File) {
        $relative = $item.FullName.Substring($portable.Length + 1)
        if ($relative -match '^(Assets|engine)\\' -or
            $relative -in 'ZIP.exe','ZIP.dll','ZIP.deps.json','ZIP.runtimeconfig.json','QuickZipShell.dll','README.md') {
            $lines.Add(('"{0}" "{1}"' -f $item.FullName, $relative))
        }
    }
    [IO.File]::WriteAllLines($mapping, $lines, [Text.UTF8Encoding]::new($true))
    & $MakeAppx pack /f $mapping /p $bundle /o
    if ($LASTEXITCODE -ne 0) { throw "MSIX packaging failed ($LASTEXITCODE)." }
    if ($signingCertificate) {
        $signArgs = @('sign', '/fd', 'SHA256', '/sha1', $SigningCertificateThumbprint)
        if ($TimestampUrl) { $signArgs += @('/tr', $TimestampUrl, '/td', 'SHA256') }
        & $SignTool @signArgs $bundle
        if ($LASTEXITCODE -ne 0) { throw "MSIX signing failed ($LASTEXITCODE)." }
        & $SignTool verify /pa $bundle
        if ($LASTEXITCODE -ne 0) { throw "MSIX signature verification failed ($LASTEXITCODE)." }
    }
    else {
        foreach ($file in "Install-ZIP.ps1", "Install-ZIP.cmd") {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $OutputDirectory -Force
        }
    }
}
