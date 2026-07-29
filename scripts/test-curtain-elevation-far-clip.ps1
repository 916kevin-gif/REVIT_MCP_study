param(
    [double]$MarginMm = 50.0,
    [double]$ToleranceMm = 1.0
)

$ErrorActionPreference = "Stop"

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw "FAIL: $Message"
    }
}

function Get-FarClipDepth {
    param(
        [double[]]$DepthsMm,
        [double]$FallbackDepthMm
    )

    if ($null -eq $DepthsMm -or $DepthsMm.Count -eq 0) {
        return [pscustomobject]@{
            DepthMm = [Math]::Max($FallbackDepthMm, $MarginMm)
            Method = "fallback_depth_no_target_points"
        }
    }

    $positive = @($DepthsMm | Where-Object { $_ -gt 0 })
    if ($positive.Count -gt 0) {
        return [pscustomobject]@{
            DepthMm = [Math]::Max(($positive | Measure-Object -Maximum).Maximum + $MarginMm, $MarginMm)
            Method = "view_origin_to_target_max_depth"
        }
    }

    $absolute = @($DepthsMm | ForEach-Object { [Math]::Abs($_) })
    return [pscustomobject]@{
        DepthMm = [Math]::Max(($absolute | Measure-Object -Maximum).Maximum + $MarginMm, $MarginMm)
        Method = "view_origin_to_target_abs_depth_fallback"
    }
}

function Test-FarClipReadback {
    param(
        [double]$RequestedMm,
        [object]$ActualMm,
        [object]$Active,
        [object]$Mode
    )

    if ($null -eq $ActualMm) {
        return $false
    }

    return [Math]::Abs($RequestedMm - [double]$ActualMm) -le $ToleranceMm -and
        ($null -eq $Active -or [int]$Active -eq 1) -and
        ($null -eq $Mode -or [int]$Mode -eq 2)
}

$markerToWall = Get-FarClipDepth -DepthsMm @(1500.0, 1700.0) -FallbackDepthMm 1200.0
Assert-True ([Math]::Abs($markerToWall.DepthMm - 1750.0) -lt 0.001) "marker-to-wall depth must include 50 mm margin"

$reversedFrame = Get-FarClipDepth -DepthsMm @(1500.0, 1700.0) -FallbackDepthMm 1200.0
Assert-True ([Math]::Abs($reversedFrame.DepthMm - $markerToWall.DepthMm) -lt 0.001) "crop frame direction must not change view-direction depth"

$negative = Get-FarClipDepth -DepthsMm @(-1400.0, -1650.0) -FallbackDepthMm 1200.0
Assert-True ([Math]::Abs($negative.DepthMm - 1700.0) -lt 0.001) "negative depths must use absolute maximum"
Assert-True ($negative.Method -eq "view_origin_to_target_abs_depth_fallback") "negative depth method"

$fallback = Get-FarClipDepth -DepthsMm @() -FallbackDepthMm 1200.0
Assert-True ([Math]::Abs($fallback.DepthMm - 1200.0) -lt 0.001) "empty geometry fallback"

$minimum = Get-FarClipDepth -DepthsMm @() -FallbackDepthMm 10.0
Assert-True ([Math]::Abs($minimum.DepthMm - 50.0) -lt 0.001) "minimum far clip depth"

Assert-True (Test-FarClipReadback 1750.0 1750.9 1 2) "readback within 1 mm"
Assert-True (-not (Test-FarClipReadback 1750.0 1751.1 1 2)) "readback beyond 1 mm"
Assert-True (-not (Test-FarClipReadback 1750.0 1750.0 0 2)) "far clip active readback"
Assert-True (-not (Test-FarClipReadback 1750.0 1750.0 1 0)) "far clip mode readback"

$projectRoot = Split-Path -Parent $PSScriptRoot
$curtainSource = [System.IO.File]::ReadAllText(
    (Join-Path $projectRoot "MCP\Core\Commands\CommandExecutor.CurtainWall.cs"),
    (New-Object System.Text.UTF8Encoding($false, $true))
)
$dimensionSource = [System.IO.File]::ReadAllText(
    (Join-Path $projectRoot "MCP\Core\Commands\CommandExecutor.CurtainWallDimensions.cs"),
    (New-Object System.Text.UTF8Encoding($false, $true))
)

Assert-True ($curtainSource.Contains('parameters["horizontalMarginMm"]?.Value<double>() ?? 0.0')) "horizontal crop margin contract changed"
Assert-True ($curtainSource.Contains('parameters["verticalMarginMm"]?.Value<double>() ?? 0.0')) "vertical crop margin contract changed"
Assert-True ($curtainSource.Contains('parameters["offsetMm"]?.Value<double>() ?? 1500.0')) "marker offset contract changed"
Assert-True ($curtainSource.Contains('parameters["depthMm"]?.Value<double>() ?? 1200.0')) "depth fallback contract changed"
Assert-True ($dimensionSource.Contains("double topGridY = maxY + offsetFt;")) "top grid dimension coordinate changed"
Assert-True ($dimensionSource.Contains("double topTotalY = topGridY + stackOffsetFt;")) "top total dimension coordinate changed"
Assert-True ($dimensionSource.Contains("double rightGridX = maxX + offsetFt;")) "right grid dimension coordinate changed"
Assert-True ($dimensionSource.Contains("double rightTotalX = rightGridX + stackOffsetFt;")) "right total dimension coordinate changed"

Write-Host "PASS curtain elevation far clip tests"
