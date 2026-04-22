# feat: 新增 `create_finish_legend` — 粉刷層與油漆材料圖例自動建立

## 摘要

新增 MCP 工具 `create_finish_legend`，讓 AI 助理能一鍵在 Revit 中自動產出「粉刷填滿圖例」Legend 視圖。同時修復 Legend 文字渲染的兩個既有 bug（背景不透明、文字壓格線）。

---

## 新功能

### `create_finish_legend` 工具

自動掃描全專案材料來源並繪製三張圖例表（地坪 / 牆面 / 天花）：

| 來源 | 掃描方式 | 去重 key |
|------|----------|----------|
| **粉刷層** | Wall/Floor/Ceiling 類型的 CompoundStructure，取 Function=Finish 層 | `(Category, TypeId)` |
| **油漆材料** | Wall/Floor/Ceiling 元素上用「油漆工具」塗色的面，依面法向量分類 | `(Category, MaterialId)` |

每張表三欄：**編號**（TypeMark / Material.Mark）｜**圖例**（FilledRegion 填滿樣式）｜**說明**（TypeName / Material.Description）

粉刷列在上、油漆列在下，中間以「── 油漆材料 ──」分隔列隔開。若某類別無油漆材料，不插入分隔列。

### FilledRegionType 自動建立

- 粉刷類型：命名為 `TypeMark + TypeName`（如 `F1 整體粉光+彈泥`）
- 油漆材料：命名為 `Paint {MaterialName}`（避免與粉刷同名衝突）
- 已存在同名則複用，不覆蓋設定（idempotent）
- 無 SurfacePattern 時 fallback 為 Solid Fill + 材料色，說明欄加 `(僅顏色)`

### 回傳 Schema

```json
{
  "success": true,
  "legendViewId": 12345,
  "legendViewName": "粉刷圖例_20260422",
  "isNewLegend": true,
  "filledRegionTypes": { "created": 8, "reused": 3, "paintCreated": 5, "paintReused": 1 },
  "rows": { "floors": 4, "walls": 5, "ceilings": 2, "paintFloors": 2, "paintWalls": 3, "paintCeilings": 1 },
  "warnings": ["..."]
}
```

---

## Bug Fix：Legend 文字渲染

### 1. 文字背景不透明（白底遮蓋格線）

**根因**：`BuiltInParameter.TEXT_BACKGROUND` 對 `TextNoteType` 回傳 null，且值語意反直覺（`1 = 透明`）。

**修法**：新增 `FindParameterByAnyName()` helper，以 Definition.Name 多語言 fallback（`"Background"` / `"背景"`）找到參數後 `Set(1)`。

### 2. 文字壓在表格格線上（不在欄位內）

**根因**：`TextNoteType` 的 bounding box 含行距，實際高度約為 em-size 的 1.33 倍（3mm em × 100 比例 = 30cm model，實際 box ≈ 40cm）。原本以純 em-size 計算偏移量（15cm），導致文字底邊恰好壓在格線上。

**修法**：依字元類型分兩個常數補償：

| 常數 | 值 | 適用文字 |
|------|----|---------|
| `LEGEND_TEXT_HEIGHT_HALF_CM` | `20` | 類型標記（F2a、W1 等短碼） |
| `LEGEND_TEXT_HEIGHT_HALF_CM_ZH` | `28` | 中文多字元（標題、表頭、說明欄、分隔列） |

---

## 修改的檔案

| 檔案 | 性質 | 說明 |
|------|------|------|
| `MCP/Core/Commands/CommandExecutor.FinishLegend.cs` | **新增** | 全部實作（~900 行） |
| `MCP-Server/src/tools/room-tools.ts` | 修改 | 新增 `create_finish_legend` tool 定義 |
| `domain/finish-legend-creation.md` | **新增** | Domain SOP 文件（v1.2） |
| `MCP/Core/CommandExecutor.cs` | 修改 | 新增 case dispatch |
| `MCP/Core/Commands/CommandExecutor.RoomSurface.cs` | 修改 | 共用 `DetectFinishLayers` helper |
| `domain/room-surface-area-review.md` | 修改 | 補充粉刷層偵測相關說明 |
| `CLAUDE.md` | 修改 | 新增 trigger keywords 與 domain 指向 |

---

## 測試清單

- [ ] 專案含粉刷層 Wall/Floor/Ceiling → 三張表均正確產出
- [ ] 被油漆工具塗色的面 → 出現在對應類別表內、分隔列正確顯示
- [ ] 同材料塗在牆和天花 → 兩張表各一筆
- [ ] Mark / Description 為空 → 顯示 `(未填)`
- [ ] 無 SurfacePattern → Solid Fill fallback + `(僅顏色)` 標記
- [ ] 第二次呼叫 → FilledRegionType 全部 reused
- [ ] 文字背景透明、不壓格線、視覺置中
- [ ] Revit 2022–2026 語言版本不影響行為（無語言相依字串）
