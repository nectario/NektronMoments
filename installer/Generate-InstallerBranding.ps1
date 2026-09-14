#Requires -Version 5.1

[CmdletBinding()]
param([string] $RepoRoot)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

Add-Type -AssemblyName System.Drawing

$identityRoot = Join-Path $RepoRoot 'Brand_Images\NektronMoments_Complete_Brand_Package_v1_3'
$app256Path = Join-Path $identityRoot 'brand\app\light\png\256.png'
$app128Path = Join-Path $identityRoot 'brand\app\light\png\128.png'
$headerMarkPath = Join-Path $identityRoot 'brand\mark\light\png\128.png'
$setupIconSource = Join-Path $identityRoot 'brand\app\NektronMoments.ico'
$assetsRoot = Join-Path $PSScriptRoot 'assets'
$sharedRoot = Join-Path $PSScriptRoot 'shared'

foreach ($required in @($app256Path, $app128Path, $headerMarkPath, $setupIconSource)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required Nektron Moments identity asset is missing: $required"
    }
}

[void] [System.IO.Directory]::CreateDirectory($assetsRoot)
[void] [System.IO.Directory]::CreateDirectory($sharedRoot)

function New-GradientCanvas {
    param(
        [Parameter(Mandatory = $true)][int] $Width,
        [Parameter(Mandatory = $true)][int] $Height,
        [Parameter(Mandatory = $true)][System.Drawing.Color] $Start,
        [Parameter(Mandatory = $true)][System.Drawing.Color] $End
    )

    $bitmap = [System.Drawing.Bitmap]::new(
        $Width,
        $Height,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $rectangle = [System.Drawing.Rectangle]::new(0, 0, $Width, $Height)
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rectangle,
        $Start,
        $End,
        35.0)
    $graphics.FillRectangle($gradient, $rectangle)
    $gradient.Dispose()
    return @($bitmap, $graphics)
}

function Save-Png {
    param(
        [Parameter(Mandatory = $true)][System.Drawing.Bitmap] $Bitmap,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
}

function Draw-ImageUnscaledWithOpacity {
    param(
        [Parameter(Mandatory = $true)][System.Drawing.Graphics] $Graphics,
        [Parameter(Mandatory = $true)][System.Drawing.Image] $Image,
        [Parameter(Mandatory = $true)][int] $X,
        [Parameter(Mandatory = $true)][int] $Y,
        [ValidateRange(0.0, 1.0)][double] $Opacity
    )

    $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
    try {
        $matrix = [System.Drawing.Imaging.ColorMatrix]::new()
        $matrix.Matrix33 = [single] $Opacity
        $attributes.SetColorMatrix($matrix)
        $destination = [System.Drawing.Rectangle]::new($X, $Y, $Image.Width, $Image.Height)
        $Graphics.DrawImage(
            $Image,
            $destination,
            0,
            0,
            $Image.Width,
            $Image.Height,
            [System.Drawing.GraphicsUnit]::Pixel,
            $attributes)
    }
    finally {
        $attributes.Dispose()
    }
}

$app256 = [System.Drawing.Image]::FromFile($app256Path)
$app128 = [System.Drawing.Image]::FromFile($app128Path)
try {
    # Welcome/finish artwork: exact 256 px mark on a 480 × 918 native canvas.
    $wizardPair = New-GradientCanvas -Width 480 -Height 918 `
        -Start ([System.Drawing.ColorTranslator]::FromHtml('#F8FBFD')) `
        -End ([System.Drawing.ColorTranslator]::FromHtml('#EAF7FC'))
    $wizard = $wizardPair[0]
    $wizardGraphics = $wizardPair[1]
    try {
        $halo = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(86, 77, 200, 243))
        $pearl = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(242, 255, 255, 255))
        $titleBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#102739'))
        $bodyBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#4D6B80'))
        $accentBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#149FE7'))
        $titleFont = [System.Drawing.Font]::new('Segoe UI', 25, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $bodyFont = [System.Drawing.Font]::new('Segoe UI', 15, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $labelFont = [System.Drawing.Font]::new('Segoe UI', 14, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        try {
            $wizardGraphics.FillEllipse($halo, 74, 50, 332, 332)
            $wizardGraphics.FillEllipse($pearl, 82, 58, 316, 316)
            $wizardGraphics.DrawImageUnscaled($app256, 112, 88)
            $wizardGraphics.DrawString('Nektron Moments', $titleFont, $titleBrush, 44, 396)
            $wizardGraphics.DrawString('Find. Relive. Remember.', $bodyFont, $bodyBrush,
                [System.Drawing.RectangleF]::new(44, 444, 392, 70))
            $wizardGraphics.DrawString('YOUR MOMENTS, FOUND', $labelFont, $accentBrush, 44, 548)
            $wizardGraphics.DrawString("Photos and videos`nAn effortless view of your library`nLocal originals stay on your computer", $bodyFont, $bodyBrush,
                [System.Drawing.RectangleF]::new(44, 590, 390, 150))
            $wizardGraphics.FillRectangle($accentBrush, 44, 786, 122, 4)
            $wizardGraphics.DrawString('NektronAI Products', $bodyFont, $bodyBrush, 44, 812)
        }
        finally {
            foreach ($resource in @($halo, $pearl, $titleBrush, $bodyBrush, $accentBrush, $titleFont, $bodyFont, $labelFont)) {
                $resource.Dispose()
            }
        }
        Save-Png -Bitmap $wizard -Path (Join-Path $assetsRoot 'NektronMoments.WizardImage.Light.png')
    }
    finally {
        $wizardGraphics.Dispose()
        $wizard.Dispose()
    }

    # Use the approved transparent mark directly. It fills the header image area
    # instead of being reduced inside an opaque, padded square.
    Copy-Item -LiteralPath $headerMarkPath -Destination (Join-Path $assetsRoot 'NektronMoments.SmallImage.Light.png') -Force

    # Full installer atmosphere: the 256 px identity remains unscaled.
    $backPair = New-GradientCanvas -Width 1242 -Height 900 `
        -Start ([System.Drawing.ColorTranslator]::FromHtml('#F7FAFC')) `
        -End ([System.Drawing.ColorTranslator]::FromHtml('#E2F5FC'))
    $back = $backPair[0]
    $backGraphics = $backPair[1]
    try {
        $cyan = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(34, 77, 200, 243))
        $blue = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(23, 42, 97, 228))
        try {
            $backGraphics.FillEllipse($cyan, -140, 590, 500, 500)
            $backGraphics.FillEllipse($blue, 960, -180, 430, 430)
            Draw-ImageUnscaledWithOpacity -Graphics $backGraphics -Image $app256 -X 885 -Y 492 -Opacity 0.10
        }
        finally {
            $cyan.Dispose()
            $blue.Dispose()
        }
        Save-Png -Bitmap $back -Path (Join-Path $assetsRoot 'NektronMoments.BackImage.Light.png')
    }
    finally {
        $backGraphics.Dispose()
        $back.Dispose()
    }
}
finally {
    $app256.Dispose()
    $app128.Dispose()
}

Copy-Item -LiteralPath $setupIconSource -Destination (Join-Path $assetsRoot 'NektronMoments.SetupIcon.ico') -Force

$branding = @'
; Generated Nektron Moments installer identity and artwork paths.
#ifndef NektronMomentsPublisher
  #define NektronMomentsPublisher "Nektron, Inc."
#endif
#ifndef NektronMomentsPublisherUrl
  #define NektronMomentsPublisherUrl "https://nektron.ai"
#endif
#ifndef NektronMomentsSupportUrl
  #define NektronMomentsSupportUrl "https://nektron.ai"
#endif
#ifndef NektronMomentsUpdatesUrl
  #define NektronMomentsUpdatesUrl "https://nektron.ai"
#endif
#ifndef NektronMomentsSetupIconFile
  #define NektronMomentsSetupIconFile InstallerRoot + "\assets\NektronMoments.SetupIcon.ico"
#endif
#ifndef NektronMomentsBackImageFile
  #define NektronMomentsBackImageFile InstallerRoot + "\assets\NektronMoments.BackImage.Light.png"
#endif
#ifndef NektronMomentsWizardImageFile
  #define NektronMomentsWizardImageFile InstallerRoot + "\assets\NektronMoments.WizardImage.Light.png"
#endif
#ifndef NektronMomentsSmallImageFile
  #define NektronMomentsSmallImageFile InstallerRoot + "\assets\NektronMoments.SmallImage.Light.png"
#endif
'@
[System.IO.File]::WriteAllText(
    (Join-Path $sharedRoot 'NektronMoments.Branding.iss'),
    $branding,
    [System.Text.UTF8Encoding]::new($false))

Write-Host "Generated Nektron Moments installer branding in $assetsRoot"
