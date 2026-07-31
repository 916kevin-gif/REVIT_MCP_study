$ErrorActionPreference = "Stop"
function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "FAIL: $Message" }
}
$root = Split-Path -Parent $PSScriptRoot
$mainPath = Join-Path $root "MCP\Core\Commands\CommandExecutor.CurtainWallLevelOffsetRuntime.cs"
$attemptPath = Join-Path $root "MCP\Core\Commands\CommandExecutor.CurtainWallLevelOffsetRuntime.Attempts.cs"
$dimensionPath = Join-Path $root "MCP\Core\Commands\CommandExecutor.CurtainWallDimensions.cs"
$schemaPath = Join-Path $root "MCP-Server\src\tools\curtain-wall-tools.ts"
$main = [IO.File]::ReadAllText($mainPath, [Text.Encoding]::UTF8)
$attempts = [IO.File]::ReadAllText($attemptPath, [Text.Encoding]::UTF8)
$dimension = [IO.File]::ReadAllText($dimensionPath, [Text.Encoding]::UTF8)
$schema = [IO.File]::ReadAllText($schemaPath, [Text.Encoding]::UTF8)
Assert-True ($schema.Contains('"level_offset"')) "MCP schema must expose level_offset"
Assert-True ($dimension.Contains('if (testMode == "level_offset")')) "diagnostic dispatch must route level_offset before legacy fallback"
Assert-True ($main.Contains("ResolveSingleSelectedCurtainWall")) "level_offset must resolve exactly one selected curtain wall"
Assert-True ($main.Contains("first-wall fallback is disabled")) "level_offset must explicitly reject first-wall fallback"
Assert-True ($main.Contains('offsets.ResolvedOffsetFt')) "production runtime attempt must use the resolved dimension stack offset"
Assert-True (-not $main.Contains('RuntimeFrameCache')) "runtime frame must remain local to the invocation"
Assert-True ($main.Contains('new[]{"level_plane","invisible_detail_curve"}')) "runtime matrix must test Level plane and Invisible DetailCurve"
Assert-True ($main.Contains('"inactive"')) "runtime matrix must include inactive target view"
Assert-True ($main.Contains('"active"')) "runtime matrix must include active target view"
Assert-True ($attempts.Contains('includeTop?3:2')) "runtime matrix must test two and three references"
Assert-True ($attempts.Contains('ElementReferenceType')) "runtime output must include ElementReferenceType"
Assert-True ($attempts.Contains('Reference.ParseFromStableRepresentation')) "runtime test must validate stable reference round-trip"
Assert-True ($attempts.Contains('tx.Commit();attempt.TransactionCommitted=true')) "runtime test must commit before post-commit assertions"
Assert-True ($attempts.Contains('persisted.AreReferencesAvailable')) "runtime test must read AreReferencesAvailable after commit"
Assert-True ($attempts.Contains('PreCommitReferenceCount')) "runtime test must report pre-commit reference count"
Assert-True ($main.Contains('CleanupFailureStage=cleanup?null:"cleanup"')) "runtime test must expose cleanup failure stage"
Assert-True ($attempts.Contains('persisted.References?.Size')) "runtime test must validate persisted reference count"
Assert-True ($attempts.Contains('GetCurtainElevationDimensionValuesMm')) "runtime test must read actual dimension segment values"
Assert-True ($attempts.Contains('ExpectedSegmentValuesMm')) "runtime test must report expected segment values"
Assert-True ($attempts.Contains('HelperUsesInvisibleLines')) "fallback test must verify OST_InvisibleLines read-back"
Assert-True ($attempts.Contains('CreateCurtainElevationTotalHeightAndLevelOffsetDimensions')) "runtime test must invoke the production creation path"
Assert-True ($attempts.Contains('FinalizeCurtainElevationDimensionsAfterCommit')) "runtime test must invoke production post-commit repair"
Assert-True ($main.Contains('group.RollBack()')) "level_offset runtime test must rollback its TransactionGroup"
Assert-True ($main.Contains('ForcedRollback=true')) "runtime result must declare forced rollback"
Assert-True ($main.Contains('All(id=>doc.GetElement(id)==null)')) "runtime test must verify rollback cleanup"
Assert-True ($main.Contains('FirstFailure=attempts.FirstOrDefault')) "runtime output must expose the first raw failure"
Assert-True ($main.Contains('FirstProductionFailure=production.FirstOrDefault')) "runtime output must expose the first production failure"
$scopeStart = $dimension.IndexOf("private bool IsCurtainElevationLevelOffsetPlanePending")
$scopeEnd = $dimension.IndexOf("private List<double> GetCurtainElevationExpectedSegmentValuesMm", $scopeStart)
Assert-True ($scopeStart -ge 0 -and $scopeEnd -gt $scopeStart) "Level false-negative scope guard must exist"
$scopeGuard = $dimension.Substring($scopeStart, $scopeEnd - $scopeStart)
Assert-True ($scopeGuard.Contains('pending.NativeReferenceSource == "wall_level_plane_reference"')) "Level false-negative must require wall_level_plane_reference"
Assert-True ($scopeGuard.Contains('pending.Kind == "level_offset"')) "Level false-negative must allow only the level_offset kind directly"
Assert-True ($scopeGuard.Contains('pending.Kind == "total_height"')) "enhanced total-height chain must be explicitly scoped"
Assert-True ($scopeGuard.Contains("pending.RecoverEnhancedTotalHeightAsSeparateDimensions")) "ordinary total height must not receive the Level false-negative exception"
Assert-True ($dimension.Contains('validationMode = "level_plane_segment_validation"')) "Level false-negative must expose its validation mode"
Assert-True ($dimension.Contains("referenceCount == pending.ExpectedReferenceCount")) "Level false-negative must validate reference count"
Assert-True ($dimension.Contains("pending.SegmentValuesPassed == true")) "Level false-negative must validate segment values"
Assert-True ($dimension.Contains("SetCurtainElevationDimensionAvailability(result, pending.Kind, referencesAvailable)")) "API AreReferencesAvailable value must be preserved"
Assert-True ($dimension.Contains("private GraphicsStyle TryFindCurtainElevationLevelInvisibleLineStyle")) "Level-specific Invisible Lines resolver missing"
Assert-True (([regex]::Matches($dimension, "TryFindCurtainElevationLevelInvisibleLineStyle").Count) -eq 2) "Level-specific Invisible Lines resolver must not be used by general dimension fallback"
Assert-True ($dimension.Contains("style.GraphicsStyleCategory.Id.GetIdValue() == invisibleCategoryId")) "Level-specific resolver must search GraphicsStyle by OST_InvisibleLines category id"
Assert-True ($attempts.Contains('PostCommitValidationMode')) "level_offset runtime must report validation mode"
Assert-True ($attempts.Contains('result.LevelOffsetDimensionReferenceSource=="wall_level_plane_reference"')) "production runtime must require the retained Level plane source"
Assert-True (-not $attempts.Contains('result.LevelOffsetDimensionAreReferencesAvailable==true')) "production runtime must not reject the proven Level plane false negative"

function Test-LevelPlanePostCommitAcceptance {
    param([string]$ReferenceSource, [string]$Kind, [bool]$RecoverEnhanced, [bool]$ReferencesAvailable, [int]$ReferenceCount, [int]$ExpectedReferenceCount, [double[]]$ExpectedSegments, [double[]]$ActualSegments)
    $scoped = $ReferenceSource -eq "wall_level_plane_reference" -and
        ($Kind -eq "level_offset" -or ($Kind -eq "total_height" -and $RecoverEnhanced))
    if ($ReferencesAvailable) { return $ReferenceCount -eq $ExpectedReferenceCount }
    if (-not $scoped -or $ReferenceCount -ne $ExpectedReferenceCount) { return $false }
    if ($ExpectedSegments.Count -ne $ActualSegments.Count) { return $false }
    for ($index = 0; $index -lt $ExpectedSegments.Count; $index++) {
        if ([Math]::Abs($ExpectedSegments[$index] - $ActualSegments[$index]) -gt 0.5) { return $false }
    }
    return $true
}

Assert-True (Test-LevelPlanePostCommitAcceptance "wall_level_plane_reference" "level_offset" $false $false 3 3 @(200, 2800) @(200, 2800)) "proven Level false-negative must be accepted"
Assert-True (-not (Test-LevelPlanePostCommitAcceptance "wall_level_plane_reference" "level_offset" $false $false 2 3 @(200, 2800) @(200, 2800))) "Level false-negative with wrong reference count must fail"
Assert-True (-not (Test-LevelPlanePostCommitAcceptance "wall_level_plane_reference" "level_offset" $false $false 3 3 @(200, 2800) @(201, 2800))) "Level false-negative beyond 0.5 mm must fail"
Assert-True (-not (Test-LevelPlanePostCommitAcceptance "wall_level_plane_reference" "total_height" $false $false 3 3 @(200, 2800) @(200, 2800))) "ordinary total height must remain strict"
Assert-True (-not (Test-LevelPlanePostCommitAcceptance "curtain_grid_curve_reference" "vertical_grid" $false $false 3 3 @(200, 2800) @(200, 2800))) "CurtainGrid dimensions must remain strict"



Write-Host "PASS curtain Level offset runtime diagnostic static tests"
