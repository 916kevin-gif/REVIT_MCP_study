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

function Resolve-DimensionSpacingMm {
    param(
        [Nullable[double]]$WitnessLineLengthPaperMm,
        [int]$ViewScale,
        [double]$InnerFallbackMm = 300.0,
        [double]$StackFallbackMm = 250.0,
        [bool]$StorageTypeIsDouble = $true
    )

    if ($StorageTypeIsDouble -and $null -ne $WitnessLineLengthPaperMm -and [double]$WitnessLineLengthPaperMm -gt 0.0 -and $ViewScale -gt 0) {
        return [pscustomobject]@{
            InnerValue = ([double]$WitnessLineLengthPaperMm + 3.0) * $ViewScale
            InnerSource = "dimension_type_witness_line_length_plus_3_mm"
            StackValue = [double]$WitnessLineLengthPaperMm * $ViewScale
            StackSource = "dimension_type_witness_line_length"
        }
    }

    return [pscustomobject]@{
        InnerValue = $(if ($InnerFallbackMm -gt 0.0) { $InnerFallbackMm } else { 300.0 })
        InnerSource = "parameter_fallback"
        StackValue = $(if ($StackFallbackMm -gt 0.0) { $StackFallbackMm } else { 250.0 })
        StackSource = "parameter_fallback"
    }
}

$cases = @()

$cases += {
    $minX = 0.0
    $maxX = 5000.0
    $minY = 0.0
    $maxY = 3200.0
    $offset = 300.0
    $stackOffset = 250.0

    $gridY = $maxY + $offset
    $totalY = $gridY + $stackOffset
    $start = Point2D 0 0 $minX $totalY
    $end = Point2D 0 0 $maxX $totalY
    Assert-True ($totalY -gt $gridY) "curtain elevation dimension assertion failed"
    Assert-True ([Math]::Abs(($gridY - $maxY) - $offset) -lt 0.001) "curtain elevation dimension assertion failed"
    Assert-True ([Math]::Abs(($totalY - $gridY) - $stackOffset) -lt 0.001) "curtain elevation dimension assertion failed"

    Assert-True ($start.Y -gt $maxY) "curtain elevation dimension assertion failed"
    Assert-True ($end.Y -gt $maxY) "curtain elevation dimension assertion failed"
    Assert-True ([Math]::Abs($start.X - $minX) -lt 0.001) "curtain elevation dimension assertion failed"
    Assert-True ([Math]::Abs($end.X - $maxX) -lt 0.001) "curtain elevation dimension assertion failed"
}

$cases += {
    $minX = -100.0
    $maxX = 4900.0
    $minY = 250.0
    $maxY = 3450.0
    $offset = 300.0
    $stackOffset = 250.0

    $gridX = $maxX + $offset
    $totalX = $gridX + $stackOffset
    $start = Point2D 0 0 $totalX $minY
    Assert-True ($totalX -gt $gridX) "curtain elevation dimension assertion failed"
    Assert-True ([Math]::Abs(($gridX - $maxX) - $offset) -lt 0.001) "curtain elevation dimension assertion failed"
    Assert-True ([Math]::Abs(($totalX - $gridX) - $stackOffset) -lt 0.001) "curtain elevation dimension assertion failed"
    $end = Point2D 0 0 $totalX $maxY

    Assert-True ($start.X -gt $maxX) "curtain elevation dimension assertion failed"
    Assert-True ($end.X -gt $maxX) "curtain elevation dimension assertion failed"
    Assert-True ([Math]::Abs($start.Y - $minY) -lt 0.001) "curtain elevation dimension assertion failed"
    Assert-True ([Math]::Abs($end.Y - $maxY) -lt 0.001) "curtain elevation dimension assertion failed"
}

$cases += {
    $values = @(0.0, 1000.0, 1000.4, 2000.0, 2000.9, 3500.0)
    $normalized = Normalize-CoordinatesMm $values $ToleranceMm

    Assert-True ($normalized.Count -eq 4) "curtain elevation dimension assertion failed"
    Assert-True ([Math]::Abs($normalized[1] - 1000.0) -lt 0.001) "curtain elevation dimension assertion failed"
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

    Assert-True ([Math]::Abs($minX - 200.0) -lt 0.001) "curtain elevation dimension assertion failed"
    Assert-True ([Math]::Abs($maxX - 4200.0) -lt 0.001) "curtain elevation dimension assertion failed"
    Assert-True ([Math]::Abs($minY - 300.0) -lt 0.001) "curtain elevation dimension assertion failed"
    Assert-True ([Math]::Abs($maxY - 3300.0) -lt 0.001) "curtain elevation dimension assertion failed"

    $offset = 300.0
    $stackOffset = 250.0
    $gridY = $maxY + $offset
    $totalY = $gridY + $stackOffset
    $gridX = $maxX + $offset
    $totalX = $gridX + $stackOffset

    Assert-True ($totalY -gt $gridY) "curtain elevation dimension assertion failed"
    Assert-True ($totalX -gt $gridX) "curtain elevation dimension assertion failed"
    Assert-True ([Math]::Abs(($totalY - $gridY) - $stackOffset) -lt 0.001) "curtain elevation dimension assertion failed"
    Assert-True ([Math]::Abs(($totalX - $gridX) - $stackOffset) -lt 0.001) "curtain elevation dimension assertion failed"
}

$cases += {
    $maxY = 3200.0
    $maxX = 5000.0
    $offset = 300.0
    $stackOffset = 250.0

    $totalYWithoutGrid = $maxY + $offset + $stackOffset
    $totalXWithoutGrid = $maxX + $offset + $stackOffset

    Assert-True ([Math]::Abs($totalYWithoutGrid - 3750.0) -lt 0.001) "curtain elevation dimension assertion failed"
    Assert-True ([Math]::Abs($totalXWithoutGrid - 5550.0) -lt 0.001) "curtain elevation dimension assertion failed"
}

$cases += {
    $resolved = Resolve-DimensionSpacingMm 3.0 50
    Assert-True ([Math]::Abs($resolved.InnerValue - 300.0) -lt 0.001) "3 mm witness line plus 3 mm at 1:50 should resolve inner offset to 300 mm"
    Assert-True ([Math]::Abs($resolved.StackValue - 150.0) -lt 0.001) "3 mm witness line at 1:50 should resolve stack offset to 150 mm"
    Assert-True ($resolved.InnerSource -eq "dimension_type_witness_line_length_plus_3_mm") "valid witness line length should drive inner offset"
    Assert-True ($resolved.StackSource -eq "dimension_type_witness_line_length") "valid witness line length should drive stack offset"

    $gridY = 3200.0 + $resolved.InnerValue
    $totalY = $gridY + $resolved.StackValue
    $gridX = 5000.0 + $resolved.InnerValue
    $totalX = $gridX + $resolved.StackValue
    Assert-True ([Math]::Abs($gridY - 3500.0) -lt 0.001) "top grid line should use resolved inner offset"
    Assert-True ([Math]::Abs($totalY - 3650.0) -lt 0.001) "top total line should remain outside grid line"
    Assert-True ([Math]::Abs($gridX - 5300.0) -lt 0.001) "right grid line should use resolved inner offset"
    Assert-True ([Math]::Abs($totalX - 5450.0) -lt 0.001) "right total line should remain outside grid line"

    $resolved = Resolve-DimensionSpacingMm 2.5 100
    Assert-True ([Math]::Abs($resolved.InnerValue - 550.0) -lt 0.001) "2.5 mm witness line plus 3 mm at 1:100 should resolve inner offset to 550 mm"
    Assert-True ([Math]::Abs($resolved.StackValue - 250.0) -lt 0.001) "2.5 mm witness line at 1:100 should resolve stack offset to 250 mm"
}

$cases += {
    $missing = Resolve-DimensionSpacingMm $null 50 325.0 275.0
    $zero = Resolve-DimensionSpacingMm 0.0 50 325.0 275.0
    $negative = Resolve-DimensionSpacingMm -2.0 50 325.0 275.0
    $invalidScale = Resolve-DimensionSpacingMm 3.0 0 325.0 275.0
    $wrongStorageType = Resolve-DimensionSpacingMm 3.0 50 325.0 275.0 $false
    $invalidFallback = Resolve-DimensionSpacingMm $null 50 -1.0 -1.0

    Assert-True ($missing.InnerSource -eq "parameter_fallback") "missing witness line length should use inner fallback"
    Assert-True ($missing.StackSource -eq "parameter_fallback") "missing witness line length should use stack fallback"
    Assert-True ([Math]::Abs($missing.InnerValue - 325.0) -lt 0.001) "missing witness line length should preserve explicit inner fallback"
    Assert-True ([Math]::Abs($missing.StackValue - 275.0) -lt 0.001) "missing witness line length should preserve explicit stack fallback"
    Assert-True ($zero.InnerSource -eq "parameter_fallback") "zero witness line length should use fallback"
    Assert-True ($negative.InnerSource -eq "parameter_fallback") "negative witness line length should use fallback"
    Assert-True ($invalidScale.InnerSource -eq "parameter_fallback") "invalid view scale should use fallback"
    Assert-True ($wrongStorageType.InnerSource -eq "parameter_fallback") "wrong StorageType should use fallback"
    Assert-True ([Math]::Abs($invalidFallback.InnerValue - 300.0) -lt 0.001) "invalid inner fallback should use default 300 mm"
    Assert-True ([Math]::Abs($invalidFallback.StackValue - 250.0) -lt 0.001) "invalid stack fallback should use default 250 mm"
}

$index = 0
foreach ($case in $cases) {
    $index++
    & $case
    Write-Host "PASS case $index"
}

Write-Host "PASS curtain elevation dimension geometry tests"
