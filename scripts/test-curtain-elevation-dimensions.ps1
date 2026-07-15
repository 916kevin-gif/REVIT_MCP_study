param(
    [double]$ToleranceMm = 1.0
)

$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw "FAIL: $Message"
    }
}

function Normalize-CoordinatesMm {
    param(
        [double[]]$Values,
        [double]$Tolerance
    )

    $sorted = $Values | Sort-Object
    $result = New-Object System.Collections.Generic.List[double]

    foreach ($value in $sorted) {
        if ($result.Count -eq 0 -or [Math]::Abs($value - $result[$result.Count - 1]) -gt $Tolerance) {
            $result.Add($value)
        }
    }

    return $result.ToArray()
}

function Point2D {
    param(
        [double]$OriginX,
        [double]$OriginY,
        [double]$X,
        [double]$Y
    )

    return [pscustomobject]@{
        X = $OriginX + $X
        Y = $OriginY + $Y
    }
}

$cases = @()

$cases += {
    $minX = 0.0
    $maxX = 5000.0
    $minY = 0.0
    $maxY = 3200.0
    $offset = 300.0

    $start = Point2D 0 0 $minX ($maxY + $offset)
    $end = Point2D 0 0 $maxX ($maxY + $offset)

    Assert-True ($start.Y -gt $maxY) "總寬 dimension line 必須在 crop 上方"
    Assert-True ($end.Y -gt $maxY) "總寬 dimension line 終點必須在 crop 上方"
    Assert-True ([Math]::Abs($start.X - $minX) -lt 0.001) "總寬起點 X 必須等於 minX"
    Assert-True ([Math]::Abs($end.X - $maxX) -lt 0.001) "總寬終點 X 必須等於 maxX"
}

$cases += {
    $minX = -100.0
    $maxX = 4900.0
    $minY = 250.0
    $maxY = 3450.0
    $offset = 300.0

    $start = Point2D 0 0 ($maxX + $offset) $minY
    $end = Point2D 0 0 ($maxX + $offset) $maxY

    Assert-True ($start.X -gt $maxX) "總高 dimension line 必須在 crop 右側"
    Assert-True ($end.X -gt $maxX) "總高 dimension line 終點必須在 crop 右側"
    Assert-True ([Math]::Abs($start.Y - $minY) -lt 0.001) "總高起點 Y 必須等於 minY"
    Assert-True ([Math]::Abs($end.Y - $maxY) -lt 0.001) "總高終點 Y 必須等於 maxY"
}

$cases += {
    $values = @(0.0, 1000.0, 1000.4, 2000.0, 2000.9, 3500.0)
    $normalized = Normalize-CoordinatesMm $values $ToleranceMm

    Assert-True ($normalized.Count -eq 4) "grid coordinates 需用 1mm tolerance 去重"
    Assert-True ([Math]::Abs($normalized[1] - 1000.0) -lt 0.001) "去重後需保留第一個 1000mm 座標"
}

$cases += {
    $sourceOriginX = 1200.0
    $sourceOriginY = -500.0
    $dimensionOriginX = 1000.0
    $dimensionOriginY = -800.0
    $cropMinX = 0.0
    $cropMaxX = 4000.0
    $cropMinY = 0.0
    $cropMaxY = 3000.0

    $xShift = $sourceOriginX - $dimensionOriginX
    $yShift = $sourceOriginY - $dimensionOriginY
    $minX = $cropMinX + $xShift
    $maxX = $cropMaxX + $xShift
    $minY = $cropMinY + $yShift
    $maxY = $cropMaxY + $yShift

    Assert-True ([Math]::Abs($minX - 200.0) -lt 0.001) "frame X shift 應被套用到 minX"
    Assert-True ([Math]::Abs($maxX - 4200.0) -lt 0.001) "frame X shift 應被套用到 maxX"
    Assert-True ([Math]::Abs($minY - 300.0) -lt 0.001) "frame Y shift 應被套用到 minY"
    Assert-True ([Math]::Abs($maxY - 3300.0) -lt 0.001) "frame Y shift 應被套用到 maxY"
}

$index = 0
foreach ($case in $cases) {
    $index++
    & $case
    Write-Host "PASS case $index"
}

Write-Host "PASS curtain elevation dimension geometry tests"
