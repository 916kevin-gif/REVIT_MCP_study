using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif
namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        private CurtainLevelRuntimeAttempt RunCurtainLevelRuntimeAttempt(Document doc,ViewSection view,Wall wall,Transform frame,DimensionType type,double minX,double maxX,double minY,double maxY,double levelY,double lineX,List<CurtainElevationGeometryReference> heightRefs,string viewState,string strategy,bool includeTop,List<ElementId> tempIds)
        {
            var attempt=new CurtainLevelRuntimeAttempt
            {
                Name=$"{strategy}_{(includeTop?"three_reference":"two_reference")}_{viewState}",ViewState=viewState,ReferenceStrategy=strategy,
                ExpectedReferenceCount=includeTop?3:2,LevelYmm=Math.Round(levelY*304.8,4),CurtainBottomYmm=Math.Round(minY*304.8,4),
                CurtainTopYmm=Math.Round(maxY*304.8,4),DimensionLineXmm=Math.Round(lineX*304.8,4),FailureStage="reference_acquisition"
            };
            Transaction tx=null;
            var aggregate=new CurtainElevationDimensionResult{WallId=wall.Id};
            try
            {
                tx=TransactionHelper.Begin(doc,"Runtime test curtain Level offset "+attempt.Name);tx.Start();
                CurtainElevationGeometryReference levelRef; string reason;
                if(strategy=="level_plane")
                {
                    if(!TryCreateCurtainElevationLevelPlaneReference(doc,doc.GetElement(wall.LevelId) as Level,minX,maxX,levelY,out levelRef,out reason)) throw new InvalidOperationException(reason);
                }
                else
                {
                    if(!TryCreateCurtainElevationInvisibleLevelReference(doc,view,frame,minX,maxX,levelY,aggregate,out levelRef,out reason)) throw new InvalidOperationException(reason);
                    attempt.HelperCurveId=levelRef.ElementId.GetIdValue();tempIds.Add(levelRef.ElementId);
                    DetailCurve helper=doc.GetElement(levelRef.ElementId) as DetailCurve;
                    attempt.HelperOwnerViewId=helper?.OwnerViewId.GetIdValue();attempt.HelperLineStyleId=helper?.LineStyle?.Id.GetIdValue();
                    GraphicsStyle invisible=TryFindCurtainElevationLevelInvisibleLineStyle(doc);
                    attempt.HelperUsesInvisibleLines=helper?.LineStyle!=null&&invisible!=null&&helper.LineStyle.Id.GetIdValue()==invisible.Id.GetIdValue();
                }
                attempt.ReferenceAcquired=true;
                var refs=new List<CurtainElevationGeometryReference>{levelRef,heightRefs[0]};if(includeTop)refs.Add(heightRefs[1]);refs=refs.OrderBy(x=>x.CenterY).ToList();
                var coordinates=refs.Select(x=>x.CenterY).ToList();
                attempt.InputReferences=refs.Select(x=>BuildCurtainLevelReferenceInfo(doc,x)).ToList();
                attempt.StableRoundTripPassed=attempt.InputReferences.All(x=>x.StableRoundTripPassed);
                if(!attempt.StableRoundTripPassed){attempt.FailureStage="stable_round_trip";throw new InvalidOperationException("Input reference stable round-trip failed.");}
                attempt.ExpectedSegmentValuesMm=coordinates.Zip(coordinates.Skip(1),(a,b)=>Math.Round(Math.Abs(b-a)*304.8,4)).ToList();
                attempt.FailureStage="creation";
                var array=new ReferenceArray();foreach(var item in refs)array.Append(item.Reference);
                Dimension dimension=doc.Create.NewDimension(view,Line.CreateBound(CurtainElevationPointAt2D(frame,lineX,coordinates.First()),CurtainElevationPointAt2D(frame,lineX,coordinates.Last())),array);
                if(dimension==null)throw new InvalidOperationException("Revit returned null Dimension.");
                ApplyDimensionType(dimension,type);doc.Regenerate();
                attempt.DimensionCreated=true;attempt.DimensionId=dimension.Id.GetIdValue();attempt.OwnerViewId=dimension.OwnerViewId.GetIdValue();tempIds.Add(dimension.Id);
                attempt.FailureStage="pre_commit";attempt.PreCommitAreReferencesAvailable=dimension.AreReferencesAvailable;attempt.PreCommitReferenceCount=dimension.References?.Size??0;
                attempt.FailureStage="commit";tx.Commit();attempt.TransactionCommitted=true;tx=null;
                attempt.FailureStage="post_commit";
                Dimension persisted=doc.GetElement(new ElementId(attempt.DimensionId.Value)) as Dimension;
                if(persisted==null)throw new InvalidOperationException("Dimension did not exist after commit.");
                attempt.PostCommitAreReferencesAvailable=persisted.AreReferencesAvailable;attempt.PostCommitReferenceCount=persisted.References?.Size??0;
                attempt.NumberOfSegments=persisted.NumberOfSegments;attempt.ActualSegmentValuesMm=GetCurtainElevationDimensionValuesMm(persisted);
                attempt.FailureStage="segment_validation";attempt.SegmentValuesPassed=CurtainLevelValuesMatch(attempt.ExpectedSegmentValuesMm,attempt.ActualSegmentValuesMm,0.5);
                bool levelPlaneFalseNegative=strategy=="level_plane"&&attempt.PostCommitAreReferencesAvailable!=true&&attempt.PostCommitReferenceCount==attempt.ExpectedReferenceCount&&attempt.SegmentValuesPassed==true;
                attempt.PostCommitValidationMode=attempt.PostCommitAreReferencesAvailable==true
                    ?"strict_references_available"
                    :(levelPlaneFalseNegative?"level_plane_segment_validation":"failed");
                bool referenceValidationPassed=attempt.PostCommitAreReferencesAvailable==true||levelPlaneFalseNegative;
                attempt.Passed=attempt.OwnerViewId==view.Id.GetIdValue()&&referenceValidationPassed&&attempt.PostCommitReferenceCount==attempt.ExpectedReferenceCount&&attempt.SegmentValuesPassed==true&&(strategy!="invisible_detail_curve"||attempt.HelperUsesInvisibleLines==true);
                attempt.FailureStage=attempt.Passed?null:"post_commit_assertion";
            }
            catch(Exception ex)
            {
                if(tx!=null&&tx.GetStatus()==TransactionStatus.Started)tx.RollBack();
                attempt.ExceptionType=ex.GetType().FullName;attempt.ExceptionMessage=ex.Message;attempt.ExceptionStackTrace=ex.StackTrace;attempt.Passed=false;
            }
            return attempt;
        }
        private CurtainLevelProductionAttempt RunCurtainLevelProductionAttempt(Document doc,ViewSection view,Wall wall,Transform frame,DimensionType type,double minX,double maxX,double minY,double maxY,double levelY,double lineX,double stackOffsetFt,List<CurtainElevationGeometryReference> heightRefs,string viewState,List<ElementId> tempIds)
        {
            var attempt=new CurtainLevelProductionAttempt{ViewState=viewState,FailureStage="production_creation"};
            var result=new CurtainElevationDimensionResult{WallId=wall.Id};
            try
            {
                using(Transaction tx=TransactionHelper.Begin(doc,"Runtime test production Level offset "+viewState))
                {
                    tx.Start();CreateCurtainElevationTotalHeightAndLevelOffsetDimensions(doc,view,wall,frame,type,minX,maxX,minY,maxY,levelY,lineX,stackOffsetFt,heightRefs,result);tx.Commit();
                }
                attempt.FailureStage="production_post_commit_repair";FinalizeCurtainElevationDimensionsAfterCommit(doc,new[]{result});
                foreach(ElementId id in new[]{result.TotalHeightDimensionId,result.LevelOffsetDimensionElementId}.Concat(result.ReferenceCurveIds).Where(x=>x!=null&&x!=ElementId.InvalidElementId).Distinct()){tempIds.Add(id);}
                attempt.Status=result.Status;attempt.LevelOffsetDimensionMode=result.LevelOffsetDimensionMode;attempt.LevelOffsetDimensionStatus=result.LevelOffsetDimensionStatus;
                attempt.LevelOffsetDimensionReferenceSource=result.LevelOffsetDimensionReferenceSource;attempt.TotalHeightDimensionId=result.TotalHeightDimensionId?.GetIdValue();
                attempt.LevelOffsetDimensionElementId=result.LevelOffsetDimensionElementId?.GetIdValue();attempt.TotalHeightReferencesAvailable=result.TotalHeightDimensionAreReferencesAvailable;
                attempt.LevelOffsetReferencesAvailable=result.LevelOffsetDimensionAreReferencesAvailable;attempt.Warnings=result.Warnings.ToList();
                CurtainElevationPendingDimension levelPending=result.PendingNativeDimensions.FirstOrDefault(IsCurtainElevationLevelOffsetPlanePending);
                attempt.PostCommitValidationMode=levelPending?.PostCommitValidationMode;
                attempt.SegmentValuesPassed=levelPending?.SegmentValuesPassed;
                attempt.PostCommitDimensionValidation=result.PostCommitDimensionValidation.ToList();
                Dimension total=result.TotalHeightDimensionId==null?null:doc.GetElement(result.TotalHeightDimensionId) as Dimension;
                Dimension offset=result.LevelOffsetDimensionElementId==null?null:doc.GetElement(result.LevelOffsetDimensionElementId) as Dimension;
                attempt.ActualTotalHeightSegmentValuesMm=GetCurtainElevationDimensionValuesMm(total);attempt.ActualLevelOffsetSegmentValuesMm=GetCurtainElevationDimensionValuesMm(offset);
                double signed=minY-levelY;
                if(Math.Abs(signed)<=1.0/304.8)
                {
                    attempt.ExpectedSegmentValuesMm=new List<double>{Math.Round((maxY-minY)*304.8,4)};
                    attempt.Passed=result.LevelOffsetDimensionStatus=="skipped_zero_distance"&&total!=null&&result.TotalHeightDimensionAreReferencesAvailable==true;
                }
                else if(signed>0)
                {
                    attempt.ExpectedSegmentValuesMm=new List<double>{Math.Round((minY-levelY)*304.8,4),Math.Round((maxY-minY)*304.8,4)};
                    attempt.Passed=total!=null&&result.LevelOffsetDimensionElementId!=null&&result.TotalHeightDimensionId.GetIdValue()==result.LevelOffsetDimensionElementId.GetIdValue()&&result.LevelOffsetDimensionReferenceSource=="wall_level_plane_reference"&&levelPending?.PostCommitValidationPassed==true&&CurtainLevelValuesMatch(attempt.ExpectedSegmentValuesMm,attempt.ActualTotalHeightSegmentValuesMm,0.5);
                }
                else
                {
                    attempt.ExpectedSegmentValuesMm=new List<double>{Math.Round(Math.Abs(minY-levelY)*304.8,4),Math.Round((maxY-minY)*304.8,4)};
                    attempt.Passed=total!=null&&offset!=null&&result.TotalHeightDimensionAreReferencesAvailable==true&&result.LevelOffsetDimensionReferenceSource=="wall_level_plane_reference"&&levelPending?.PostCommitValidationPassed==true&&CurtainLevelValuesMatch(new[]{attempt.ExpectedSegmentValuesMm[1]},attempt.ActualTotalHeightSegmentValuesMm,0.5)&&CurtainLevelValuesMatch(new[]{attempt.ExpectedSegmentValuesMm[0]},attempt.ActualLevelOffsetSegmentValuesMm,0.5);
                }
                attempt.FailureStage=attempt.Passed?null:"production_assertion";
            }
            catch(Exception ex){attempt.ExceptionType=ex.GetType().FullName;attempt.ExceptionMessage=ex.Message;attempt.ExceptionStackTrace=ex.StackTrace;attempt.Passed=false;}
            return attempt;
        }
        private CurtainLevelReferenceInfo BuildCurtainLevelReferenceInfo(Document doc,CurtainElevationGeometryReference reference)
        {
            var info=new CurtainLevelReferenceInfo{ElementId=reference.ElementId?.GetIdValue(),ReferenceSource=reference.ReferenceSource};
            try
            {
                info.ElementReferenceType=reference.Reference.ElementReferenceType.ToString();info.StableRepresentation=reference.Reference.ConvertToStableRepresentation(doc);
                Reference parsed=Reference.ParseFromStableRepresentation(doc,info.StableRepresentation);
                info.StableRoundTripPassed=parsed!=null&&parsed.ElementId!=null&&reference.ElementId!=null&&parsed.ElementId.GetIdValue()==reference.ElementId.GetIdValue();
            }
            catch(Exception ex){info.Failure=ex.Message;}
            return info;
        }
        private bool CurtainLevelValuesMatch(IList<double> expected,IList<double> actual,double toleranceMm)
        {
            return expected!=null&&actual!=null&&expected.Count==actual.Count&&expected.Zip(actual,(a,b)=>Math.Abs(a-b)<=toleranceMm).All(x=>x);
        }
    }
}
