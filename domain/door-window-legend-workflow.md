---
name: door-window-legend-workflow
description: 門窗圖例表 seed-based Legend Component 建立流程，主入口為 door-window-legend-tools，缺少 seed 時透過 list_seeds 取得候選並等待使用者選擇。
metadata:
  version: "1.3"
  updated: "2026-05-21"
  created: "2026-05-20"
  references: []
  related:
    - element-query-workflow.md
    - tool-capability-boundary.md
  referenced_by:
    - door-window-legend-tools
    - list_seeds
  contributors:
    - "OpenAI Codex"
  tags: [door, window, legend, type-mark, legend-component, seed, workflow]
---

# 門窗圖例表 Workflow

## 目的

此 SOP 定義門表與窗表的建立流程：

- `mode=list`：列出專案中已放置實例使用到的 door/window type。
- `mode=create`：使用指定的 seed Legend 視圖建立門表或窗表。
- 若 create 缺少 seed，tool 必須回 workflow state，對話層再呼叫 `list_seeds` 並等待使用者選擇。
- 不得自動選 seed。
- 不得自動輪流測試 seed。

## Tool Contract

### `door-window-legend-tools`

輸入：

- `targetType`: `door` 或 `window`
- `mode`: `list` 或 `create`
- `layoutDirection`: `horizontal` 或 `vertical`，create 必填
- `maxPerLine`: 大於等於 1，create 必填
- `seedLegendViewId`: create 使用的 seed Legend 視圖 ID

create 若缺少 `seedLegendViewId`，回傳：

- `WorkflowState = "awaiting_seed_selection"`
- `NextAction = "call_list_seeds"`
- `SeedTypeRequired = "legend"`
- `RequiresUserInput = true`
- `DoNotAutoSelectSeed = true`
- `DoNotRetryWithOtherSeeds = true`
- `PromptToUser = "請先從 list_seeds 的結果中選擇一個 ViewName 作為 seed。"`

此狀態不是錯誤，也不是 fallback 訊號。對話層只能呼叫 `list_seeds` 後停下來問使用者。

### `list_seeds`

輸入：

- `seedType = "legend"`

回傳全部非樣板 Legend 視圖：

- `viewId`
- `viewName`
- `legendComponentCount`
- `isUsableSeed`

回傳 workflow metadata：

- `WorkflowState = "awaiting_user_choice"`
- `SelectionMode = "user_must_choose"`
- `SelectionField = "ViewName"`
- `RequiresUserInput = true`
- `DoNotAutoSelect = true`
- `DoNotAutoRetryCreate = true`
- `PromptToUser = "請從以下 Legend 視圖中選一個 ViewName 作為 seed。"`

## Used Types

- `door` 使用 `BuiltInCategory.OST_Doors`
- `window` 使用 `BuiltInCategory.OST_Windows`
- 只收已放置 instance 的 type
- 以 `GetTypeId()` 去重，再回查 `FamilySymbol`
- `Type Mark` 優先讀 `ALL_MODEL_TYPE_MARK`
- 空白 Type Mark 顯示 `(未填)`，排序排最後

## 排序

1. 主鍵為 `Type Mark`
2. 非空白 Type Mark 使用自然排序，例如 `D1 < D2 < D10`
3. 空白 Type Mark 排最後
4. 次鍵為 `Type Name`

## 建立流程

指定 `seedLegendViewId` 後：

1. 驗證 view 存在、是 `Legend`、不是 template。
2. 用 `ViewDuplicateOption.WithDetailing` 複製 seed Legend。
3. 在 duplicated view 內收集 `seedOriginalElementIds`。
4. 在 duplicated view 內找第一個 `OST_LegendComponents` 作為 source seed component。
5. 對每個 used type，從 source seed component 做 same-view copy。
6. 對 copy 出來的 component 設定 `BuiltInParameter.LEGEND_COMPONENT = FamilySymbol.Id`。
7. 建立 `TextNote`，文字只放 `typeMarkDisplay`。
8. 完成後執行安全清理，只嘗試刪 duplicated seed 原始內容。
9. 切換到新建立的 Legend 視圖。

## 安全清理

清理只使用 ElementId 集合，不依賴生成流程內部 id 名稱：

- `seedOriginalElementIds`：duplicate 後當下視圖內所有元素 id。
- `finalViewElementIds`：生成完成後視圖內所有元素 id。
- `protectedElementIds = finalViewElementIds - seedOriginalElementIds`，代表本次新生成的元素。

清理規則：

1. 對每個 `seedOriginalElementId` 開 `SubTransaction`。
2. 呼叫 `doc.Delete(originalId)`。
3. 讀取 Revit 實際回傳的 `deletedIds`。
4. 若 `deletedIds` 會刪到任何 `protectedElementIds`，rollback 該筆刪除。
5. 若沒有碰到 protected ids，commit 該筆刪除。

這樣可以避免 Revit cascade delete 把新生成的 Legend Component 一起刪掉。

## 錯誤規則

- `legend_seed_view_not_found`：指定 seed view 不存在、不是 Legend，或是 template。
- `legend_seed_component_not_found`：指定 seed view 沒有任何 Legend Component。
- `legend_component_type_swap_failed`：copy 後無法設定成目標 door/window type。

若指定 seed 失敗：

- 不提供其他 seed 建議。
- 不自動改試其他 seed。
- 交還使用者重新選擇。

## 輸出重點

create 成功時回傳：

- `legendViewId`
- `legendViewName`
- `seedLegendViewId`
- `seedLegendViewName`
- `usedTypeCount`
- `placedCount`
- `failedTypes[]`
- `CleanupMode`
- `CleanupDeletedCount`
- `CleanupSkippedCount`
- `CleanupSkippedOriginalIds[]`
- `CleanupProtectedElementCount`
- `CleanupDeletedElementIds[]`
