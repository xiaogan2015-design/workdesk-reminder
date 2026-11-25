param([switch]$DesktopChecks)

$ErrorActionPreference = 'Stop'

function Write-WorkDeskIcon([string]$Path) {
    Add-Type -AssemblyName System.Drawing
    $tiles = foreach ($size in 16, 32, 48, 256) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size)
        $drawing = [System.Drawing.Graphics]::FromImage($bitmap)
        $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $purple = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.Rectangle]::new(0, 0, 64, 64),
            [System.Drawing.ColorTranslator]::FromHtml('#39236F'),
            [System.Drawing.ColorTranslator]::FromHtml('#7854EE'), [single]48)
        $check = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [single]6.5)
        $buffer = [IO.MemoryStream]::new()
        try {
            $drawing.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $drawing.ScaleTransform([single]($size / 64.0), [single]($size / 64.0))
            $shape.AddArc(2, 2, 28, 28, 180, 90)
            $shape.AddArc(34, 2, 28, 28, 270, 90)
            $shape.AddArc(34, 34, 28, 28, 0, 90)
            $shape.AddArc(2, 34, 28, 28, 90, 90)
            $shape.CloseFigure()
            $drawing.FillPath($purple, $shape)
            $check.StartCap = $check.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $check.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $drawing.DrawLines($check, [System.Drawing.Point[]]@(
                [System.Drawing.Point]::new(18, 33), [System.Drawing.Point]::new(28, 43), [System.Drawing.Point]::new(47, 23)))
            $bitmap.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
            [pscustomobject]@{ Size = $size; Bytes = $buffer.ToArray() }
        }
        finally {
            $buffer.Dispose()
            $check.Dispose()
            $purple.Dispose()
            $shape.Dispose()
            $drawing.Dispose()
            $bitmap.Dispose()
        }
    }

    $writer = [IO.BinaryWriter]::new([IO.File]::Create($Path))
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$tiles.Count)
        $offset = 6 + 16 * $tiles.Count
        foreach ($tile in $tiles) {
            $writer.Write([byte]($tile.Size % 256))
            $writer.Write([byte]($tile.Size % 256))
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$tile.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $tile.Bytes.Length
        }
        foreach ($tile in $tiles) { $writer.Write([byte[]]$tile.Bytes) }
    }
    finally { $writer.Dispose() }
}

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
    '/r:System.IO.Compression.dll'
    '/r:System.Drawing.dll'
    '/r:System.Windows.Forms.dll'
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
$icon = [IO.Path]::ChangeExtension($exe, '.ico')
Write-WorkDeskIcon $icon
& (Join-Path $framework 'csc.exe') /nologo /utf8output /codepage:65001 $target $entry /optimize+ "/out:$exe" `
    "/win32icon:$icon" "/win32manifest:$src\WorkDesk.manifest" $references $resources $sources
if ($LASTEXITCODE -ne 0) { throw 'WorkDesk compilation failed.' }
Get-Item -LiteralPath $exe
