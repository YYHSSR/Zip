param([switch] $Remove)

$ErrorActionPreference = 'Stop'
$classes = 'HKCU:\Software\Classes'
$clsid = '{8E6570A7-BE80-4E33-803C-589F157E7B71}'
$menu = "$classes\AllFilesystemObjects\shell\QuickZip.Desktop"
$registration = "$classes\CLSID\$clsid"
$exe = Join-Path $PSScriptRoot 'ZIP.exe'

function Set-DefaultValue([string] $Path, [string] $Value) {
    New-Item -Path $Path -Force | Out-Null
    Set-Item -LiteralPath $Path -Value $Value
}

# File associations (ProgIDs, Open with, Settings > Default apps) are written by ZIP.exe itself.
function Invoke-Association([string] $Verb) {
    $process = Start-Process -FilePath $exe -ArgumentList $Verb -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "ZIP.exe $Verb failed ($($process.ExitCode))." }
}

if ($Remove) {
    if (Test-Path -LiteralPath $exe) { Invoke-Association '--unassociate' }
    foreach ($key in $menu, $registration) {
        if (Test-Path -LiteralPath $key) {
            Remove-Item -LiteralPath $key -Recurse -Force
        }
    }
}
else {
    $exe = (Get-Item -LiteralPath $exe).FullName
    $dll = (Get-Item -LiteralPath (Join-Path $PSScriptRoot 'QuickZipShell.dll')).FullName
    Set-DefaultValue "$registration\InprocServer32" $dll
    New-ItemProperty -LiteralPath "$registration\InprocServer32" -Name 'ThreadingModel' -Value 'Apartment' -Force | Out-Null
    Set-DefaultValue $menu 'ZIP'
    New-ItemProperty -LiteralPath $menu -Name 'ExplorerCommandHandler' -Value $clsid -Force | Out-Null
    New-ItemProperty -LiteralPath $menu -Name 'MultiSelectModel' -Value 'Player' -Force | Out-Null
    New-ItemProperty -LiteralPath $menu -Name 'Icon' -Value ('"' + $exe + '",0') -Force | Out-Null
    Invoke-Association '--associate'
}

if ($Remove) { Write-Output 'ZIP context menu and file associations removed.' }
else {
    Write-Output 'ZIP context menu and file associations enabled. Use Show more options on Windows 11.'
    Write-Output 'To open archives by double-click, run: ZIP.exe --set-default'
}
