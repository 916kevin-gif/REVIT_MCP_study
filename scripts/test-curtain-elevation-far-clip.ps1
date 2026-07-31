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
Assert-True ($dimensionSource.Contains("double topGridY = maxY + stackOffsetResolution.InnerOffsetFt;")) "top grid dimension coordinate changed"
Assert-True ($dimensionSource.Contains("double topTotalY = topGridY + stackOffsetResolution.ResolvedOffsetFt;")) "top total dimension coordinate changed"
Assert-True ($dimensionSource.Contains("double leftGridX = minX - stackOffsetResolution.InnerOffsetFt;")) "right grid dimension coordinate changed"
Assert-True ($dimensionSource.Contains("double leftTotalX = leftGridX - stackOffsetResolution.ResolvedOffsetFt;")) "right total dimension coordinate changed"

function Resolve-LevelAwareCropBottomMm {
    param(
        [object]$LevelYmm,
        [double]$CurtainGeometryMinYmm,
        [double]$VerticalMarginMm
    )

    $baseBottomMm = if ($null -eq $LevelYmm) {
        $CurtainGeometryMinYmm
    }
    else {
        [Math]::Min([double]$LevelYmm, $CurtainGeometryMinYmm)
    }

    return $baseBottomMm - $VerticalMarginMm
}

Assert-True ((Resolve-LevelAwareCropBottomMm 0.0 300.0 0.0) -eq 0.0) "crop bottom must align wall Level when curtain starts above it"
Assert-True ((Resolve-LevelAwareCropBottomMm 0.0 -200.0 0.0) -eq -200.0) "crop bottom must retain curtain geometry below wall Level"
Assert-True ((Resolve-LevelAwareCropBottomMm 0.0 300.0 100.0) -eq -100.0) "vertical margin must expand below resolved crop bottom"
Assert-True ((Resolve-LevelAwareCropBottomMm $null 300.0 100.0) -eq 200.0) "missing Level must fall back to geometry bottom"
Assert-True ($curtainSource.Contains("ResolveCurtainElevationLevelAwareCropBottom")) "Level-aware crop resolver missing"
Assert-True ($curtainSource.Contains("Math.Min(levelViewYFt, curtainGeometryMinYFt)")) "Level/geometry minimum rule missing"
Assert-True ($curtainSource.Contains("view2DFrame.Inverse.OfPoint(levelPoint).Y")) "Level elevation must be projected into view 2D frame"
Assert-True ($dimensionSource.Contains("cropResult.CurtainGeometryMinYFt ?? cropResult.View2DMin.Y")) "dimension minimum must prefer curtain geometry bounds"
Assert-True ($dimensionSource.Contains("cropResult.CurtainGeometryMaxYFt ?? cropResult.View2DMax.Y")) "dimension maximum must prefer curtain geometry bounds"
function Resolve-LevelOffsetDimensionModeMm {
    param(
        [double]$CurtainBottomYmm,
        [double]$LevelYmm
    )

    $signedOffsetMm = $CurtainBottomYmm - $LevelYmm
    if ([Math]::Abs($signedOffsetMm) -le 1.0) {
        return "skipped_zero_distance"
    }
    if ($signedOffsetMm -gt 1.0) {
        return "total_height_chain"
    }
    return "separate_outer_below_level"
}

Assert-True ((Resolve-LevelOffsetDimensionModeMm 300.0 0.0) -eq "total_height_chain") "curtain bottom above Level must add Level to total-height chain"
Assert-True ((Resolve-LevelOffsetDimensionModeMm 1.0 0.0) -eq "skipped_zero_distance") "level offset at 1 mm tolerance must be skipped"
Assert-True ((Resolve-LevelOffsetDimensionModeMm -200.0 0.0) -eq "separate_outer_below_level") "curtain bottom below Level must use a separate outer dimension"
Assert-True ($dimensionSource.Contains("const double zeroToleranceFt = 1.0 / 304.8;")) "level offset zero tolerance must be 1 mm"
Assert-True ($dimensionSource.Contains("Reference reference = level?.GetPlaneReference();")) "level offset must use Level.GetPlaneReference()"
Assert-True (-not $dimensionSource.Contains("new Reference(level)")) "generic Level element references must not be used for dimensions"
Assert-True ($dimensionSource.Contains("Reference.ParseFromStableRepresentation")) "Level references must validate stable representation round-trip"
Assert-True ($dimensionSource.Contains('ReferenceSource = "wall_level_plane_reference"')) "Level plane diagnostics source missing"
Assert-True ($dimensionSource.Contains('ReferenceSource = "invisible_detail_curve_fallback"')) "invisible Level fallback diagnostics source missing"
Assert-True ($dimensionSource.Contains("BuiltInCategory.OST_InvisibleLines was unavailable.")) "missing invisible line style must hard-fail the helper"
Assert-True ($dimensionSource.Contains("DeleteCurtainElevationInvisibleLevelReference")) "failed invisible Level helpers must be cleaned up"
$levelHelperStart = $dimensionSource.IndexOf("private bool TryCreateCurtainElevationLevelPlaneReference")
$levelHelperEnd = $dimensionSource.IndexOf("private bool TryCreateCurtainElevationOriginalTotalHeightDimension", $levelHelperStart)
Assert-True ($levelHelperStart -ge 0 -and $levelHelperEnd -gt $levelHelperStart) "Level reference helper source range missing"
$levelHelperSource = $dimensionSource.Substring($levelHelperStart, $levelHelperEnd - $levelHelperStart)
Assert-True (-not $levelHelperSource.Contains("ActiveView")) "Level reference resolution must not activate or open the target view"
Assert-True ($dimensionSource.Contains("cropResult.CropBottomLevelViewYFt.Value + yShift")) "Level Y must be converted into the dimension frame"
Assert-True ($dimensionSource.Contains("result.LevelOffsetDimensionElementId = enhancedId;")) "same-chain result must expose the containing dimension id"
Assert-True ($dimensionSource.Contains("double separateDimensionX = leftTotalX - stackOffsetFt;")) "below-Level offset must use the next outer dimension stack"
Assert-True ($dimensionSource.Contains("TryRecoverEnhancedCurtainElevationTotalHeightDimension")) "enhanced total-height dimension must have post-commit recovery"
Write-Host "PASS curtain elevation far clip tests"
