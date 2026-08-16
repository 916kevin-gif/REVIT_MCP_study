using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        private object DiagnoseDoorWindowLegendGrouping(Document doc, string targetType, int? legendViewId, string action, string token)
        {
            if (string.Equals(action, "finalize", StringComparison.OrdinalIgnoreCase))
            {
                return FinalizeDoorWindowLegendGroupingDiagnostic(doc, token);
            }
            if (!string.Equals(action, "run", StringComparison.OrdinalIgnoreCase))
            {
                return new { WorkflowState = "diagnostic_invalid_action", ErrorCode = "invalid_diagnostic_action", Message = "diagnosticAction must be run or finalize." };
            }
            if (!legendViewId.HasValue)
            {
                return new { WorkflowState = "awaiting_legend_view_selection", RequiresUserInput = true, MissingFields = new[] { "legendViewId" }, Message = "diagnose_grouping requires a source Legend view." };
            }
            View sourceView = GetLegendViewById(doc, legendViewId.Value);
            if (sourceView == null)
            {
                return new { WorkflowState = "diagnostic_source_not_found", ErrorCode = "legend_view_not_found", LegendViewId = legendViewId.Value };
            }

            List<DoorWindowLegendGroupingDiagnosticCase> cases = new List<DoorWindowLegendGroupingDiagnosticCase>
            {
                new DoorWindowLegendGroupingDiagnosticCase { Name = "full_metadata_assimilate", CompletionMode = "assimilate", MetadataMode = "full" },
                new DoorWindowLegendGroupingDiagnosticCase { Name = "full_no_metadata_assimilate", CompletionMode = "assimilate", MetadataMode = "none" },
                new DoorWindowLegendGroupingDiagnosticCase { Name = "member_metadata_only_assimilate", CompletionMode = "assimilate", MetadataMode = "members" },
                new DoorWindowLegendGroupingDiagnosticCase { Name = "no_dimensions_assimilate", CompletionMode = "assimilate", MetadataMode = "full", DimensionRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) },
                new DoorWindowLegendGroupingDiagnosticCase { Name = "width_only_assimilate", CompletionMode = "assimilate", MetadataMode = "full", DimensionRoles = new HashSet<string>(new[] { "width_dimension" }, StringComparer.OrdinalIgnoreCase) },
                new DoorWindowLegendGroupingDiagnosticCase { Name = "height_only_assimilate", CompletionMode = "assimilate", MetadataMode = "full", DimensionRoles = new HashSet<string>(new[] { "height_dimension" }, StringComparer.OrdinalIgnoreCase) },
                new DoorWindowLegendGroupingDiagnosticCase { Name = "internal_reference_dimensions_assimilate", CompletionMode = "assimilate", MetadataMode = "full", OnlyDimensionsWithInternalReferences = true },
                new DoorWindowLegendGroupingDiagnosticCase { Name = "full_metadata_commit", CompletionMode = "commit", MetadataMode = "full" }
            };
            if (string.Equals(targetType, "window", StringComparison.OrdinalIgnoreCase))
            {
                cases.Insert(6, new DoorWindowLegendGroupingDiagnosticCase { Name = "sill_only_assimilate", CompletionMode = "assimilate", MetadataMode = "full", DimensionRoles = new HashSet<string>(new[] { "sill_dimension" }, StringComparer.OrdinalIgnoreCase) });
            }

            DoorWindowLegendGroupingDiagnosticPending pending = new DoorWindowLegendGroupingDiagnosticPending { Token = Guid.NewGuid().ToString("N") };
            foreach (DoorWindowLegendGroupingDiagnosticCase diagnosticCase in cases)
            {
                pending.Cases.Add(RunDoorWindowLegendGroupingDiagnosticCase(doc, sourceView, targetType, diagnosticCase));
            }
            DoorWindowLegendGroupingDiagnostics[pending.Token] = pending;
            return new
            {
                WorkflowState = "grouping_diagnostic_pending_finalize",
                DiagnosticToken = pending.Token,
                RequiresFinalize = true,
                NextAction = "call diagnose_grouping with diagnosticAction=finalize and diagnosticToken",
                SourceLegendViewId = legendViewId.Value,
                Cases = pending.Cases,
                Summary = SummarizeDoorWindowLegendGroupingDiagnostic(pending.Cases)
            };
        }

        private DoorWindowLegendGroupingDiagnosticCaseResult RunDoorWindowLegendGroupingDiagnosticCase(Document doc, View sourceView, string targetType, DoorWindowLegendGroupingDiagnosticCase diagnosticCase)
        {
            DoorWindowLegendGroupingDiagnosticCaseResult result = new DoorWindowLegendGroupingDiagnosticCaseResult { CaseName = diagnosticCase.Name, CompletionMode = diagnosticCase.CompletionMode };
            TransactionGroup transactionGroup = new TransactionGroup(doc, "RMCP DWL grouping diagnostic " + diagnosticCase.Name);
            Transaction transaction = null;
            SubTransaction subTransaction = null;
            ElementId groupId = ElementId.InvalidElementId;
            string transactionStatus = "NotStarted";
            try
            {
                transactionGroup.Start();
                transaction = new Transaction(doc, "RMCP DWL diagnostic item");
                transaction.Start();
                FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(new DoorWindowLegendGroupingFailureCollector(result.Failures));
                transaction.SetFailureHandlingOptions(options);

                View diagnosticView = DuplicateLegendView(doc, sourceView, "RMCP_DWL_DIAG_" + diagnosticCase.Name);
                result.ViewId = diagnosticView.Id.GetIdValue();
                Schema schema = GetDoorWindowLegendItemMetadataSchema(createIfMissing: false);
                if (schema != null)
                {
                    foreach (ElementId id in CollectViewElementIds(doc, diagnosticView))
                    {
                        Element element = doc.GetElement(id);
                        if (element != null && element.GetEntity(schema).IsValid()) element.DeleteEntity(schema);
                    }
                }
                doc.Regenerate();

                DoorWindowLegendExistingItem item = CollectExistingDoorWindowLegendItems(doc, diagnosticView, targetType, "horizontal", 100)
                    .Where(candidate => IsValidElementId(candidate.ComponentId))
                    .OrderBy(candidate => candidate.GridIndex)
                    .FirstOrDefault();
                if (item == null) throw new InvalidOperationException("No diagnostic item was found in the duplicated Legend view.");
                List<ElementId> relatedIds = CollectDoorWindowLegendItemRelatedElementIds(doc, diagnosticView, item, targetType)
                    .Where(IsValidElementId).Where(id => doc.GetElement(id) != null).Distinct(new ElementIdValueComparer()).ToList();
                Dictionary<ElementId, string> roles = BuildDoorWindowLegendDiagnosticRoleMap(doc, item, targetType, relatedIds);
                HashSet<IdType> allRelatedValues = relatedIds.Select(id => id.GetIdValue()).ToHashSet();
                Dictionary<ElementId, string> selectedRoles = roles
                    .Where(pair => !(doc.GetElement(pair.Key) is Dimension) || DoorWindowLegendDiagnosticDimensionIncluded(doc, pair.Key, pair.Value, diagnosticCase, allRelatedValues))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, new ElementIdValueComparer());
                if (!selectedRoles.ContainsKey(item.ComponentId)) selectedRoles[item.ComponentId] = "component";
                result.MemberRoles = selectedRoles.ToDictionary(pair => pair.Key.GetIdValue(), pair => pair.Value);
                result.MemberIds = selectedRoles.Keys.Select(id => id.GetIdValue()).ToList();
                result.IncludedRoles = selectedRoles.Values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();

                string itemGuid = Guid.NewGuid().ToString("D");
                string ownerUniqueId = doc.GetElement(item.ComponentId).UniqueId;
                List<KeyValuePair<ElementId, string>> children = selectedRoles.Where(pair => pair.Key.GetIdValue() != item.ComponentId.GetIdValue()).ToList();
                List<string> childUniqueIds = children.Select(pair => doc.GetElement(pair.Key).UniqueId).ToList();
                List<string> childRoles = children.Select(pair => pair.Value).ToList();

                subTransaction = new SubTransaction(doc);
                subTransaction.Start();
                if (!string.Equals(diagnosticCase.MetadataMode, "none", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (KeyValuePair<ElementId, string> pair in selectedRoles)
                    {
                        bool component = pair.Key.GetIdValue() == item.ComponentId.GetIdValue();
                        SetDoorWindowLegendItemMetadata(doc.GetElement(pair.Key), component ? "component" : "member", itemGuid, item.Key, targetType, ownerUniqueId, string.Empty, pair.Value, component ? childUniqueIds : new List<string>(), component ? childRoles : new List<string>());
                    }
                }

                Autodesk.Revit.DB.Group group = doc.Create.NewGroup(selectedRoles.Keys.ToList());
                groupId = group.Id;
                result.GroupId = group.Id.GetIdValue();
                result.GroupTypeId = group.GroupType.Id.GetIdValue();
                result.Snapshots.Add(CaptureDoorWindowLegendGroupingDiagnosticSnapshot(doc, diagnosticView.Id, groupId, selectedRoles, "after_new_group", subTransaction.GetStatus().ToString()));
                if (string.Equals(diagnosticCase.MetadataMode, "full", StringComparison.OrdinalIgnoreCase))
                {
                    string groupUniqueId = group.UniqueId;
                    group.GroupType.Name = "RMCP_DWL_DIAG_" + itemGuid.Replace("-", string.Empty).Substring(0, 8);
                    SetDoorWindowLegendItemMetadata(group, "container", itemGuid, item.Key, targetType, ownerUniqueId, groupUniqueId, "container", childUniqueIds, childRoles);
                    SetDoorWindowLegendItemMetadata(group.GroupType, "container_type", itemGuid, item.Key, targetType, ownerUniqueId, groupUniqueId, "container_type", new List<string>(), new List<string>());
                }
                result.Snapshots.Add(CaptureDoorWindowLegendGroupingDiagnosticSnapshot(doc, diagnosticView.Id, groupId, selectedRoles, "after_metadata", subTransaction.GetStatus().ToString()));
                doc.Regenerate();
                result.Snapshots.Add(CaptureDoorWindowLegendGroupingDiagnosticSnapshot(doc, diagnosticView.Id, groupId, selectedRoles, "after_regenerate", subTransaction.GetStatus().ToString()));
                TransactionStatus subStatus = subTransaction.Commit();
                result.Snapshots.Add(CaptureDoorWindowLegendGroupingDiagnosticSnapshot(doc, diagnosticView.Id, groupId, selectedRoles, "after_subtransaction_commit", subStatus.ToString()));
                subTransaction.Dispose();
                subTransaction = null;
                TransactionStatus commitStatus = transaction.Commit();
                transactionStatus = commitStatus.ToString();
                result.Snapshots.Add(CaptureDoorWindowLegendGroupingDiagnosticSnapshot(doc, diagnosticView.Id, groupId, selectedRoles, "after_transaction_commit", transactionStatus));
                transaction.Dispose();
                transaction = null;
                TransactionStatus groupStatus = string.Equals(diagnosticCase.CompletionMode, "commit", StringComparison.OrdinalIgnoreCase) ? transactionGroup.Commit() : transactionGroup.Assimilate();
                result.Snapshots.Add(CaptureDoorWindowLegendGroupingDiagnosticSnapshot(doc, diagnosticView.Id, groupId, selectedRoles, "after_transaction_group_" + diagnosticCase.CompletionMode, groupStatus.ToString()));
            }
            catch (Exception ex)
            {
                result.Exception = ex.GetType().FullName + ": " + ex.Message;
                try
                {
                    if (subTransaction != null && subTransaction.GetStatus() == TransactionStatus.Started) subTransaction.RollBack();
                    if (transaction != null && transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
                    if (transactionGroup.GetStatus() == TransactionStatus.Started) transactionGroup.RollBack();
                }
                catch (Exception rollbackException)
                {
                    result.Exception += " | rollback: " + rollbackException.Message;
                }
            }
            finally
            {
                if (subTransaction != null) subTransaction.Dispose();
                if (transaction != null) transaction.Dispose();
                ((IDisposable)transactionGroup)?.Dispose();
            }
            return result;
        }

        private Dictionary<ElementId, string> BuildDoorWindowLegendDiagnosticRoleMap(Document doc, DoorWindowLegendExistingItem item, string targetType, List<ElementId> ids)
        {
            Dictionary<ElementId, string> roles = new Dictionary<ElementId, string>(new ElementIdValueComparer());
            Dictionary<ElementId, string> dimensionRoles = BuildLegacyDoorWindowDimensionRoleMap(doc, ids, targetType);
            foreach (ElementId id in ids)
            {
                Element element = doc.GetElement(id);
                if (element == null) continue;
                if (id.GetIdValue() == item.ComponentId.GetIdValue()) roles[id] = "component";
                else if (IsValidElementId(item.FflLineId) && id.GetIdValue() == item.FflLineId.GetIdValue()) roles[id] = "ffl_line";
                else if (element is TextNote note) roles[id] = (note.Text ?? string.Empty).IndexOf("FFL", StringComparison.OrdinalIgnoreCase) >= 0 ? "ffl_label" : "type_mark";
                else if (element is Dimension && dimensionRoles.TryGetValue(id, out string dimensionRole)) roles[id] = dimensionRole;
                else if (element is DetailCurve) roles[id] = "dimension_reference_curve";
                else roles[id] = "member";
            }
            return roles;
        }

        private bool DoorWindowLegendDiagnosticDimensionIncluded(Document doc, ElementId id, string role, DoorWindowLegendGroupingDiagnosticCase diagnosticCase, HashSet<IdType> relatedIds)
        {
            if (diagnosticCase.DimensionRoles != null && !diagnosticCase.DimensionRoles.Contains(role)) return false;
            if (!diagnosticCase.OnlyDimensionsWithInternalReferences) return true;
            List<IdType> referenceIds = GetDoorWindowLegendDiagnosticDimensionReferenceIds(doc.GetElement(id) as Dimension);
            return referenceIds.Count > 0 && referenceIds.All(relatedIds.Contains);
        }

        private List<IdType> GetDoorWindowLegendDiagnosticDimensionReferenceIds(Dimension dimension)
        {
            List<IdType> ids = new List<IdType>();
            if (dimension == null || dimension.References == null) return ids;
            foreach (Reference reference in dimension.References)
            {
                if (reference != null && IsValidElementId(reference.ElementId)) ids.Add(reference.ElementId.GetIdValue());
            }
            return ids;
        }

        private DoorWindowLegendGroupingDiagnosticSnapshot CaptureDoorWindowLegendGroupingDiagnosticSnapshot(Document doc, ElementId viewId, ElementId groupId, IDictionary<ElementId, string> roles, string phase, string transactionStatus)
        {
            Autodesk.Revit.DB.Group group = IsValidElementId(groupId) ? doc.GetElement(groupId) as Autodesk.Revit.DB.Group : null;
            DoorWindowLegendGroupingDiagnosticSnapshot snapshot = new DoorWindowLegendGroupingDiagnosticSnapshot
            {
                Phase = phase, TransactionStatus = transactionStatus,
                ViewId = IsValidElementId(viewId) ? viewId.GetIdValue() : 0,
                GroupId = IsValidElementId(groupId) ? groupId.GetIdValue() : 0,
                GroupTypeId = group == null || group.GroupType == null ? 0 : group.GroupType.Id.GetIdValue(),
                GroupName = group == null || group.GroupType == null ? null : group.GroupType.Name,
                GroupCategory = group == null || group.Category == null ? null : group.Category.Name,
                GroupExists = group != null,
                GroupMetadataExists = ReadDoorWindowLegendItemMetadata(group) != null,
                GroupTypeMetadataExists = group != null && ReadDoorWindowLegendItemMetadata(group.GroupType) != null,
                GroupMemberIds = group == null ? new List<IdType>() : group.GetMemberIds().Where(IsValidElementId).Select(id => id.GetIdValue()).ToList()
            };
            foreach (KeyValuePair<ElementId, string> pair in roles ?? new Dictionary<ElementId, string>())
            {
                Element element = doc.GetElement(pair.Key);
                snapshot.Members.Add(new { ElementId = pair.Key.GetIdValue(), Role = pair.Value, Exists = element != null, UniqueId = element?.UniqueId, Category = element?.Category?.Name, RuntimeType = element?.GetType().FullName, GroupId = element == null || !IsValidElementId(element.GroupId) ? 0 : element.GroupId.GetIdValue(), MetadataExists = ReadDoorWindowLegendItemMetadata(element) != null });
                if (element is Dimension dimension) snapshot.Dimensions.Add(new { ElementId = pair.Key.GetIdValue(), Role = pair.Value, ReferenceElementIds = GetDoorWindowLegendDiagnosticDimensionReferenceIds(dimension) });
            }
            Logger.Info($"door-window-legend grouping diagnostic. phase={phase}, viewId={snapshot.ViewId}, groupId={snapshot.GroupId}, groupExists={snapshot.GroupExists}, groupMemberCount={snapshot.GroupMemberIds.Count}, transactionStatus={transactionStatus}");
            return snapshot;
        }

        private object FinalizeDoorWindowLegendGroupingDiagnostic(Document doc, string token)
        {
            if (string.IsNullOrWhiteSpace(token) || !DoorWindowLegendGroupingDiagnostics.TryGetValue(token, out DoorWindowLegendGroupingDiagnosticPending pending))
            {
                return new { WorkflowState = "diagnostic_token_not_found", ErrorCode = "diagnostic_token_not_found", DiagnosticToken = token };
            }
            foreach (DoorWindowLegendGroupingDiagnosticCaseResult result in pending.Cases.Where(item => item.ViewId != 0))
            {
                Dictionary<ElementId, string> roles = result.MemberRoles.ToDictionary(pair => DoorWindowLegendDiagnosticElementId(pair.Key), pair => pair.Value, new ElementIdValueComparer());
                result.Snapshots.Add(CaptureDoorWindowLegendGroupingDiagnosticSnapshot(doc, DoorWindowLegendDiagnosticElementId(result.ViewId), DoorWindowLegendDiagnosticElementId(result.GroupId), roles, "next_external_event", "ExternalEvent"));
            }
            List<IdType> deletedViewIds = new List<IdType>();
            List<string> cleanupFailures = new List<string>();
            Transaction cleanup = new Transaction(doc, "RMCP DWL diagnostic cleanup");
            try
            {
                cleanup.Start();
                foreach (DoorWindowLegendGroupingDiagnosticCaseResult result in pending.Cases)
                {
                    ElementId viewId = DoorWindowLegendDiagnosticElementId(result.ViewId);
                    if (IsValidElementId(viewId) && doc.GetElement(viewId) != null) { doc.Delete(viewId); deletedViewIds.Add(result.ViewId); }
                }
                List<ElementId> diagnosticGroupTypeIds = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>()
                    .Where(groupType => (groupType.Name ?? string.Empty).StartsWith("RMCP_DWL_DIAG_", StringComparison.OrdinalIgnoreCase)).Select(groupType => groupType.Id).ToList();
                if (diagnosticGroupTypeIds.Count > 0) doc.Delete(diagnosticGroupTypeIds);
                cleanup.Commit();
            }
            catch (Exception ex)
            {
                cleanupFailures.Add(ex.GetType().FullName + ": " + ex.Message);
                if (cleanup.GetStatus() == TransactionStatus.Started) cleanup.RollBack();
            }
            finally { cleanup.Dispose(); }
            DoorWindowLegendGroupingDiagnostics.Remove(token);
            return new { WorkflowState = cleanupFailures.Count == 0 ? "grouping_diagnostic_complete" : "grouping_diagnostic_cleanup_failed", DiagnosticToken = token, Cases = pending.Cases, Summary = SummarizeDoorWindowLegendGroupingDiagnostic(pending.Cases), DeletedDiagnosticViewIds = deletedViewIds, CleanupFailures = cleanupFailures };
        }

        private object SummarizeDoorWindowLegendGroupingDiagnostic(List<DoorWindowLegendGroupingDiagnosticCaseResult> cases)
        {
            return cases.Select(result =>
            {
                int firstExistingIndex = result.Snapshots.FindIndex(snapshot => snapshot.GroupExists);
                DoorWindowLegendGroupingDiagnosticSnapshot firstExisting = firstExistingIndex < 0 ? null : result.Snapshots[firstExistingIndex];
                DoorWindowLegendGroupingDiagnosticSnapshot firstMissing = firstExistingIndex < 0 ? null : result.Snapshots.Skip(firstExistingIndex + 1).FirstOrDefault(snapshot => !snapshot.GroupExists);
                return new { result.CaseName, result.CompletionMode, FirstExistingPhase = firstExisting?.Phase, FirstMissingPhase = firstMissing?.Phase, FinalGroupExists = result.Snapshots.LastOrDefault()?.GroupExists ?? false, FailureIds = result.Failures.Select(failure => failure.FailureDefinitionId).Distinct().ToList(), result.Exception };
            }).ToList();
        }

        private static ElementId DoorWindowLegendDiagnosticElementId(IdType value)
        {
            return value == 0 ? ElementId.InvalidElementId : new ElementId(value);
        }
    }
}
