param(
    [double]$ToleranceMm = 0.1
)

$ErrorActionPreference = "Stop"

function New-Vec([double]$x, [double]$y, [double]$z) {
    [pscustomobject]@{ X = $x; Y = $y; Z = $z }
}

function Add-Vec($a, $b) {
    New-Vec ($a.X + $b.X) ($a.Y + $b.Y) ($a.Z + $b.Z)
}

function Sub-Vec($a, $b) {
    New-Vec ($a.X - $b.X) ($a.Y - $b.Y) ($a.Z - $b.Z)
}

function Mul-Vec($a, [double]$s) {
    New-Vec ($a.X * $s) ($a.Y * $s) ($a.Z * $s)
}

function Dot-Vec($a, $b) {
    ($a.X * $b.X) + ($a.Y * $b.Y) + ($a.Z * $b.Z)
}

function Normalize-Vec($a) {
    $len = [Math]::Sqrt((Dot-Vec $a $a))
    if ($len -le 1e-9) {
        throw "Cannot normalize zero vector."
    }

    Mul-Vec $a (1.0 / $len)
}

function Compute-FarClipPlan($origin, $look, $cropOrigin, $cropBasisZ, $points, [double]$marginMm, [double]$actualViewerBoundOffsetMm) {
    $visualLook = Normalize-Vec $look
    $basisZ = Normalize-Vec $cropBasisZ
    $viewOriginLocalZ = Dot-Vec (Sub-Vec $origin $cropOrigin) $basisZ
    $lookLocalZ = Dot-Vec $visualLook $basisZ

    $minDepth = [double]::PositiveInfinity
    $maxDepth = [double]::NegativeInfinity
    $maxPositiveDepth = [double]::NegativeInfinity
    $maxAbsDepth = [double]::NegativeInfinity
    $positiveCount = 0

    foreach ($point in $points) {
        $depth = Dot-Vec (Sub-Vec $point $origin) $visualLook
        $minDepth = [Math]::Min($minDepth, $depth)
        $maxDepth = [Math]::Max($maxDepth, $depth)

        if ($depth -gt 0) {
            $positiveCount++
            $maxPositiveDepth = [Math]::Max($maxPositiveDepth, $depth)
        }

        $maxAbsDepth = [Math]::Max($maxAbsDepth, [Math]::Abs($depth))
    }

    if ($points.Count -eq 0) {
        throw "Test case has no target points."
    }

    $targetDepth = if ($positiveCount -gt 0) { $maxPositiveDepth } else { $maxAbsDepth }
    $requestedDepth = $targetDepth + $marginMm
    $warning = if ($positiveCount -gt 0) { $null } else { "absolute_fallback" }
    $offsetDelta = [Math]::Abs($actualViewerBoundOffsetMm - $requestedDepth)

    if ([Math]::Abs($lookLocalZ) -le 1e-9) {
        return [pscustomobject]@{
            RequestedDepthMm = $requestedDepth
            ViewerBoundOffsetMm = $actualViewerBoundOffsetMm
            DepthDeltaMm = $offsetDelta
            FarClipPass = $offsetDelta -le $ToleranceMm
            PositivePointCount = $positiveCount
            Warning = "crop_box_z_not_applied"
            MinDepthMm = $minDepth
            MaxDepthMm = $maxDepth
        }
    }

    if ($lookLocalZ -ge 0) {
        $minZ = $viewOriginLocalZ
        $maxZ = $viewOriginLocalZ + $requestedDepth
    } else {
        $minZ = $viewOriginLocalZ - $requestedDepth
        $maxZ = $viewOriginLocalZ
    }

    if ($minZ -gt $maxZ) {
        $tmp = $minZ
        $minZ = $maxZ
        $maxZ = $tmp
    }

    [pscustomobject]@{
        RequestedDepthMm = $requestedDepth
        ViewerBoundOffsetMm = $actualViewerBoundOffsetMm
        DepthDeltaMm = $offsetDelta
        FarClipPass = $offsetDelta -le $ToleranceMm
        PositivePointCount = $positiveCount
        Warning = $warning
        MinDepthMm = $minDepth
        MaxDepthMm = $maxDepth
        CropBoxMinZMm = $minZ
        CropBoxMaxZMm = $maxZ
        CropBoxDepthMm = [Math]::Abs($maxZ - $minZ)
        CropBoxDepthIsDiagnosticOnly = $true
        ViewOriginLocalZMm = $viewOriginLocalZ
        LookDirectionLocalZ = $lookLocalZ
    }
}

function Assert-Near([string]$name, [double]$actual, [double]$expected) {
    if ([Math]::Abs($actual - $expected) -gt $ToleranceMm) {
        throw "$name expected $expected, got $actual"
    }
}

function Assert-Equal([string]$name, $actual, $expected) {
    if ($actual -ne $expected) {
        throw "$name expected $expected, got $actual"
    }
}

$sqrtHalf = [Math]::Sqrt(0.5)
$cases = @(
    @{
        Name = "horizontal_positive_depth"
        Origin = New-Vec 0 0 0
        Look = New-Vec 0 1 0
        CropOrigin = New-Vec 0 0 0
        CropBasisZ = New-Vec 0 1 0
        Points = @((New-Vec 0 100 0), (New-Vec 0 200 0))
        ExpectedDepth = 250
        ActualViewerBoundOffset = 250
        ExpectedPositiveCount = 2
        ExpectedWarning = $null
        ExpectedPass = $true
    },
    @{
        Name = "vertical_positive_depth"
        Origin = New-Vec 10 20 0
        Look = New-Vec 1 0 0
        CropOrigin = New-Vec 10 20 0
        CropBasisZ = New-Vec 1 0 0
        Points = @((New-Vec 110 20 0), (New-Vec 220 45 0))
        ExpectedDepth = 260
        ActualViewerBoundOffset = 260
        ExpectedPositiveCount = 2
        ExpectedWarning = $null
        ExpectedPass = $true
    },
    @{
        Name = "slanted_positive_depth"
        Origin = New-Vec 0 0 0
        Look = New-Vec $sqrtHalf $sqrtHalf 0
        CropOrigin = New-Vec 0 0 0
        CropBasisZ = New-Vec $sqrtHalf $sqrtHalf 0
        Points = @((New-Vec (100 * $sqrtHalf - 30 * $sqrtHalf) (100 * $sqrtHalf + 30 * $sqrtHalf) 0), (New-Vec (200 * $sqrtHalf) (200 * $sqrtHalf) 0))
        ExpectedDepth = 250
        ActualViewerBoundOffset = 250
        ExpectedPositiveCount = 2
        ExpectedWarning = $null
        ExpectedPass = $true
    },
    @{
        Name = "negative_depth_absolute_fallback"
        Origin = New-Vec 0 0 0
        Look = New-Vec 0 1 0
        CropOrigin = New-Vec 0 0 0
        CropBasisZ = New-Vec 0 1 0
        Points = @((New-Vec 0 -200 0), (New-Vec 0 -80 0))
        ExpectedDepth = 250
        ActualViewerBoundOffset = 250
        ExpectedPositiveCount = 0
        ExpectedWarning = "absolute_fallback"
        ExpectedPass = $true
    },
    @{
        Name = "reversed_crop_local_z"
        Origin = New-Vec 0 0 0
        Look = New-Vec 0 1 0
        CropOrigin = New-Vec 0 0 0
        CropBasisZ = New-Vec 0 -1 0
        Points = @((New-Vec 0 100 0), (New-Vec 0 200 0))
        ExpectedDepth = 250
        ActualViewerBoundOffset = 250
        ExpectedPositiveCount = 2
        ExpectedWarning = $null
        ExpectedPass = $true
    },
    @{
        Name = "viewer_bound_offset_mismatch_fails"
        Origin = New-Vec 0 0 0
        Look = New-Vec 0 1 0
        CropOrigin = New-Vec 0 0 0
        CropBasisZ = New-Vec 0 1 0
        Points = @((New-Vec 0 100 0), (New-Vec 0 200 0))
        ExpectedDepth = 250
        ActualViewerBoundOffset = 400
        ExpectedPositiveCount = 2
        ExpectedWarning = $null
        ExpectedPass = $false
    }
)

foreach ($case in $cases) {
    $result = Compute-FarClipPlan $case.Origin $case.Look $case.CropOrigin $case.CropBasisZ $case.Points 50 $case.ActualViewerBoundOffset
    Assert-Near "$($case.Name) RequestedDepthMm" $result.RequestedDepthMm $case.ExpectedDepth
    Assert-Near "$($case.Name) ViewerBoundOffsetMm" $result.ViewerBoundOffsetMm $case.ActualViewerBoundOffset
    Assert-Equal "$($case.Name) PositivePointCount" $result.PositivePointCount $case.ExpectedPositiveCount
    Assert-Equal "$($case.Name) Warning" $result.Warning $case.ExpectedWarning
    Assert-Equal "$($case.Name) FarClipPass" $result.FarClipPass $case.ExpectedPass
    Write-Host "PASS $($case.Name)"
}

Write-Host "All curtain elevation geometry tests passed."
