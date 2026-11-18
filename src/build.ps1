$ErrorActionPreference = 'Stop'

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$output = Join-Path (Split-Path -Parent $PSScriptRoot) 'EdgeCountdown.exe'
$icon = Join-Path ([IO.Path]::GetTempPath()) ('EdgeCountdown-' + [Guid]::NewGuid() + '.ico')

Add-Type -AssemblyName System.Drawing
$bitmap = [Drawing.Bitmap]::new(64, 64)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$background = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(29, 48, 48))
$clock = [Drawing.Pen]::new([Drawing.Color]::FromArgb(122, 230, 190), 4)
$shape = [Drawing.Drawing2D.GraphicsPath]::new()
$png = [IO.MemoryStream]::new()

try {
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $shape.AddArc(2, 2, 28, 28, 180, 90)
    $shape.AddArc(34, 2, 28, 28, 270, 90)
    $shape.AddArc(34, 34, 28, 28, 0, 90)
    $shape.AddArc(2, 34, 28, 28, 90, 90)
    $shape.CloseFigure()
    $graphics.FillPath($background, $shape)
    $clock.StartCap = $clock.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $clock.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    $graphics.DrawEllipse($clock, 15, 15, 34, 34)
    $graphics.DrawLines($clock, [Drawing.Point[]]@(
        [Drawing.Point]::new(32, 22), [Drawing.Point]::new(32, 32), [Drawing.Point]::new(39, 36)
    ))
    $bitmap.Save($png, [Drawing.Imaging.ImageFormat]::Png)

    $writer = [IO.BinaryWriter]::new([IO.File]::Create($icon))
    try {
        $writer.Write([byte[]]@(0, 0, 1, 0, 1, 0, 64, 64, 0, 0, 1, 0, 32, 0))
        $writer.Write([int]$png.Length)
        $writer.Write([int]22)
        $writer.Write($png.ToArray())
    } finally {
        $writer.Dispose()
    }

    $compilerArguments = @(
        '/nologo'
        '/target:winexe'
        '/optimize+'
        '/codepage:65001'
        "/out:$output"
        "/win32icon:$icon"
        "/win32manifest:$PSScriptRoot\countdown.manifest"
        "/reference:$framework\WPF\WindowsBase.dll"
        "/reference:$framework\WPF\PresentationCore.dll"
        "/reference:$framework\WPF\PresentationFramework.dll"
        "/reference:$framework\System.Xaml.dll"
        "/reference:$framework\System.Runtime.Serialization.dll"
        "/reference:$framework\System.Drawing.dll"
        "/reference:$framework\System.Windows.Forms.dll"
        "/resource:$PSScriptRoot\TimerWindow.xaml,TimerWindow.xaml"
        "$PSScriptRoot\Countdown.cs"
        "$PSScriptRoot\TimerPreferences.cs"
    )
    & $compiler @compilerArguments
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)." }
    Get-Item -LiteralPath $output
} finally {
    $graphics.Dispose()
    $bitmap.Dispose()
    $background.Dispose()
    $clock.Dispose()
    $shape.Dispose()
    $png.Dispose()
    if (Test-Path -LiteralPath $icon) { Remove-Item -LiteralPath $icon }
}
