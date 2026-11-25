param([switch]$DesktopChecks)

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$output = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$src = Join-Path $PSScriptRoot 'src'
$wpf = Join-Path $framework 'WPF'
$exe = Join-Path $output 'WorkDesk.exe'
$sources = (Get-ChildItem -LiteralPath $src -Filter '*.cs' -File).FullName
$references = @(
    '/r:System.Runtime.Serialization.dll'
    "/r:$wpf\WindowsBase.dll"
    "/r:$wpf\PresentationCore.dll"
    "/r:$wpf\PresentationFramework.dll"
    "/r:$framework\System.Xaml.dll"
)
$resources = @(
    "/resource:$src\Theme.xaml,WorkDesk.Theme.xaml"
    "/resource:$src\Shell.xaml,WorkDesk.Shell.xaml"
)
$target = '/target:winexe'
$entry = '/main:WorkDesk.Program'
if ($DesktopChecks) {
    $exe = Join-Path $output 'DesktopChecks.exe'
    $target = '/target:exe'
    $entry = '/main:WorkDesk.DesktopChecks'
    $sources = @($sources) + (Join-Path $PSScriptRoot 'tests\DesktopChecks.cs')
}
& (Join-Path $framework 'csc.exe') /nologo /utf8output /codepage:65001 $target $entry /optimize+ "/out:$exe" $references $resources $sources
if ($LASTEXITCODE -ne 0) { throw 'WorkDesk compilation failed.' }
Get-Item -LiteralPath $exe
