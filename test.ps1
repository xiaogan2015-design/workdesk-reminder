$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$output = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$runner = Join-Path $output 'StateChecks.exe'
& (Join-Path $framework 'csc.exe') /nologo /utf8output /codepage:65001 /target:exe "/out:$runner" `
    /r:System.Runtime.Serialization.dll (Join-Path $PSScriptRoot 'src\WorkState.cs') `
    (Join-Path $PSScriptRoot 'tests\StateChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'State check compilation failed.' }
& $runner
if ($LASTEXITCODE -ne 0) { throw 'State checks failed.' }
$desktop = & (Join-Path $PSScriptRoot 'build.ps1') -DesktopChecks
& $desktop.FullName
if ($LASTEXITCODE -ne 0) { throw 'Desktop checks failed.' }
