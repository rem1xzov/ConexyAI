# App icons from the brand SVG (frontend/public/conexyai.svg).
# Chrome is used only as a rasterizer - it is already installed on the build machine.
# NOTE: keep this file ASCII-only; PowerShell reads BOM-less scripts as ANSI here.
$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Resolve-Path (Join-Path $here '..\..')
$svg = Join-Path $repo 'frontend\public\conexyai.svg'
$res = Join-Path $here 'app\src\main\res'
$tmp = Join-Path $here 'build\icons'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

$chrome = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $chrome) { throw 'Chrome or Edge is required to rasterize the SVG' }

$svgText = Get-Content $svg -Raw

# Legacy tile: dark square with the chevrons.
$tileHtml = Join-Path $tmp 'tile.html'
Set-Content -Path $tileHtml -Encoding UTF8 -Value "<html><body style='margin:0'>$svgText</body></html>"

# Adaptive foreground: chevrons only, scaled into the 66dp safe zone of the 108dp canvas.
$fgSvg = $svgText -replace '<rect[^>]*fill="#0a0a0a"[^>]*/>', '' `
                  -replace '(?s)<g fill="none"', '<g transform="translate(320,320) scale(0.58) translate(-320,-320)" fill="none"'
$fgHtml = Join-Path $tmp 'fg.html'
Set-Content -Path $fgHtml -Encoding UTF8 -Value "<html><body style='margin:0;background:transparent'>$fgSvg</body></html>"

function Render([string]$html, [string]$out) {
    $url = "file:///$($html -replace '\\','/')"
    Start-Process -FilePath $chrome -Wait -NoNewWindow -ArgumentList @(
        '--headless=new', '--disable-gpu', '--hide-scrollbars', '--window-size=1024,1024',
        '--default-background-color=00000000', "--screenshot=$out", $url
    ) -RedirectStandardError (Join-Path $tmp 'chrome.err') -RedirectStandardOutput (Join-Path $tmp 'chrome.out') | Out-Null
    if (-not (Test-Path $out)) { throw "Chrome did not render $out" }
}

Render $tileHtml (Join-Path $tmp 'tile.png')
Render $fgHtml (Join-Path $tmp 'fg.png')

Add-Type -AssemblyName System.Drawing

function Resize([string]$src, [string]$dst, [int]$size) {
    $img = [System.Drawing.Image]::FromFile($src)
    try {
        $bmp = New-Object System.Drawing.Bitmap($size, $size)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $g.Clear([System.Drawing.Color]::Transparent)
            $g.DrawImage($img, 0, 0, $size, $size)
        } finally { $g.Dispose() }
        $bmp.Save($dst, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
    } finally { $img.Dispose() }
}

# legacy: 48/72/96/144/192; adaptive foreground: 108dp -> 108/162/216/324/432.
$densities = @(
    @{ dir = 'mipmap-mdpi';    icon = 48;  fg = 108 },
    @{ dir = 'mipmap-hdpi';    icon = 72;  fg = 162 },
    @{ dir = 'mipmap-xhdpi';   icon = 96;  fg = 216 },
    @{ dir = 'mipmap-xxhdpi';  icon = 144; fg = 324 },
    @{ dir = 'mipmap-xxxhdpi'; icon = 192; fg = 432 }
)

foreach ($d in $densities) {
    $dir = Join-Path $res $d.dir
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Resize (Join-Path $tmp 'tile.png') (Join-Path $dir 'ic_launcher.png') $d.icon
    Resize (Join-Path $tmp 'tile.png') (Join-Path $dir 'ic_launcher_round.png') $d.icon
    Resize (Join-Path $tmp 'fg.png') (Join-Path $dir 'ic_launcher_foreground.png') $d.fg
    Write-Output "$($d.dir): $($d.icon)px"
}

# Adaptive icon background - the brand dark colour.
$bgFile = Join-Path $res 'values\ic_launcher_background.xml'
@'
<?xml version="1.0" encoding="utf-8"?>
<resources>
    <color name="ic_launcher_background">#0A0A0A</color>
</resources>
'@ | Set-Content -Path $bgFile -Encoding UTF8

Write-Output 'ICONS OK'
