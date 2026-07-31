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
        private class CurtainLevelReferenceInfo
        {
            public IdType? ElementId { get; set; }
            public string ReferenceSource { get; set; }
            public string ElementReferenceType { get; set; }
            public string StableRepresentation { get; set; }
            public bool StableRoundTripPassed { get; set; }
            public string Failure { get; set; }
        }
        private class CurtainLevelRuntimeAttempt
        {
            public string Name { get; set; }
            public string ViewState { get; set; }
            public string ReferenceStrategy { get; set; }
            public int ExpectedReferenceCount { get; set; }
            public List<CurtainLevelReferenceInfo> InputReferences { get; set; } = new List<CurtainLevelReferenceInfo>();
            public double LevelYmm { get; set; }
            public double CurtainBottomYmm { get; set; }
            public double CurtainTopYmm { get; set; }
            public double DimensionLineXmm { get; set; }
            public List<double> ExpectedSegmentValuesMm { get; set; } = new List<double>();
            public List<double> ActualSegmentValuesMm { get; set; } = new List<double>();
            public bool? SegmentValuesPassed { get; set; }
            public int? PreCommitReferenceCount { get; set; }
            public bool ReferenceAcquired { get; set; }
            public bool StableRoundTripPassed { get; set; }
            public bool DimensionCreated { get; set; }
            public bool TransactionCommitted { get; set; }
            public IdType? DimensionId { get; set; }
            public IdType? OwnerViewId { get; set; }
            public bool? PreCommitAreReferencesAvailable { get; set; }
            public bool? PostCommitAreReferencesAvailable { get; set; }
            public int? PostCommitReferenceCount { get; set; }
            public int? NumberOfSegments { get; set; }
            public IdType? HelperCurveId { get; set; }
            public IdType? HelperOwnerViewId { get; set; }
            public IdType? HelperLineStyleId { get; set; }
            public bool? HelperUsesInvisibleLines { get; set; }
            public string FailureStage { get; set; }
            public string PostCommitValidationMode { get; set; }
            public string ExceptionType { get; set; }
            public string ExceptionMessage { get; set; }
            public string ExceptionStackTrace { get; set; }
            public bool Passed { get; set; }
        }
        private class CurtainLevelProductionAttempt
        {
            public string ViewState { get; set; }
            public string FailureStage { get; set; }
            public string ExceptionType { get; set; }
            public string ExceptionMessage { get; set; }
            public string ExceptionStackTrace { get; set; }
            public string Status { get; set; }
            public string LevelOffsetDimensionMode { get; set; }
            public string LevelOffsetDimensionStatus { get; set; }
            public string LevelOffsetDimensionReferenceSource { get; set; }
            public IdType? TotalHeightDimensionId { get; set; }
            public IdType? LevelOffsetDimensionElementId { get; set; }
            public bool? TotalHeightReferencesAvailable { get; set; }
            public bool? LevelOffsetReferencesAvailable { get; set; }
            public string PostCommitValidationMode { get; set; }
            public bool? SegmentValuesPassed { get; set; }
            public List<object> PostCommitDimensionValidation { get; set; } = new List<object>();
            public List<double> ExpectedSegmentValuesMm { get; set; } = new List<double>();
            public List<double> ActualTotalHeightSegmentValuesMm { get; set; } = new List<double>();
            public List<double> ActualLevelOffsetSegmentValuesMm { get; set; } = new List<double>();
            public List<string> Warnings { get; set; } = new List<string>();
            public bool Passed { get; set; }
        }
        private object DiagnoseCurtainWallElevationLevelOffsetRuntime(JObject parameters)
        {
            Document doc=_uiApp.ActiveUIDocument.Document;
            UIDocument uidoc=_uiApp.ActiveUIDocument;
            IdType? viewId=parameters["viewId"]?.Value<IdType?>();
            ViewSection view=viewId.HasValue?doc.GetElement(new ElementId(viewId.Value)) as ViewSection:uidoc.ActiveView as ViewSection;
            if(view==null||view.IsTemplate||view.ViewType!=ViewType.Elevation) throw new Exception("level_offset runtime test requires a valid elevation viewId or active elevation.");
            IdType? wallId=parameters["wallId"]?.Value<IdType?>();
            Wall wall=wallId.HasValue?doc.GetElement(new ElementId(wallId.Value)) as Wall:ResolveSingleSelectedCurtainWall(uidoc,doc);
            if(wall==null||wall.CurtainGrid==null) throw new Exception("level_offset runtime test requires wallId or exactly one selected curtain wall; first-wall fallback is disabled.");
            var warnings=new List<string>();
            CurtainElevationDimensionTypeResolution typeResolution=ResolveCurtainElevationDimensionType(doc,parameters,warnings);
            DimensionType dimensionType=typeResolution.DimensionType;
            if(dimensionType==null) throw new Exception("level_offset runtime test could not resolve a DimensionType.");
            double inner=(parameters["dimensionOffsetMm"]?.Value<double>()??300.0)/304.8;
            double stack=(parameters["dimensionStackOffsetMm"]?.Value<double>()??250.0)/304.8;
            CurtainElevationDimensionStackOffsetResolution offsets=ResolveCurtainElevationDimensionStackOffset(dimensionType,view.Scale,inner,stack);
            if(!string.IsNullOrWhiteSpace(offsets.Warning)) warnings.Add(offsets.Warning);
            View original=uidoc.ActiveView;
            var originalTabs=new HashSet<IdType>(uidoc.GetOpenUIViews().Select(x=>x.ViewId.GetIdValue()));
            var attempts=new List<CurtainLevelRuntimeAttempt>();
            var production=new List<CurtainLevelProductionAttempt>();
            var tempIds=new List<ElementId>();
            var failures=new List<string>();
            bool inactiveEstablished=original?.Id!=view.Id,activated=false;
            string inactiveFailure=null,activationFailure=null;
            double minX=0,maxX=0,minY=0,maxY=0,levelY=0,leftX=0;
            Transform frame=null;
            List<CurtainElevationGeometryReference> heightRefs=null;
            IdType? levelId=null; string levelName=null;
            using(TransactionGroup group=new TransactionGroup(doc,"Diagnose curtain Level offset dimensions (Rollback)"))
            {
                group.Start();
                try
                {
                    using(Transaction setup=TransactionHelper.Begin(doc,"Prepare curtain Level offset runtime test"))
                    {
                        setup.Start();
                        XYZ mid=(wall.Location as LocationCurve)?.Curve?.Evaluate(0.5,true);
                        CurtainElevationCropResult crop=ConfigureCurtainElevationCrop(doc,view,wall,mid,view.Origin,0,0,1200.0/304.8);
                        doc.Regenerate();
                        Transform source=GetCurtainElevationView2DFrame(view,view.CropBox?.Transform);
                        frame=GetCurtainElevationDimensionFrame(view,source);
                        if(frame==null||source==null||crop?.View2DMin==null||crop.View2DMax==null) throw new InvalidOperationException("Production crop or dimension frame was unavailable.");
                        XYZ delta=source.Origin-frame.Origin;
                        double xs=delta.DotProduct(frame.BasisX),ys=delta.DotProduct(frame.BasisY);
                        minX=(crop.WallBoundaryMinXFt??crop.View2DMin.X)+xs;
                        maxX=(crop.WallBoundaryMaxXFt??crop.View2DMax.X)+xs;
                        minY=(crop.CurtainGeometryMinYFt??crop.View2DMin.Y)+ys;
                        maxY=(crop.CurtainGeometryMaxYFt??crop.View2DMax.Y)+ys;
                        if(!crop.CropBottomLevelViewYFt.HasValue) throw new InvalidOperationException("Projected wall Level Y was unavailable.");
                        levelY=crop.CropBottomLevelViewYFt.Value+ys;
                        Level level=doc.GetElement(wall.LevelId) as Level;
                        if(level==null) throw new InvalidOperationException("wall.LevelId did not resolve to Level.");
                        levelId=level.Id.GetIdValue(); levelName=level.Name;
                        var geometry=CollectCurtainElevationGeometryReferences(doc,wall,view,frame,minX,maxX,minY,maxY);
                        heightRefs=SelectCurtainElevationBoundaryReferences(geometry,"vertical",minX,maxX,minY,maxY);
                        if(heightRefs.Count!=2) throw new InvalidOperationException($"Expected 2 curtain bottom/top references, got {heightRefs.Count}.");
                        leftX=minX-offsets.InnerOffsetFt-offsets.ResolvedOffsetFt;
                        setup.Commit();
                    }
                    if(uidoc.ActiveView?.Id==view.Id)
                    {
                        View alternate=ResolveCurtainElevationAlternateDiagnosticView(uidoc,doc,view);
                        if(alternate==null) inactiveFailure="No alternate graphical view was available.";
                        else try{uidoc.ActiveView=alternate;uidoc.RefreshActiveView();inactiveEstablished=uidoc.ActiveView?.Id!=view.Id;}catch(Exception ex){inactiveFailure=ex.Message;}
                    }
                    if(inactiveEstablished) RunCurtainLevelRuntimeMatrix(doc,view,wall,frame,dimensionType,minX,maxX,minY,maxY,levelY,leftX,offsets.ResolvedOffsetFt,heightRefs,"inactive",attempts,production,tempIds);
                    try{uidoc.ActiveView=view;uidoc.RefreshActiveView();activated=uidoc.ActiveView?.Id==view.Id;if(!activated)activationFailure="ActiveView did not change.";}catch(Exception ex){activationFailure=ex.Message;}
                    if(activated) RunCurtainLevelRuntimeMatrix(doc,view,wall,frame,dimensionType,minX,maxX,minY,maxY,levelY,leftX,offsets.ResolvedOffsetFt,heightRefs,"active",attempts,production,tempIds);
                }
                catch(Exception ex){failures.Add(ex.ToString());}
                finally
                {
                    try{if(original!=null&&doc.GetElement(original.Id)!=null){uidoc.ActiveView=original;uidoc.RefreshActiveView();}}catch(Exception ex){failures.Add("Restore ActiveView failed: "+ex.Message);}
                    if(group.GetStatus()==TransactionStatus.Started) group.RollBack();
                }
            }
            bool cleanup=tempIds.Where(id=>id!=null&&id!=ElementId.InvalidElementId).Distinct().All(id=>doc.GetElement(id)==null);
            foreach(UIView tab in uidoc.GetOpenUIViews().ToList())
            {
                if(originalTabs.Contains(tab.ViewId.GetIdValue())||tab.ViewId==uidoc.ActiveView?.Id) continue;
                try{tab.Close();}catch(Exception ex){failures.Add("Close diagnostic tab failed: "+ex.Message);}
            }
            return new
            {
                TestMode="level_offset",WallId=wall.Id.GetIdValue(),ViewId=view.Id.GetIdValue(),ViewName=view.Name,
                LevelId=levelId,LevelName=levelName,LevelYmm=Math.Round(levelY*304.8,4),CurtainBottomYmm=Math.Round(minY*304.8,4),
                CurtainTopYmm=Math.Round(maxY*304.8,4),SignedBottomToLevelMm=Math.Round((minY-levelY)*304.8,4),
                DimensionTypeId=dimensionType.Id.GetIdValue(),DimensionTypeName=dimensionType.Name,DimensionTypeSource=typeResolution.Source,
                WasViewOpen=originalTabs.Contains(view.Id.GetIdValue()),WasViewActive=original?.Id==view.Id,
                InactiveControlEstablished=inactiveEstablished,InactiveControlFailure=inactiveFailure,ActivationSucceeded=activated,ActivationFailure=activationFailure,
                RuntimeAttempts=attempts,ProductionPathAttempts=production,FirstFailure=attempts.FirstOrDefault(x=>!x.Passed),
                FirstProductionFailure=production.FirstOrDefault(x=>!x.Passed),TemporaryElementIds=tempIds.Select(x=>x.GetIdValue()).Distinct().ToList(),
                FailureStage=failures.Count>0?"setup_or_view_control":attempts.FirstOrDefault(x=>!x.Passed)?.FailureStage??production.FirstOrDefault(x=>!x.Passed)?.FailureStage??(cleanup?null:"cleanup"),
                RollbackCleanupPassed=cleanup,CleanupFailureStage=cleanup?null:"cleanup",ForcedRollback=true,Warnings=warnings,Failures=failures
            };
        }
        private Wall ResolveSingleSelectedCurtainWall(UIDocument uidoc,Document doc)
        {
            List<Wall> walls=uidoc.Selection.GetElementIds().Select(id=>doc.GetElement(id) as Wall).Where(x=>x!=null).Where(x=>{try{return x.CurtainGrid!=null;}catch{return false;}}).ToList();
            return walls.Count==1?walls[0]:null;
        }
        private View ResolveCurtainElevationAlternateDiagnosticView(UIDocument uidoc,Document doc,View target)
        {
            View open=uidoc.GetOpenUIViews().Where(x=>x.ViewId!=target.Id).Select(x=>doc.GetElement(x.ViewId) as View).FirstOrDefault(x=>x!=null&&!x.IsTemplate);
            return open??new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(x=>!x.IsTemplate&&x.Id!=target.Id&&x.CanBePrinted);
        }
        private void RunCurtainLevelRuntimeMatrix(Document doc,ViewSection view,Wall wall,Transform frame,DimensionType type,double minX,double maxX,double minY,double maxY,double levelY,double lineX,double stackOffsetFt,List<CurtainElevationGeometryReference> refs,string state,List<CurtainLevelRuntimeAttempt> attempts,List<CurtainLevelProductionAttempt> production,List<ElementId> ids)
        {
            foreach(string strategy in new[]{"level_plane","invisible_detail_curve"})
            {
                attempts.Add(RunCurtainLevelRuntimeAttempt(doc,view,wall,frame,type,minX,maxX,minY,maxY,levelY,lineX,refs,state,strategy,false,ids));
                attempts.Add(RunCurtainLevelRuntimeAttempt(doc,view,wall,frame,type,minX,maxX,minY,maxY,levelY,lineX,refs,state,strategy,true,ids));
            }
            production.Add(RunCurtainLevelProductionAttempt(doc,view,wall,frame,type,minX,maxX,minY,maxY,levelY,lineX,stackOffsetFt,refs,state,ids));
        }
    }
}
