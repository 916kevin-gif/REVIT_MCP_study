using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        private static IdType? LastCurtainElevationDimensionTypeId;

        private object DiagnoseCurtainWallElevationDimensions(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            UIDocument uidoc = _uiApp.ActiveUIDocument;

            string testMode = parameters["testMode"]?.Value<string>()?.Trim().ToLowerInvariant() ?? "both";
            bool rollback = parameters["rollback"]?.Value<bool>() ?? true;
            double fallbackInnerOffsetFt = (parameters["dimensionOffsetMm"]?.Value<double>() ?? 300.0) / 304.8;
            double fallbackStackOffsetFt = (parameters["dimensionStackOffsetMm"]?.Value<double>() ?? 250.0) / 304.8;
            var failures = new List<string>();
            var attempts = new List<CurtainElevationDimensionAttempt>();
            var createdDimensionIds = new List<ElementId>();
            var verifiedDimensionIds = new List<ElementId>();
            var referencePlaneIds = new List<ElementId>();
            var dimensionWarnings = new List<string>();

            ViewSection view = null;
            IdType? viewId = parameters["viewId"]?.Value<IdType>();
            if (viewId.HasValue)
                view = doc.GetElement(new ElementId(viewId.Value)) as ViewSection;
            else
                view = uidoc.ActiveView as ViewSection;

            if (view == null || view.IsTemplate)
                throw new Exception("Provide a valid elevation ViewSection viewId or make one active.");

            Wall wall = null;
            IdType? wallId = parameters["wallId"]?.Value<IdType>();
            if (wallId.HasValue)
                wall = doc.GetElement(new ElementId(wallId.Value)) as Wall;
            else
            {
                wall = new FilteredElementCollector(doc)
                    .OfClass(typeof(Wall))
                    .WhereElementIsNotElementType()
                    .Cast<Wall>()
                    .FirstOrDefault(w =>
                    {
                        try { return w.CurtainGrid != null; }
                        catch { return false; }
                    });
            }

            if (wall == null || wall.CurtainGrid == null)
                throw new Exception("Provide a valid curtain wall wallId (Wall with CurtainGrid).");

            CurtainElevationDimensionTypeResolution dimensionTypeResolution =
                ResolveCurtainElevationDimensionType(doc, parameters, dimensionWarnings);
            DimensionType dimensionType = dimensionTypeResolution.DimensionType;
            if (dimensionType == null)
                failures.Add("No DimensionType could be resolved.");
            CurtainElevationDimensionStackOffsetResolution stackOffsetResolution =
                ResolveCurtainElevationDimensionStackOffset(dimensionType, view.Scale, fallbackInnerOffsetFt, fallbackStackOffsetFt);
            if (!string.IsNullOrWhiteSpace(stackOffsetResolution.Warning))
                dimensionWarnings.Add(stackOffsetResolution.Warning);

            int referencePlaneCreatedCount = 0;
            int referencePlaneReferenceCount = 0;
            List<CurtainElevationGeometryReference> geometryReferences = new List<CurtainElevationGeometryReference>();
            List<CurtainElevationGeometryReference> gridLineReferences = new List<CurtainElevationGeometryReference>();
            CurtainElevationCropResult cropResult = null;

            using (Transaction trans = new Transaction(doc, rollback ? "Diagnose curtain elevation dimensions (Rollback)" : "Diagnose curtain elevation dimensions"))
            {
                trans.Start();

                try
                {
                    LocationCurve loc = wall.Location as LocationCurve;
                    XYZ wallMid = loc?.Curve?.Evaluate(0.5, true);
                    cropResult = ConfigureCurtainElevationCrop(doc, view, wall, wallMid, view.Origin, 0, 0, 1200.0 / 304.8);
                    doc.Regenerate();

                    Transform sourceFrame = GetCurtainElevationView2DFrame(view, view.CropBox?.Transform);
                    Transform frame = GetCurtainElevationDimensionFrame(view, sourceFrame);
                    if (frame == null || sourceFrame == null || cropResult.View2DMin == null || cropResult.View2DMax == null)
                    {
                        failures.Add("Cannot resolve dimension frame or crop 2D bounds.");
                    }
                    else if (dimensionType != null)
                    {
                        XYZ sourceOriginDelta = sourceFrame.Origin - frame.Origin;
                        double xShift = sourceOriginDelta.DotProduct(frame.BasisX);
                        double yShift = sourceOriginDelta.DotProduct(frame.BasisY);
                        double minX = cropResult.View2DMin.X + xShift;
                        double maxX = cropResult.View2DMax.X + xShift;
                        double minY = cropResult.View2DMin.Y + yShift;
                        double maxY = cropResult.View2DMax.Y + yShift;
                        double topGridY = maxY + stackOffsetResolution.InnerOffsetFt;
                        double topTotalY = topGridY + stackOffsetResolution.ResolvedOffsetFt;
                        double rightGridX = maxX + stackOffsetResolution.InnerOffsetFt;
                        double rightTotalX = rightGridX + stackOffsetResolution.ResolvedOffsetFt;

                        geometryReferences = CollectCurtainElevationGeometryReferences(doc, wall, view, frame, minX, maxX, minY, maxY);
                        gridLineReferences = CollectCurtainElevationGridLineReferences(doc, wall, view, frame, minX, maxX, minY, maxY);
                        if (testMode == "geometry_reference" || testMode == "both")
                        {
                            List<CurtainElevationGeometryReference> totalWidthRefs = SelectCurtainElevationBoundaryReferences(geometryReferences, "horizontal", minX, maxX, minY, maxY);
                            attempts.Add(TryDiagnoseCurtainGeometryDimension(doc, view, frame, dimensionType, "total_width", "horizontal", new List<double> { minX, maxX }, totalWidthRefs, topTotalY));

                            List<CurtainElevationGeometryReference> totalHeightRefs = SelectCurtainElevationBoundaryReferences(geometryReferences, "vertical", minX, maxX, minY, maxY);
                            attempts.Add(TryDiagnoseCurtainGeometryDimension(doc, view, frame, dimensionType, "total_height", "vertical", new List<double> { minY, maxY }, totalHeightRefs, rightTotalX));

                            List<double> verticalGridXs = GetCurtainElevationGridCoordinates(doc, wall, frame, "vertical", minX, maxX, minY, maxY);
                            List<CurtainElevationGeometryReference> verticalGridRefs = SelectCurtainElevationGridDimensionReferences(geometryReferences, gridLineReferences, "horizontal", verticalGridXs);
                            attempts.Add(TryDiagnoseCurtainGeometryDimension(doc, view, frame, dimensionType, "horizontal_grid", "horizontal", verticalGridXs, verticalGridRefs, topGridY));

                            List<double> horizontalGridYs = GetCurtainElevationGridCoordinates(doc, wall, frame, "horizontal", minX, maxX, minY, maxY);
                            List<CurtainElevationGeometryReference> horizontalGridRefs = SelectCurtainElevationGridDimensionReferences(geometryReferences, gridLineReferences, "vertical", horizontalGridYs);
                            attempts.Add(TryDiagnoseCurtainGeometryDimension(doc, view, frame, dimensionType, "vertical_grid", "vertical", horizontalGridYs, horizontalGridRefs, rightGridX));
                        }

                        if (testMode == "reference_plane_fallback" || testMode == "both")
                        {
                            attempts.Add(TryDiagnoseCurtainReferencePlaneDimension(doc, view, frame, dimensionType, "total_width", "horizontal", new List<double> { minX, maxX }, minY, maxY, topTotalY, referencePlaneIds, out int widthRefs));
                            referencePlaneReferenceCount += widthRefs;

                            attempts.Add(TryDiagnoseCurtainReferencePlaneDimension(doc, view, frame, dimensionType, "total_height", "vertical", new List<double> { minY, maxY }, minX, maxX, rightTotalX, referencePlaneIds, out int heightRefs));
                            referencePlaneReferenceCount += heightRefs;
                        }
                    }

                    doc.Regenerate();

                    foreach (CurtainElevationDimensionAttempt attempt in attempts)
                    {
                        if (attempt?.DimensionId == null || attempt.DimensionId == ElementId.InvalidElementId)
                            continue;

                        createdDimensionIds.Add(attempt.DimensionId);
                        Dimension dimension = doc.GetElement(attempt.DimensionId) as Dimension;
                        attempt.ExistsAfterCreate = dimension != null;
                        attempt.OwnerViewId = dimension?.OwnerViewId;
                        if (dimension != null && dimension.OwnerViewId == view.Id)
                            verifiedDimensionIds.Add(attempt.DimensionId);
                        else if (dimension != null)
                            attempt.FailureMessage = AppendCurtainElevationWarning(attempt.FailureMessage, $"OwnerViewId readback mismatch: {dimension.OwnerViewId.GetIdValue()}.");
                    }

                    referencePlaneCreatedCount = referencePlaneIds.Count;

                    if (rollback)
                        trans.RollBack();
                    else
                        trans.Commit();
                }
                catch (Exception ex)
                {
                    failures.Add(ex.Message);
                    if (trans.GetStatus() == TransactionStatus.Started)
                        trans.RollBack();
                }
            }

            return new
            {
                WallId = wall.Id.GetIdValue(),
                ViewId = view.Id.GetIdValue(),
                ViewName = view.Name,
                DimensionTypeId = dimensionType?.Id.GetIdValue(),
                DimensionTypeName = dimensionType?.Name,
                DimensionTypeSource = dimensionTypeResolution.Source,
                DimensionWitnessLineLengthPaperMm = stackOffsetResolution.WitnessLineLengthPaperFt.HasValue
                    ? Math.Round(stackOffsetResolution.WitnessLineLengthPaperFt.Value * 304.8, 3)
                    : (double?)null,
                DimensionViewScale = stackOffsetResolution.ViewScale,
                DimensionInnerOffsetExtraPaperMm = Math.Round(stackOffsetResolution.InnerOffsetExtraPaperFt * 304.8, 3),
                DimensionInnerOffsetModelMm = Math.Round(stackOffsetResolution.InnerOffsetFt * 304.8, 3),
                DimensionInnerOffsetSource = stackOffsetResolution.InnerOffsetSource,
                DimensionInnerOffsetFallbackReason = stackOffsetResolution.InnerOffsetFallbackReason,
                DimensionStackOffsetModelMm = Math.Round(stackOffsetResolution.ResolvedOffsetFt * 304.8, 3),
                DimensionStackOffsetSource = stackOffsetResolution.Source,
                DimensionStackOffsetFallbackReason = stackOffsetResolution.FallbackReason,
                DimensionWarnings = dimensionWarnings,
                GeometryReferenceCount = geometryReferences.Count,
                GeometryReferenceSamples = geometryReferences
                    .Take(20)
                    .Select(r => new
                    {
                        ElementId = r.ElementId?.GetIdValue(),
                        Category = r.CategoryName,
                        IsVertical = r.IsVertical,
                        IsHorizontal = r.IsHorizontal,
                        CenterXmm = Math.Round(r.CenterX * 304.8, 1),
                        CenterYmm = Math.Round(r.CenterY * 304.8, 1),
                        LengthMm = Math.Round(r.Length * 304.8, 1)
                    })
                    .ToList(),
                ReferencePlaneCreatedCount = referencePlaneCreatedCount,
                ReferencePlaneReferenceCount = referencePlaneReferenceCount,
                ReferencePlaneIds = referencePlaneIds.Select(id => id.GetIdValue()).ToList(),
                AttemptedDimensions = attempts.Select(ToCurtainElevationDimensionAttemptResult).ToList(),
                CreatedDimensionIds = createdDimensionIds.Select(id => id.GetIdValue()).ToList(),
                VerifiedDimensionIds = verifiedDimensionIds.Select(id => id.GetIdValue()).ToList(),
                Failures = failures,
                Rollback = rollback
            };
        }


        private class CurtainElevationDimensionTypeResolution
        {
            public DimensionType DimensionType { get; set; }
            public string Source { get; set; } = "not_resolved";
        }

        private class CurtainElevationDimensionStackOffsetResolution
        {
            public double ResolvedOffsetFt { get; set; }
            public double InnerOffsetFt { get; set; }
            public double InnerOffsetExtraPaperFt { get; set; }
            public string InnerOffsetSource { get; set; } = "parameter_fallback";
            public string InnerOffsetFallbackReason { get; set; }
            public double? WitnessLineLengthPaperFt { get; set; }
            public int ViewScale { get; set; }
            public string Source { get; set; } = "parameter_fallback";
            public string FallbackReason { get; set; }
            public string Warning { get; set; }
        }

        private class CurtainElevationDimensionResult
        {
            public ElementId TotalWidthDimensionId { get; set; }
            public ElementId HorizontalGridDimensionId { get; set; }
            public ElementId TotalHeightDimensionId { get; set; }
            public ElementId VerticalGridDimensionId { get; set; }
            public List<ElementId> ReferenceCurveIds { get; } = new List<ElementId>();
            public List<string> Warnings { get; } = new List<string>();
            public int GeometryReferenceCount { get; set; }
            public int CurtainGridLineCount { get; set; }
            public int CurtainGridLineReferenceCount { get; set; }
            public List<string> CurtainGridLineReferenceFailures { get; } = new List<string>();
            public List<object> CurtainGridLineReferenceSamples { get; } = new List<object>();
            public List<string> GeometryReferenceCategories { get; set; } = new List<string>();
            public string TotalWidthDimensionReferenceSource { get; set; }
            public string TotalHeightDimensionReferenceSource { get; set; }
            public string HorizontalGridDimensionReferenceSource { get; set; }
            public string VerticalGridDimensionReferenceSource { get; set; }
            public string DimensionFallbackReason { get; set; }
            public double? DimensionWitnessLineLengthPaperFt { get; set; }
            public int DimensionViewScale { get; set; }
            public double DimensionInnerOffsetExtraPaperFt { get; set; }
            public double DimensionInnerOffsetFt { get; set; }
            public string DimensionInnerOffsetSource { get; set; }
            public string DimensionInnerOffsetFallbackReason { get; set; }
            public double DimensionStackOffsetFt { get; set; }
            public string DimensionStackOffsetSource { get; set; }
            public string DimensionStackOffsetFallbackReason { get; set; }
            public int AttemptCount { get; set; }
            public int VerifiedCount { get; set; }
            public List<string> CreationErrors { get; } = new List<string>();
            public int CreatedCount { get; set; }
            public int FailedCount { get; set; }
            public string Status { get; set; } = "not_started";
            public string Warning => string.Join(" ", Warnings.Where(w => !string.IsNullOrWhiteSpace(w)));
        }

        private class CurtainElevationGeometryReference
        {
            public Reference Reference { get; set; }
            public ElementId ElementId { get; set; }
            public string CategoryName { get; set; }
            public XYZ Start { get; set; }
            public XYZ End { get; set; }
            public double MinX { get; set; }
            public double MaxX { get; set; }
            public double MinY { get; set; }
            public double MaxY { get; set; }
            public double CenterX => (MinX + MaxX) / 2.0;
            public double CenterY => (MinY + MaxY) / 2.0;
            public double Length { get; set; }
            public bool IsVertical { get; set; }
            public bool IsHorizontal { get; set; }
            public ElementId CurtainGridLineId { get; set; }
            public string StableRepresentation { get; set; }
            public string GeometryObjectType { get; set; }
            public bool SelectedForDimension { get; set; }
            public string SelectionReason { get; set; }
        }

        private class CurtainElevationDimensionAttempt
        {
            public string Name { get; set; }
            public string Method { get; set; }
            public int ReferenceCount { get; set; }
            public XYZ DimensionLineStart { get; set; }
            public XYZ DimensionLineEnd { get; set; }
            public bool Success { get; set; }
            public ElementId DimensionId { get; set; }
            public ElementId OwnerViewId { get; set; }
            public bool ExistsAfterCreate { get; set; }
            public string FailureMessage { get; set; }
        }


        private CurtainElevationDimensionTypeResolution ResolveCurtainElevationDimensionType(Document doc, JObject parameters, List<string> warnings)
        {
            var result = new CurtainElevationDimensionTypeResolution();
            if (doc == null)
                return result;

            IdType? explicitId = parameters?["dimensionTypeId"]?.Value<IdType?>();
            if (explicitId.HasValue && explicitId.Value != 0)
            {
                DimensionType explicitType = doc.GetElement(new ElementId(explicitId.Value)) as DimensionType;
                if (explicitType != null)
                {
                    result.DimensionType = explicitType;
                    result.Source = "explicit_id";
                    LastCurtainElevationDimensionTypeId = explicitType.Id.GetIdValue();
                    return result;
                }

                warnings?.Add($"dimensionTypeId={explicitId.Value} is not a valid DimensionType; falling back to name/last/default.");
            }

            string explicitName = parameters?["dimensionTypeName"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(explicitName))
            {
                DimensionType namedType = new FilteredElementCollector(doc)
                    .OfClass(typeof(DimensionType))
                    .Cast<DimensionType>()
                    .FirstOrDefault(t => string.Equals(t.Name, explicitName, StringComparison.OrdinalIgnoreCase));
                if (namedType != null)
                {
                    result.DimensionType = namedType;
                    result.Source = "explicit_name";
                    LastCurtainElevationDimensionTypeId = namedType.Id.GetIdValue();
                    return result;
                }

                warnings?.Add($"dimensionTypeName='{explicitName}' not found; falling back to last/default.");
            }

            if (LastCurtainElevationDimensionTypeId.HasValue)
            {
                DimensionType lastType = doc.GetElement(new ElementId(LastCurtainElevationDimensionTypeId.Value)) as DimensionType;
                if (lastType != null)
                {
                    result.DimensionType = lastType;
                    result.Source = "last_used";
                    return result;
                }
            }

            try
            {
                ElementId defaultTypeId = doc.GetDefaultElementTypeId((ElementTypeGroup)10);
                DimensionType defaultType = doc.GetElement(defaultTypeId) as DimensionType;
                if (defaultType != null)
                {
                    result.DimensionType = defaultType;
                    result.Source = "revit_default";
                    LastCurtainElevationDimensionTypeId = defaultType.Id.GetIdValue();
                    return result;
                }
            }
            catch (Exception ex)
            {
                warnings?.Add($"Revit default dimension type lookup skipped: {ex.Message}");
            }

            DimensionType firstType = new FilteredElementCollector(doc)
                .OfClass(typeof(DimensionType))
                .WhereElementIsElementType()
                .Cast<DimensionType>()
                .FirstOrDefault();
            if (firstType != null)
            {
                result.DimensionType = firstType;
                result.Source = "first_available";
                LastCurtainElevationDimensionTypeId = firstType.Id.GetIdValue();
                return result;
            }

            warnings?.Add("No DimensionType found. Elevations will be created without dimensions.");
            result.Source = "not_found";
            return result;
        }

        private CurtainElevationDimensionResult CreateCurtainElevationDimensions(
            Document doc,
            ViewSection view,
            Wall wall,
            CurtainElevationCropResult cropResult,
            DimensionType dimensionType,
            bool addDimensions,
            double fallbackInnerOffsetFt,
            double fallbackStackOffsetFt)
        {
            var result = new CurtainElevationDimensionResult();
            if (!addDimensions)
            {
                result.Status = "disabled";
                return result;
            }

            if (doc == null || view == null || wall == null || cropResult == null)
            {
                result.Status = "failed";
                result.Warnings.Add("dimension skipped: missing document/view/wall/crop result.");
                result.FailedCount = 4;
                return result;
            }

            if (dimensionType == null)
            {
                result.Status = "skipped_no_dimension_type";
                result.Warnings.Add("dimension skipped: no DimensionType available.");
                result.FailedCount = 4;
                return result;
            }

            CurtainElevationDimensionStackOffsetResolution stackOffsetResolution =
                ResolveCurtainElevationDimensionStackOffset(dimensionType, view.Scale, fallbackInnerOffsetFt, fallbackStackOffsetFt);
            result.DimensionWitnessLineLengthPaperFt = stackOffsetResolution.WitnessLineLengthPaperFt;
            result.DimensionViewScale = stackOffsetResolution.ViewScale;
            result.DimensionInnerOffsetExtraPaperFt = stackOffsetResolution.InnerOffsetExtraPaperFt;
            result.DimensionInnerOffsetFt = stackOffsetResolution.InnerOffsetFt;
            result.DimensionInnerOffsetSource = stackOffsetResolution.InnerOffsetSource;
            result.DimensionInnerOffsetFallbackReason = stackOffsetResolution.InnerOffsetFallbackReason;
            result.DimensionStackOffsetFt = stackOffsetResolution.ResolvedOffsetFt;
            result.DimensionStackOffsetSource = stackOffsetResolution.Source;
            result.DimensionStackOffsetFallbackReason = stackOffsetResolution.FallbackReason;
            if (!string.IsNullOrWhiteSpace(stackOffsetResolution.Warning))
                result.Warnings.Add(stackOffsetResolution.Warning);

            Transform sourceFrame = GetCurtainElevationView2DFrame(view, view.CropBox?.Transform);
            Transform frame = GetCurtainElevationDimensionFrame(view, sourceFrame);
            if (frame == null || sourceFrame == null || cropResult.View2DMin == null || cropResult.View2DMax == null)
            {
                result.Status = "failed";
                result.Warnings.Add("dimension skipped: view 2D bounds unavailable.");
                result.FailedCount = 4;
                return result;
            }

            XYZ sourceOriginDelta = sourceFrame.Origin - frame.Origin;
            double xShift = sourceOriginDelta.DotProduct(frame.BasisX);
            double yShift = sourceOriginDelta.DotProduct(frame.BasisY);
            double minX = cropResult.View2DMin.X + xShift;
            double maxX = cropResult.View2DMax.X + xShift;
            double minY = cropResult.View2DMin.Y + yShift;
            double maxY = cropResult.View2DMax.Y + yShift;
            if (maxX - minX <= 1e-6 || maxY - minY <= 1e-6)
            {
                result.Status = "failed";
                result.Warnings.Add("dimension skipped: view 2D bounds are too small.");
                result.FailedCount = 4;
                return result;
            }

            double topGridY = maxY + stackOffsetResolution.InnerOffsetFt;
            double topTotalY = topGridY + stackOffsetResolution.ResolvedOffsetFt;
            double rightGridX = maxX + stackOffsetResolution.InnerOffsetFt;
            double rightTotalX = rightGridX + stackOffsetResolution.ResolvedOffsetFt;
            List<CurtainElevationGeometryReference> geometryReferences = CollectCurtainElevationGeometryReferences(doc, wall, view, frame, minX, maxX, minY, maxY);
            List<CurtainElevationGeometryReference> gridLineReferences = CollectCurtainElevationGridLineReferences(doc, wall, view, frame, minX, maxX, minY, maxY);
            result.GeometryReferenceCount = geometryReferences.Count + gridLineReferences.Count;
            result.CurtainGridLineCount = wall.CurtainGrid.GetUGridLineIds().Count + wall.CurtainGrid.GetVGridLineIds().Count;
            result.CurtainGridLineReferenceCount = gridLineReferences.Count;
            if (result.CurtainGridLineReferenceCount < result.CurtainGridLineCount)
                result.CurtainGridLineReferenceFailures.Add($"Only {result.CurtainGridLineReferenceCount} of {result.CurtainGridLineCount} CurtainGridLine elements exposed a usable aligned geometry reference.");
            result.CurtainGridLineReferenceSamples.AddRange(gridLineReferences.Select(r => (object)new
            {
                GridLineId = r.CurtainGridLineId?.GetIdValue(),
                GridLineOrientation = r.IsVertical ? "vertical" : (r.IsHorizontal ? "horizontal" : "other"),
                GeometryObjectType = r.GeometryObjectType,
                ReferenceAvailable = r.Reference != null,
                StableRepresentation = r.StableRepresentation,
                ProjectedCoordinate = Math.Round((r.IsVertical ? r.CenterX : r.CenterY) * 304.8, 1),
                LengthMm = Math.Round(r.Length * 304.8, 1),
                SelectedForDimension = r.SelectedForDimension,
                SelectionReason = r.SelectionReason
            }));
            result.GeometryReferenceCategories = geometryReferences
                .Select(r => r.CategoryName)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            List<CurtainElevationGeometryReference> totalWidthRefs = SelectCurtainElevationBoundaryReferences(geometryReferences, "horizontal", minX, maxX, minY, maxY);
            if (TryCreateCurtainElevationDimensionChain(doc, view, frame, dimensionType, "horizontal", new List<double> { minX, maxX }, totalWidthRefs, minY, maxY, topTotalY, result, false, out ElementId totalWidthId, out string totalWidthSource, out string totalWidthReason))
            {
                result.TotalWidthDimensionId = totalWidthId;
                result.TotalWidthDimensionReferenceSource = totalWidthSource;
                result.CreatedCount++;
            }
            else
            {
                result.FailedCount++;
                result.TotalWidthDimensionReferenceSource = "failed";
                result.Warnings.Add("total width dimension failed: " + totalWidthReason);
            }

            List<CurtainElevationGeometryReference> totalHeightRefs = SelectCurtainElevationBoundaryReferences(geometryReferences, "vertical", minX, maxX, minY, maxY);
            if (TryCreateCurtainElevationDimensionChain(doc, view, frame, dimensionType, "vertical", new List<double> { minY, maxY }, totalHeightRefs, minX, maxX, rightTotalX, result, false, out ElementId totalHeightId, out string totalHeightSource, out string totalHeightReason))
            {
                result.TotalHeightDimensionId = totalHeightId;
                result.TotalHeightDimensionReferenceSource = totalHeightSource;
                result.CreatedCount++;
            }
            else
            {
                result.FailedCount++;
                result.TotalHeightDimensionReferenceSource = "failed";
                result.Warnings.Add("total height dimension failed: " + totalHeightReason);
            }

            List<double> verticalGridXs = GetCurtainElevationGridCoordinates(doc, wall, frame, "vertical", minX, maxX, minY, maxY);
            if (verticalGridXs.Count >= 3)
            {
                List<CurtainElevationGeometryReference> verticalGridRefs = SelectCurtainElevationGridDimensionReferences(geometryReferences, gridLineReferences, "horizontal", verticalGridXs);
                if (TryCreateCurtainElevationDimensionChain(doc, view, frame, dimensionType, "horizontal", verticalGridXs, verticalGridRefs, minY, maxY, topGridY, result, true, out ElementId horizontalGridId, out string horizontalGridSource, out string horizontalGridReason))
                {
                    result.HorizontalGridDimensionId = horizontalGridId;
                    result.HorizontalGridDimensionReferenceSource = horizontalGridSource;
                    result.CreatedCount++;
                }
                else
                {
                    result.FailedCount++;
                    result.HorizontalGridDimensionReferenceSource = "failed";
                    result.Warnings.Add("horizontal grid dimension failed: " + horizontalGridReason);
                }
            }
            else
            {
                result.HorizontalGridDimensionReferenceSource = "skipped";
                result.Warnings.Add("horizontal grid dimension skipped: fewer than 3 grid/boundary X coordinates.");
            }

            List<double> horizontalGridYs = GetCurtainElevationGridCoordinates(doc, wall, frame, "horizontal", minX, maxX, minY, maxY);
            if (horizontalGridYs.Count >= 3)
            {
                List<CurtainElevationGeometryReference> horizontalGridRefs = SelectCurtainElevationGridDimensionReferences(geometryReferences, gridLineReferences, "vertical", horizontalGridYs);
                if (TryCreateCurtainElevationDimensionChain(doc, view, frame, dimensionType, "vertical", horizontalGridYs, horizontalGridRefs, minX, maxX, rightGridX, result, true, out ElementId verticalGridId, out string verticalGridSource, out string verticalGridReason))
                {
                    result.VerticalGridDimensionId = verticalGridId;
                    result.VerticalGridDimensionReferenceSource = verticalGridSource;
                    result.CreatedCount++;
                }
                else
                {
                    result.FailedCount++;
                    result.VerticalGridDimensionReferenceSource = "failed";
                    result.Warnings.Add("vertical grid dimension failed: " + verticalGridReason);
                }
            }
            else
            {
                result.VerticalGridDimensionReferenceSource = "skipped";
                result.Warnings.Add("vertical grid dimension skipped: fewer than 3 grid/boundary Y coordinates.");
            }

            result.AttemptCount = result.CreatedCount + result.FailedCount;
            result.Status = result.CreatedCount > 0
                ? (result.FailedCount > 0 ? "partial" : "created")
                : "failed";
            return result;
        }

        private CurtainElevationDimensionStackOffsetResolution ResolveCurtainElevationDimensionStackOffset(
            DimensionType dimensionType,
            int viewScale,
            double fallbackInnerOffsetFt,
            double fallbackStackOffsetFt)
        {
            const double defaultInnerFallbackOffsetFt = 300.0 / 304.8;
            const double defaultStackFallbackOffsetFt = 250.0 / 304.8;
            const double innerOffsetExtraPaperFt = 3.0 / 304.8;
            double safeInnerFallbackOffsetFt = fallbackInnerOffsetFt > 1e-9
                ? fallbackInnerOffsetFt
                : defaultInnerFallbackOffsetFt;
            double safeStackFallbackOffsetFt = fallbackStackOffsetFt > 1e-9
                ? fallbackStackOffsetFt
                : defaultStackFallbackOffsetFt;
            string fallbackReason = null;

            if (dimensionType == null)
            {
                fallbackReason = "dimension_type_unavailable";
            }
            else if (viewScale <= 0)
            {
                fallbackReason = "view_scale_not_positive";
            }
            else
            {
                try
                {
                    Parameter witnessLineLength =
                        dimensionType.get_Parameter(BuiltInParameter.DIM_WITNS_LINE_EXTENSION_BELOW);
                    if (witnessLineLength == null)
                    {
                        fallbackReason = "witness_line_length_parameter_unavailable";
                    }
                    else if (witnessLineLength.StorageType != StorageType.Double)
                    {
                        fallbackReason = $"witness_line_length_storage_type_{witnessLineLength.StorageType}";
                    }
                    else
                    {
                        double witnessLineLengthPaperFt = witnessLineLength.AsDouble();
                        if (witnessLineLengthPaperFt > 1e-9)
                        {
                            return new CurtainElevationDimensionStackOffsetResolution
                            {
                                ResolvedOffsetFt = witnessLineLengthPaperFt * viewScale,
                                InnerOffsetFt = (witnessLineLengthPaperFt + innerOffsetExtraPaperFt) * viewScale,
                                InnerOffsetExtraPaperFt = innerOffsetExtraPaperFt,
                                InnerOffsetSource = "dimension_type_witness_line_length_plus_3_mm",
                                WitnessLineLengthPaperFt = witnessLineLengthPaperFt,
                                ViewScale = viewScale,
                                Source = "dimension_type_witness_line_length"
                            };
                        }

                        fallbackReason = "witness_line_length_not_positive";
                    }
                }
                catch (Exception ex)
                {
                    fallbackReason = $"witness_line_length_read_failed: {ex.Message}";
                }
            }

            string innerFallbackReason = fallbackReason;
            string stackFallbackReason = fallbackReason;
            if (fallbackInnerOffsetFt <= 1e-9)
                innerFallbackReason = AppendCurtainElevationWarning(innerFallbackReason, "fallback_inner_offset_not_positive_used_default_300_mm");
            if (fallbackStackOffsetFt <= 1e-9)
                stackFallbackReason = AppendCurtainElevationWarning(stackFallbackReason, "fallback_stack_offset_not_positive_used_default_250_mm");

            return new CurtainElevationDimensionStackOffsetResolution
            {
                ResolvedOffsetFt = safeStackFallbackOffsetFt,
                InnerOffsetFt = safeInnerFallbackOffsetFt,
                InnerOffsetExtraPaperFt = innerOffsetExtraPaperFt,
                InnerOffsetSource = "parameter_fallback",
                InnerOffsetFallbackReason = innerFallbackReason,
                WitnessLineLengthPaperFt = null,
                ViewScale = viewScale,
                Source = "parameter_fallback",
                FallbackReason = stackFallbackReason,
                Warning = $"Dimension offsets used fallbacks: dimensionOffsetMm={safeInnerFallbackOffsetFt * 304.8:F3} mm ({innerFallbackReason}); dimensionStackOffsetMm={safeStackFallbackOffsetFt * 304.8:F3} mm ({stackFallbackReason})."
            };
        }

        private void VerifyCurtainElevationDimensionResult(Document doc, View view, CurtainElevationDimensionResult result)
        {
            if (doc == null || view == null || result == null)
                return;

            var ids = new[]
            {
                result.TotalWidthDimensionId,
                result.HorizontalGridDimensionId,
                result.TotalHeightDimensionId,
                result.VerticalGridDimensionId
            };

            result.VerifiedCount = 0;
            foreach (ElementId id in ids)
            {
                if (id == null || id == ElementId.InvalidElementId)
                    continue;

                Element element = doc.GetElement(id);
                Dimension dimension = element as Dimension;
                if (dimension == null)
                {
                    result.CreationErrors.Add($"Dimension id {id.GetIdValue()} was returned but cannot be read back as Dimension.");
                    continue;
                }

                if (dimension.OwnerViewId != view.Id)
                {
                    result.CreationErrors.Add($"Dimension id {id.GetIdValue()} owner view is {dimension.OwnerViewId.GetIdValue()}, expected {view.Id.GetIdValue()}.");
                    continue;
                }

                result.VerifiedCount++;
            }

            if (result.AttemptCount > 0 && result.VerifiedCount == 0)
            {
                result.Status = "failed_no_dimension_created";
                if (result.CreationErrors.Count == 0)
                result.CreationErrors.Add("No created dimension id could be verified in the target elevation view.");
            }
        }

        private CurtainElevationDimensionAttempt TryDiagnoseCurtainGeometryDimension(
            Document doc,
            View view,
            Transform frame,
            DimensionType dimensionType,
            string name,
            string axis,
            List<double> coordinates,
            List<CurtainElevationGeometryReference> geometryReferences,
            double dimensionLineOffset)
        {
            List<double> distinct = NormalizeCurtainElevationDimensionCoordinates(coordinates);
            var attempt = new CurtainElevationDimensionAttempt
            {
                Name = name,
                Method = "geometry_reference",
                ReferenceCount = geometryReferences?.Count ?? 0
            };

            try
            {
                if (distinct.Count < 2)
                {
                    attempt.FailureMessage = "not enough coordinates.";
                    return attempt;
                }

                if (axis == "horizontal")
                {
                    attempt.DimensionLineStart = CurtainElevationPointAt2D(frame, distinct.First(), dimensionLineOffset);
                    attempt.DimensionLineEnd = CurtainElevationPointAt2D(frame, distinct.Last(), dimensionLineOffset);
                }
                else
                {
                    attempt.DimensionLineStart = CurtainElevationPointAt2D(frame, dimensionLineOffset, distinct.First());
                    attempt.DimensionLineEnd = CurtainElevationPointAt2D(frame, dimensionLineOffset, distinct.Last());
                }

                if (geometryReferences == null || geometryReferences.Count < distinct.Count)
                {
                    attempt.FailureMessage = $"not enough geometry references. Need {distinct.Count}, got {geometryReferences?.Count ?? 0}.";
                    return attempt;
                }

                var referenceArray = new ReferenceArray();
                foreach (CurtainElevationGeometryReference geometryReference in geometryReferences)
                {
                    if (geometryReference?.Reference == null)
                    {
                        attempt.FailureMessage = "geometry reference contains null Reference.";
                        return attempt;
                    }

                    referenceArray.Append(geometryReference.Reference);
                }

                Dimension dimension = doc.Create.NewDimension(
                    view,
                    Line.CreateBound(attempt.DimensionLineStart, attempt.DimensionLineEnd),
                    referenceArray);
                if (dimension == null)
                {
                    attempt.FailureMessage = "Revit returned null Dimension.";
                    return attempt;
                }

                ApplyDimensionType(dimension, dimensionType);
                attempt.DimensionId = dimension.Id;
                attempt.OwnerViewId = dimension.OwnerViewId;
                attempt.Success = true;
                return attempt;
            }
            catch (Exception ex)
            {
                attempt.FailureMessage = ex.Message;
                return attempt;
            }
        }

        private CurtainElevationDimensionAttempt TryDiagnoseCurtainReferencePlaneDimension(
            Document doc,
            View view,
            Transform frame,
            DimensionType dimensionType,
            string name,
            string axis,
            List<double> coordinates,
            double minOther,
            double maxOther,
            double dimensionLineOffset,
            List<ElementId> referencePlaneIds,
            out int referenceCount)
        {
            referenceCount = 0;
            List<double> distinct = NormalizeCurtainElevationDimensionCoordinates(coordinates);
            var attempt = new CurtainElevationDimensionAttempt
            {
                Name = name,
                Method = "reference_plane_fallback"
            };

            try
            {
                if (distinct.Count < 2)
                {
                    attempt.FailureMessage = "not enough coordinates.";
                    return attempt;
                }

                double stubMin = minOther;
                double stubMax = maxOther;
                if (Math.Abs(stubMax - stubMin) < 1e-6)
                    stubMax = stubMin + 100.0 / 304.8;

                var referenceArray = new ReferenceArray();
                foreach (double coordinate in distinct)
                {
                    XYZ bubbleEnd;
                    XYZ freeEnd;
                    if (axis == "horizontal")
                    {
                        bubbleEnd = CurtainElevationPointAt2D(frame, coordinate, stubMin);
                        freeEnd = CurtainElevationPointAt2D(frame, coordinate, stubMax);
                    }
                    else
                    {
                        bubbleEnd = CurtainElevationPointAt2D(frame, stubMin, coordinate);
                        freeEnd = CurtainElevationPointAt2D(frame, stubMax, coordinate);
                    }

                    ReferencePlane referencePlane = doc.Create.NewReferencePlane(bubbleEnd, freeEnd, frame.BasisZ, view);
                    if (referencePlane == null)
                    {
                        attempt.FailureMessage = "failed to create ReferencePlane.";
                        return attempt;
                    }

                    referencePlaneIds?.Add(referencePlane.Id);
                    Reference reference = referencePlane.GetReference();
                    if (reference == null)
                    {
                        attempt.FailureMessage = "ReferencePlane.GetReference() returned null.";
                        return attempt;
                    }

                    referenceArray.Append(reference);
                    referenceCount++;
                }

                attempt.ReferenceCount = referenceCount;
                if (axis == "horizontal")
                {
                    attempt.DimensionLineStart = CurtainElevationPointAt2D(frame, distinct.First(), dimensionLineOffset);
                    attempt.DimensionLineEnd = CurtainElevationPointAt2D(frame, distinct.Last(), dimensionLineOffset);
                }
                else
                {
                    attempt.DimensionLineStart = CurtainElevationPointAt2D(frame, dimensionLineOffset, distinct.First());
                    attempt.DimensionLineEnd = CurtainElevationPointAt2D(frame, dimensionLineOffset, distinct.Last());
                }

                Dimension dimension = doc.Create.NewDimension(
                    view,
                    Line.CreateBound(attempt.DimensionLineStart, attempt.DimensionLineEnd),
                    referenceArray);
                if (dimension == null)
                {
                    attempt.FailureMessage = "Revit returned null Dimension.";
                    return attempt;
                }

                ApplyDimensionType(dimension, dimensionType);
                attempt.DimensionId = dimension.Id;
                attempt.OwnerViewId = dimension.OwnerViewId;
                attempt.Success = true;
                return attempt;
            }
            catch (Exception ex)
            {
                attempt.ReferenceCount = referenceCount;
                attempt.FailureMessage = ex.Message;
                return attempt;
            }
        }

        private object ToCurtainElevationDimensionAttemptResult(CurtainElevationDimensionAttempt attempt)
        {
            if (attempt == null)
                return null;

            return new
            {
                Name = attempt.Name,
                Method = attempt.Method,
                ReferenceCount = attempt.ReferenceCount,
                DimensionLineStart = ToCurtainElevationPointMm(attempt.DimensionLineStart),
                DimensionLineEnd = ToCurtainElevationPointMm(attempt.DimensionLineEnd),
                Success = attempt.Success,
                DimensionId = attempt.DimensionId?.GetIdValue(),
                OwnerViewId = attempt.OwnerViewId?.GetIdValue(),
                ExistsAfterCreate = attempt.ExistsAfterCreate,
                FailureMessage = attempt.FailureMessage
            };
        }

        private bool TryCreateCurtainElevationDimensionChain(
            Document doc,
            View view,
            Transform frame,
            DimensionType dimensionType,
            string axis,
            List<double> coordinates,
            List<CurtainElevationGeometryReference> geometryReferences,
            double minOther,
            double maxOther,
            double dimensionLineOffset,
            CurtainElevationDimensionResult aggregate,
            bool allowDetailCurveFallback,
            out ElementId dimensionId,
            out string referenceSource,
            out string reason)
        {
            dimensionId = null;
            referenceSource = null;
            reason = null;

            try
            {
                List<double> distinct = NormalizeCurtainElevationDimensionCoordinates(coordinates);
                if (distinct.Count < 2)
                {
                    reason = "not enough coordinates.";
                    referenceSource = "failed";
                    return false;
                }

                if (TryCreateCurtainElevationGeometryReferenceDimension(
                    doc,
                    view,
                    frame,
                    dimensionType,
                    axis,
                    distinct,
                    geometryReferences,
                    dimensionLineOffset,
                    out dimensionId,
                    out string geometryReason))
                {
                    referenceSource = "geometry_reference";
                    return true;
                }

                if (!allowDetailCurveFallback)
                {
                    reason = "geometry reference dimension failed; detail curve fallback is disabled for this dimension: " + geometryReason;
                    referenceSource = "failed";
                    aggregate.DimensionFallbackReason = AppendCurtainElevationWarning(
                        aggregate.DimensionFallbackReason,
                        reason);
                    return false;
                }

                aggregate.DimensionFallbackReason = AppendCurtainElevationWarning(
                    aggregate.DimensionFallbackReason,
                    $"{axis} grid dimension used invisible detail curve fallback from curtain grid coordinates: {geometryReason}");

                if (TryCreateCurtainElevationDetailCurveFallbackDimension(
                    doc,
                    view,
                    frame,
                    dimensionType,
                    axis,
                    distinct,
                    minOther,
                    maxOther,
                    dimensionLineOffset,
                    aggregate,
                    out dimensionId,
                    out string fallbackReason))
                {
                    referenceSource = "detail_curve_fallback_from_curtain_grid_coordinates";
                    return true;
                }

                reason = $"geometry: {geometryReason}; detail curve fallback: {fallbackReason}";
                referenceSource = "failed";
                return false;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                referenceSource = "failed";
                return false;
            }
        }


        private bool TryApplyExistingInvisibleLineStyle(Document doc, DetailCurve detailCurve)
        {
            if (doc == null || detailCurve == null)
                return false;

            try
            {
                GraphicsStyle style = TryFindExistingInvisibleLineStyle(doc);
                if (style == null)
                    return false;

                detailCurve.LineStyle = style;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private GraphicsStyle TryFindExistingInvisibleLineStyle(Document doc)
        {
            if (doc == null)
                return null;

            try
            {
                var candidates = new List<Category>();

                Category invisibleCategory = Category.GetCategory(doc, BuiltInCategory.OST_InvisibleLines);
                if (invisibleCategory != null)
                    candidates.Add(invisibleCategory);

                try
                {
                    Category settingsInvisibleCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_InvisibleLines);
                    if (settingsInvisibleCategory != null && !candidates.Any(c => c.Id == settingsInvisibleCategory.Id))
                        candidates.Add(settingsInvisibleCategory);
                }
                catch
                {
                    // Some Revit builds expose invisible lines only as a Lines subcategory.
                }

                try
                {
                    Category linesCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                    ElementId invisibleCategoryId = new ElementId(BuiltInCategory.OST_InvisibleLines);
                    if (linesCategory != null)
                    {
                        foreach (Category subCategory in linesCategory.SubCategories)
                        {
                            if (subCategory != null && subCategory.Id == invisibleCategoryId)
                                candidates.Add(subCategory);
                        }
                    }
                }
                catch
                {
                    // Best effort. Do not fall back to name guessing here.
                }

                foreach (Category category in candidates)
                {
                    GraphicsStyle style = category?.GetGraphicsStyle(GraphicsStyleType.Projection);
                    if (style != null)
                        return style;
                }
            }
            catch
            {
            }

            return null;
        }

        private bool TryCreateCurtainElevationDetailCurveFallbackDimension(
            Document doc,
            View view,
            Transform frame,
            DimensionType dimensionType,
            string axis,
            List<double> distinct,
            double minOther,
            double maxOther,
            double dimensionLineOffset,
            CurtainElevationDimensionResult aggregate,
            out ElementId dimensionId,
            out string reason)
        {
            dimensionId = null;
            reason = null;
            var createdReferenceCurves = new List<DetailCurve>();

            try
            {
                var referenceArray = new ReferenceArray();
                double stubMin = minOther;
                double stubMax = maxOther;
                if (Math.Abs(stubMax - stubMin) < 1e-6)
                    stubMax = stubMin + (100.0 / 304.8);

                foreach (double coordinate in distinct)
                {
                    Line referenceLine;
                    if (axis == "horizontal")
                    {
                        referenceLine = Line.CreateBound(
                            CurtainElevationPointAt2D(frame, coordinate, stubMin),
                            CurtainElevationPointAt2D(frame, coordinate, stubMax));
                    }
                    else
                    {
                        referenceLine = Line.CreateBound(
                            CurtainElevationPointAt2D(frame, stubMin, coordinate),
                            CurtainElevationPointAt2D(frame, stubMax, coordinate));
                    }

                    DetailCurve detailCurve = doc.Create.NewDetailCurve(view, referenceLine);
                    if (detailCurve == null)
                    {
                        reason = "failed to create reference detail curve.";
                        DeleteCurtainElevationDetailCurves(doc, createdReferenceCurves);
                        return false;
                    }

                    createdReferenceCurves.Add(detailCurve);
                    Reference reference = detailCurve.GeometryCurve?.Reference;
                    if (reference == null)
                    {
                        reason = "reference detail curve has no Reference before applying invisible line style.";
                        DeleteCurtainElevationDetailCurves(doc, createdReferenceCurves);
                        return false;
                    }

                    referenceArray.Append(reference);
                }

                Line dimensionLine;
                if (axis == "horizontal")
                {
                    dimensionLine = Line.CreateBound(
                        CurtainElevationPointAt2D(frame, distinct.First(), dimensionLineOffset),
                        CurtainElevationPointAt2D(frame, distinct.Last(), dimensionLineOffset));
                }
                else
                {
                    dimensionLine = Line.CreateBound(
                        CurtainElevationPointAt2D(frame, dimensionLineOffset, distinct.First()),
                        CurtainElevationPointAt2D(frame, dimensionLineOffset, distinct.Last()));
                }

                Dimension dimension = doc.Create.NewDimension(view, dimensionLine, referenceArray);
                if (dimension == null)
                {
                    reason = "Revit returned null Dimension.";
                    DeleteCurtainElevationDetailCurves(doc, createdReferenceCurves);
                    return false;
                }

                ApplyDimensionType(dimension, dimensionType);

                GraphicsStyle invisibleLineStyle = TryFindExistingInvisibleLineStyle(doc);
                bool invisibleLineStyleApplied = invisibleLineStyle != null;
                foreach (DetailCurve detailCurve in createdReferenceCurves)
                {
                    aggregate.ReferenceCurveIds.Add(detailCurve.Id);

                    if (invisibleLineStyle == null)
                    {
                        invisibleLineStyleApplied = false;
                        continue;
                    }

                    try
                    {
                        detailCurve.LineStyle = invisibleLineStyle;
                    }
                    catch
                    {
                        invisibleLineStyleApplied = false;
                    }
                }

                if (!invisibleLineStyleApplied)
                {
                    aggregate.Warnings.Add("Grid dimension detail-curve fallback succeeded, but Revit did not expose/apply BuiltInCategory.OST_InvisibleLines to the helper curves.");
                    aggregate.DimensionFallbackReason = AppendCurtainElevationWarning(
                        aggregate.DimensionFallbackReason,
                        "detail curve fallback dimension succeeded, invisible line style was not applied.");
                }

                LastCurtainElevationDimensionTypeId = dimensionType.Id.GetIdValue();
                dimensionId = dimension.Id;
                return true;
            }
            catch (Exception ex)
            {
                DeleteCurtainElevationDetailCurves(doc, createdReferenceCurves);
                reason = ex.Message;
                return false;
            }
        }

        private void DeleteCurtainElevationDetailCurves(Document doc, IEnumerable<DetailCurve> detailCurves)
        {
            if (doc == null || detailCurves == null)
                return;

            foreach (DetailCurve detailCurve in detailCurves)
            {
                try
                {
                    if (detailCurve != null && detailCurve.Id != ElementId.InvalidElementId && doc.GetElement(detailCurve.Id) != null)
                        doc.Delete(detailCurve.Id);
                }
                catch
                {
                    // Best effort cleanup for failed fallback references.
                }
            }
        }

        private bool TryCreateCurtainElevationGeometryReferenceDimension(
            Document doc,
            View view,
            Transform frame,
            DimensionType dimensionType,
            string axis,
            List<double> coordinates,
            List<CurtainElevationGeometryReference> geometryReferences,
            double dimensionLineOffset,
            out ElementId dimensionId,
            out string reason)
        {
            dimensionId = null;
            reason = null;

            try
            {
                if (geometryReferences == null || geometryReferences.Count < coordinates.Count)
                {
                    reason = $"not enough geometry references. Need {coordinates.Count}, got {geometryReferences?.Count ?? 0}.";
                    return false;
                }

                var referenceArray = new ReferenceArray();
                foreach (CurtainElevationGeometryReference geometryReference in geometryReferences)
                {
                    if (geometryReference?.Reference == null)
                    {
                        reason = "geometry reference contains null Reference.";
                        return false;
                    }

                    referenceArray.Append(geometryReference.Reference);
                }

                Line dimensionLine;
                if (axis == "horizontal")
                {
                    dimensionLine = Line.CreateBound(
                        CurtainElevationPointAt2D(frame, coordinates.First(), dimensionLineOffset),
                        CurtainElevationPointAt2D(frame, coordinates.Last(), dimensionLineOffset));
                }
                else
                {
                    dimensionLine = Line.CreateBound(
                        CurtainElevationPointAt2D(frame, dimensionLineOffset, coordinates.First()),
                        CurtainElevationPointAt2D(frame, dimensionLineOffset, coordinates.Last()));
                }

                Dimension dimension = doc.Create.NewDimension(view, dimensionLine, referenceArray);
                if (dimension == null)
                {
                    reason = "Revit returned null Dimension for geometry references.";
                    return false;
                }

                ApplyDimensionType(dimension, dimensionType);
                LastCurtainElevationDimensionTypeId = dimensionType.Id.GetIdValue();
                dimensionId = dimension.Id;
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private List<double> GetCurtainElevationGridCoordinates(
            Document doc,
            Wall wall,
            Transform frame,
            string targetOrientation,
            double minX,
            double maxX,
            double minY,
            double maxY)
        {
            var values = new List<double>();
            if (targetOrientation == "vertical")
            {
                values.Add(minX);
                values.Add(maxX);
            }
            else
            {
                values.Add(minY);
                values.Add(maxY);
            }

            try
            {
                CurtainGrid grid = wall?.CurtainGrid;
                if (grid == null)
                    return NormalizeCurtainElevationDimensionCoordinates(values);

                var gridIds = new List<ElementId>();
                gridIds.AddRange(grid.GetUGridLineIds());
                gridIds.AddRange(grid.GetVGridLineIds());

                foreach (ElementId id in gridIds)
                {
                    CurtainGridLine gridLine = doc.GetElement(id) as CurtainGridLine;
                    Curve curve = gridLine?.FullCurve;
                    if (curve == null)
                        continue;

                    List<XYZ> points = curve.Tessellate()?.ToList() ?? new List<XYZ>();
                    if (points.Count == 0)
                    {
                        points.Add(curve.GetEndPoint(0));
                        points.Add(curve.GetEndPoint(1));
                    }

                    var local = points.Select(p => frame.Inverse.OfPoint(p)).ToList();
                    double gxMin = local.Min(p => p.X);
                    double gxMax = local.Max(p => p.X);
                    double gyMin = local.Min(p => p.Y);
                    double gyMax = local.Max(p => p.Y);
                    double dx = gxMax - gxMin;
                    double dy = gyMax - gyMin;

                    if (targetOrientation == "vertical" && dy >= dx)
                    {
                        double x = local.Average(p => p.X);
                        if (x > minX + 1e-4 && x < maxX - 1e-4)
                            values.Add(x);
                    }
                    else if (targetOrientation == "horizontal" && dx > dy)
                    {
                        double y = local.Average(p => p.Y);
                        if (y > minY + 1e-4 && y < maxY - 1e-4)
                            values.Add(y);
                    }
                }
            }
            catch
            {
                // Grid dimensions are optional; total dimensions still represent the curtain elevation.
            }

            return NormalizeCurtainElevationDimensionCoordinates(values);
        }

        private List<CurtainElevationGeometryReference> CollectCurtainElevationGeometryReferences(
            Document doc,
            Wall wall,
            View view,
            Transform frame,
            double minX,
            double maxX,
            double minY,
            double maxY)
        {
            var references = new List<CurtainElevationGeometryReference>();
            if (doc == null || wall == null || view == null || frame == null)
                return references;

            var options = new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = false
            };
            options.View = view;

            foreach (ElementId id in GetCurtainElevationElementIds(wall, includeHostWall: false))
            {
                Element element = doc.GetElement(id);
                if (element == null)
                    continue;

                try
                {
                    GeometryElement geometry = element.get_Geometry(options);
                    CollectCurtainElevationGeometryReferences(geometry, references, frame, Transform.Identity, element);
                }
                catch
                {
                    // Some curtain sub-elements do not expose reference-bearing geometry in elevation views.
                }
            }

            double tolerance = 5.0 / 304.8;
            return references
                .Where(r => r.Reference != null)
                .Where(r => r.Length > tolerance)
                .Where(r => r.MaxX >= minX - tolerance && r.MinX <= maxX + tolerance)
                .Where(r => r.MaxY >= minY - tolerance && r.MinY <= maxY + tolerance)
                .GroupBy(r => $"{r.ElementId.GetIdValue()}|{Math.Round(r.CenterX / tolerance)}|{Math.Round(r.CenterY / tolerance)}|{r.IsVertical}|{r.IsHorizontal}")
                .Select(g => g.OrderByDescending(r => r.Length).First())
                .ToList();
        }

        private void CollectCurtainElevationGeometryReferences(
            GeometryElement geometry,
            List<CurtainElevationGeometryReference> references,
            Transform viewFrame,
            Transform geometryTransform,
            Element sourceElement)
        {
            if (geometry == null || references == null || viewFrame == null || sourceElement == null)
                return;

            foreach (GeometryObject obj in geometry)
            {
                if (obj == null)
                    continue;

                if (obj is GeometryInstance instance)
                {
                    try
                    {
                        Transform nextTransform = geometryTransform.Multiply(instance.Transform);
                        CollectCurtainElevationGeometryReferences(instance.GetSymbolGeometry(), references, viewFrame, nextTransform, sourceElement);
                    }
                    catch
                    {
                        try
                        {
                            CollectCurtainElevationGeometryReferences(instance.GetInstanceGeometry(), references, viewFrame, geometryTransform, sourceElement);
                        }
                        catch
                        {
                            // Ignore geometry instance extraction failures.
                        }
                    }
                    continue;
                }

                if (obj is Curve curve)
                {
                    AddCurtainElevationGeometryReference(curve.Reference, curve, references, viewFrame, geometryTransform, sourceElement);
                    continue;
                }

                if (obj is Solid solid && solid.Edges != null)
                {
                    foreach (Edge edge in solid.Edges)
                    {
                        try
                        {
                            AddCurtainElevationGeometryReference(edge.Reference, edge.AsCurve(), references, viewFrame, geometryTransform, sourceElement);
                        }
                        catch
                        {
                            // Ignore malformed edge references.
                        }
                    }
                }
            }
        }

        private void AddCurtainElevationGeometryReference(
            Reference reference,
            Curve curve,
            List<CurtainElevationGeometryReference> references,
            Transform viewFrame,
            Transform geometryTransform,
            Element sourceElement)
        {
            if (reference == null || curve == null || references == null || viewFrame == null || sourceElement == null || !curve.IsBound)
                return;

            try
            {
                XYZ start = geometryTransform.OfPoint(curve.GetEndPoint(0));
                XYZ end = geometryTransform.OfPoint(curve.GetEndPoint(1));
                XYZ localStart = viewFrame.Inverse.OfPoint(start);
                XYZ localEnd = viewFrame.Inverse.OfPoint(end);
                double dx = Math.Abs(localEnd.X - localStart.X);
                double dy = Math.Abs(localEnd.Y - localStart.Y);
                double tolerance = 3.0 / 304.8;
                bool isVertical = dx <= tolerance && dy > tolerance;
                bool isHorizontal = dy <= tolerance && dx > tolerance;
                if (!isVertical && !isHorizontal)
                    return;

                references.Add(new CurtainElevationGeometryReference
                {
                    Reference = reference,
                    ElementId = sourceElement.Id,
                    CategoryName = sourceElement.Category?.Name,
                    Start = start,
                    End = end,
                    MinX = Math.Min(localStart.X, localEnd.X),
                    MaxX = Math.Max(localStart.X, localEnd.X),
                    MinY = Math.Min(localStart.Y, localEnd.Y),
                    MaxY = Math.Max(localStart.Y, localEnd.Y),
                    Length = Math.Sqrt(dx * dx + dy * dy),
                    IsVertical = isVertical,
                    IsHorizontal = isHorizontal
                });
            }
            catch
            {
                // Reference classification is best effort; invalid curves are ignored.
            }
        }

        private List<CurtainElevationGeometryReference> SelectCurtainElevationBoundaryReferences(
            List<CurtainElevationGeometryReference> references,
            string dimensionAxis,
            double minX,
            double maxX,
            double minY,
            double maxY)
        {
            double tolerance = 25.0 / 304.8;
            if (dimensionAxis == "horizontal")
            {
                List<CurtainElevationGeometryReference> verticals = references.Where(r => r.IsVertical).ToList();
                CurtainElevationGeometryReference left = verticals
                    .Where(r => Math.Abs(r.CenterX - minX) <= tolerance)
                    .OrderBy(r => Math.Abs(r.CenterX - minX))
                    .ThenByDescending(r => r.Length)
                    .FirstOrDefault();
                CurtainElevationGeometryReference right = verticals
                    .Where(r => Math.Abs(r.CenterX - maxX) <= tolerance)
                    .OrderBy(r => Math.Abs(r.CenterX - maxX))
                    .ThenByDescending(r => r.Length)
                    .FirstOrDefault();
                return left != null && right != null ? new List<CurtainElevationGeometryReference> { left, right } : new List<CurtainElevationGeometryReference>();
            }

            List<CurtainElevationGeometryReference> horizontals = references.Where(r => r.IsHorizontal).ToList();
            CurtainElevationGeometryReference bottom = horizontals
                .Where(r => Math.Abs(r.CenterY - minY) <= tolerance)
                .OrderBy(r => Math.Abs(r.CenterY - minY))
                .ThenByDescending(r => r.Length)
                .FirstOrDefault();
            CurtainElevationGeometryReference top = horizontals
                .Where(r => Math.Abs(r.CenterY - maxY) <= tolerance)
                .OrderBy(r => Math.Abs(r.CenterY - maxY))
                .ThenByDescending(r => r.Length)
                .FirstOrDefault();
            return bottom != null && top != null ? new List<CurtainElevationGeometryReference> { bottom, top } : new List<CurtainElevationGeometryReference>();
        }

        private List<CurtainElevationGeometryReference> SelectCurtainElevationGridDimensionReferences(
            List<CurtainElevationGeometryReference> boundaryReferences,
            List<CurtainElevationGeometryReference> gridLineReferences,
            string dimensionAxis,
            List<double> coordinates)
        {
            var result = new List<CurtainElevationGeometryReference>();
            List<double> distinct = NormalizeCurtainElevationDimensionCoordinates(coordinates);
            if (distinct.Count == 0)
                return result;

            double tolerance = 10.0 / 304.8;
            double minCoordinate = distinct.First();
            double maxCoordinate = distinct.Last();

            foreach (double coordinate in distinct)
            {
                bool isBoundary = Math.Abs(coordinate - minCoordinate) <= tolerance || Math.Abs(coordinate - maxCoordinate) <= tolerance;
                List<CurtainElevationGeometryReference> candidates;
                if (isBoundary)
                {
                    candidates = dimensionAxis == "horizontal"
                        ? boundaryReferences.Where(r => r.IsVertical).ToList()
                        : boundaryReferences.Where(r => r.IsHorizontal).ToList();
                }
                else
                {
                    candidates = dimensionAxis == "horizontal"
                        ? gridLineReferences.Where(r => r.IsVertical).ToList()
                        : gridLineReferences.Where(r => r.IsHorizontal).ToList();
                }

                CurtainElevationGeometryReference match = candidates
                    .Where(r => Math.Abs((dimensionAxis == "horizontal" ? r.CenterX : r.CenterY) - coordinate) <= tolerance)
                    .OrderBy(r => Math.Abs((dimensionAxis == "horizontal" ? r.CenterX : r.CenterY) - coordinate))
                    .ThenByDescending(r => r.Length)
                    .FirstOrDefault();

                if (match == null || result.Any(r => r.Reference == match.Reference))
                    return new List<CurtainElevationGeometryReference>();

                result.Add(match);
            }

            return result;
        }

        private List<CurtainElevationGeometryReference> CollectCurtainElevationGridLineReferences(
            Document doc,
            Wall wall,
            View view,
            Transform frame,
            double minX,
            double maxX,
            double minY,
            double maxY)
        {
            var selected = new List<CurtainElevationGeometryReference>();
            CurtainGrid grid = wall?.CurtainGrid;
            if (doc == null || grid == null || frame == null)
                return selected;

            // CurtainGridLine references must come from the element geometry without binding
            // extraction to the target elevation view's visibility/crop state.
            var options = new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine
            };

            var gridIds = new List<ElementId>();
            gridIds.AddRange(grid.GetUGridLineIds());
            gridIds.AddRange(grid.GetVGridLineIds());
            double tolerance = 5.0 / 304.8;

            foreach (ElementId id in gridIds.Distinct())
            {
                CurtainGridLine gridLine = doc.GetElement(id) as CurtainGridLine;
                if (gridLine == null)
                    continue;

                try
                {
                    Curve fullCurve = gridLine.FullCurve;
                    if (fullCurve == null || !fullCurve.IsBound)
                        continue;

                    XYZ fullStart = fullCurve.GetEndPoint(0);
                    XYZ fullEnd = fullCurve.GetEndPoint(1);
                    XYZ fullLocalStart = frame.Inverse.OfPoint(fullStart);
                    XYZ fullLocalEnd = frame.Inverse.OfPoint(fullEnd);
                    XYZ fullDirection = fullLocalEnd - fullLocalStart;
                    if (fullDirection.GetLength() < tolerance)
                        continue;
                    fullDirection = fullDirection.Normalize();

                    var candidates = new List<CurtainElevationGeometryReference>();
                    // Prefer native CurtainGridLine curve references before solid geometry.
                    try
                    {
                        AddCurtainElevationGeometryReference(fullCurve.Reference, fullCurve, candidates, frame, Transform.Identity, gridLine);
                        CurveArray segmentCurves = gridLine.AllSegmentCurves;
                        if (segmentCurves != null)
                            foreach (Curve segment in segmentCurves)
                                AddCurtainElevationGeometryReference(segment?.Reference, segment, candidates, frame, Transform.Identity, gridLine);
                    }
                    catch
                    {
                        // Some Revit versions expose FullCurve but not its Reference.
                    }
                    GeometryElement geometry = gridLine.get_Geometry(options);
                    CollectCurtainElevationGeometryReferences(geometry, candidates, frame, Transform.Identity, gridLine);

                    candidates = candidates
                        .Where(r => r.Reference != null && r.Length > tolerance)
                        .Where(r => r.MaxX >= minX - tolerance && r.MinX <= maxX + tolerance)
                        .Where(r => r.MaxY >= minY - tolerance && r.MinY <= maxY + tolerance)
                        .Where(r =>
                        {
                            XYZ direction = frame.Inverse.OfVector(r.End - r.Start);
                            if (direction.GetLength() < tolerance)
                                return false;
                            double alignment = Math.Abs(direction.Normalize().DotProduct(fullDirection));
                            return alignment >= 0.98;
                        })
                        .OrderByDescending(r => r.Length)
                        .ToList();

                    CurtainElevationGeometryReference best = candidates.FirstOrDefault();
                    if (best == null)
                        continue;

                    best.CurtainGridLineId = id;
                    best.GeometryObjectType = best.GeometryObjectType ?? "Curve";
                    best.SelectedForDimension = true;
                    best.SelectionReason = "longest_reference_aligned_with_full_curve";
                    try
                    {
                        best.StableRepresentation = best.Reference.ConvertToStableRepresentation(doc);
                    }
                    catch
                    {
                        best.StableRepresentation = null;
                    }
                    selected.Add(best);
                }
                catch
                {
                    // A grid line can exist without reference-bearing project geometry.
                }
            }

            return selected
                .GroupBy(r => r.CurtainGridLineId ?? r.ElementId)
                .Select(g => g.OrderByDescending(r => r.Length).First())
                .ToList();
        }
        private List<double> NormalizeCurtainElevationDimensionCoordinates(IEnumerable<double> coordinates)
        {
            const double tolerance = 1.0 / 304.8;
            var result = new List<double>();
            foreach (double coordinate in coordinates.Where(c => !double.IsNaN(c) && !double.IsInfinity(c)).OrderBy(c => c))
            {
                if (result.Count == 0 || Math.Abs(result.Last() - coordinate) > tolerance)
                    result.Add(coordinate);
            }

            return result;
        }

        private XYZ CurtainElevationPointAt2D(Transform frame, double x, double y)
        {
            return frame.Origin + frame.BasisX * x + frame.BasisY * y;
        }

        private Transform GetCurtainElevationDimensionFrame(ViewSection view, Transform sourceFrame)
        {
            if (view == null || sourceFrame == null)
                return sourceFrame;

            Transform frame = Transform.Identity;
            frame.Origin = view.Origin ?? sourceFrame.Origin;
            frame.BasisX = NormalizeOrFallback(view.RightDirection, sourceFrame.BasisX);
            frame.BasisY = NormalizeOrFallback(view.UpDirection, sourceFrame.BasisY);
            frame.BasisZ = NormalizeOrFallback(view.ViewDirection, sourceFrame.BasisZ);
            return frame;
        }

        private object BuildCurtainElevationDimensionTypePrompt(string selectionMode)
        {
            return new
            {
                Success = false,
                WorkflowState = "awaiting_dimension_type_selection",
                NextAction = "call_list_dimension_types",
                RequiresUserInput = true,
                NoModelChanges = true,
                ElevationsCreated = false,
                MissingFields = new[] { "dimensionTypeId" },
                DimensionTypeSelectionMode = selectionMode,
                PromptToUser = "Please call list_dimension_types and provide dimensionTypeId or dimensionTypeName.",
                Message = "Dimension type selection is required; no curtain elevation views were created."
            };
        }


        private string AppendCurtainElevationWarning(string current, string warning)
        {
            if (string.IsNullOrWhiteSpace(warning)) return current;
            return string.IsNullOrWhiteSpace(current) ? warning : current + " " + warning;
        }
    }
}
