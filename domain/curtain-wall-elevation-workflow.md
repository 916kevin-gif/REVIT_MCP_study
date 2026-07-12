---
name: curtain-wall-elevation-workflow
description: "帷幕牆外立面視圖與帷幕表前置工作流 SOP：說明如何收集帷幕牆、建立外側 ElevationMarker 視圖、套用帷幕立面 view template，並避免把 tool discovery 問題誤判成 domain 缺漏。"
metadata:
  version: "1.0"
  updated: "2026-07-12"
  created: "2026-07-12"
  references: []
  related:
    - curtain-wall-pattern.md
    - door-window-legend-workflow.md
    - sheet-viewport-management.md
    - wall-check.md
  referenced_by:
    - curtain-wall
    - create_curtain_wall_elevations
  contributors:
    - "gpt-5"
  tags: [curtain wall, elevation, curtain schedule, view template, antigravity, mcp]
---

# 帷幕牆外立面視圖工作流

## 適用情境

用於產生「帷幕表」前的視圖準備：每一道帷幕牆需要一張外立面圖，後續才能做標註、尺寸、排版與表格化。

這份 SOP 處理的是 **view workflow**，不是 `curtain-wall-pattern.md` 的 panel pattern workflow。兩者差異如下：

| 工作 | 目標 | 主要 Revit API / Tool |
|---|---|---|
| 帷幕 panel pattern | 讀取/套用 panel type 與 grid pattern | `CurtainGrid`, `apply_panel_pattern` |
| 帷幕外立面 | 為每道帷幕牆建立外側 elevation view | `ElevationMarker`, `create_curtain_wall_elevations` |

## 成功標準

- 專案內每一道 `CurtainGrid != null` 的牆都被視為帷幕牆候選。
- 每道可處理的帷幕牆建立一張 Revit 原生 Elevation view。
- 立面視圖名稱使用 `{樓層}{分隔字串}{標記}`；標記空白時 fallback 為 `CW-{ElementId}`。
- 立面 marker 放在 `wall.Orientation` 指向的外側。
- 立面觀看方向朝向牆面，也就是 `-wall.Orientation`。
- 每張立面套用名為 `帷幕立面` 的 View Template。
- `帷幕立面` 樣板只保留帷幕所需元素、樓層線與牆標籤。
- Crop box 與 far clip depth 不受樣板控制，允許每張視圖保留各自裁切範圍。

## Tool Contract

MCP tool：

```text
create_curtain_wall_elevations
```

最小呼叫參數：

```json
{}
```

常用參數：

```json
{
  "scale": 50,
  "offsetMm": 1500,
  "horizontalMarginMm": 300,
  "verticalMarginMm": 300,
  "depthMm": 1200,
  "viewTemplateName": "帷幕立面",
  "applyViewTemplate": true,
  "nameSeparator": ""
}
```

回傳結果必須包含：

- `TotalCurtainWalls`
- `CreatedCount`
- `SkippedCount`
- `Created[]`
- `Skipped[]`
- `ViewTemplateName`
- `TemplateCreated`
- `TemplateUpdated`

## 實作規則

### 1. 收集帷幕牆

用 `FilteredElementCollector(doc).OfClass(typeof(Wall))` 收集牆，再用 `wall.CurtainGrid != null` 判定帷幕牆。

不要用門窗邏輯處理帷幕牆。門窗是 family instance；帷幕牆是 wall system，panel/mullion 是其子系統。

### 2. 樓層與標記

樓層：

```text
doc.GetElement(wall.LevelId) as Level
```

標記：

```text
BuiltInParameter.ALL_MODEL_MARK
```

若標記為空，使用：

```text
CW-{wall.Id}
```

### 3. 外側方向

外側以 `wall.Orientation` 為準。這是 deterministic rule，不依賴房間資料。

如果專案模型的牆方向建錯，應先用 wall orientation check 修正或由後續版本提供 flip/room-detection 選項；不要在此 workflow 中自行猜測。

### 4. 建立 ElevationMarker

marker 放置點：

```text
wall midpoint + wall.Orientation * (wall.Width / 2 + offset)
```

建立方式：

```text
ElevationMarker.CreateElevationMarker(...)
marker.CreateElevation(doc, placementView.Id, 0)
```

建立後旋轉 marker，使 view direction 對齊：

```text
-wall.Orientation
```

### 5. View Template

樣板名稱預設：

```text
帷幕立面
```

若樣板存在就更新；不存在則用第一張成功建立的 elevation view 建立 template。

樣板保留類別：

| BuiltInCategory | 用途 |
|---|---|
| `OST_Walls` | 帷幕牆本體與牆類元素 |
| `OST_CurtainWallPanels` | 帷幕 panel |
| `OST_CurtainWallMullions` | 帷幕 mullion |
| `OST_Levels` | 樓層線 |
| `OST_WallTags` | 牆標籤 |

其他可隱藏的 model/annotation category 預設隱藏。

### 6. 樣板不控制的項目

以下設定必須排除在 template controlled parameters 之外：

- crop box
- crop region visibility
- far clip
- far clip offset / depth

目標是讓每一道帷幕牆的立面仍可有自己的裁切範圍與深度。

## 常見誤判

### Antigravity 找不到 tool

這不是 domain 缺漏。

Domain file 只定義 SOP，不會讓 MCP tool 出現在 AI client 裡。若 Antigravity 把 `create_curtain_wall_elevations` 當成文字任務，開始執行 `Get-ChildItem` 搜檔案，代表它沒有成功呼叫 Revit MCP tool。

檢查順序：

1. Antigravity 是否開啟 repo root：

```text
D:\RevitMCP\REVIT_MCP_study
```

2. `.mcp.json` 是否由 Antigravity 載入。
3. `MCP-Server/build/tools/curtain-wall-tools.js` 是否包含 `create_curtain_wall_elevations`。
4. MCP profile 是否為 `full` 或 `architect`。
5. Revit 2024 是否已重開並載入新版 `RevitMCP.dll`。
6. Revit MCP service 是否啟動並連到 localhost `8964`。

如果 tool call 成功，流程應是：

```text
AI client tool call
-> MCP-Server executeRevitTool("create_curtain_wall_elevations", args)
-> localhost:8964
-> Revit add-in CommandExecutor
```

不應出現 `Get-ChildItem` 掃描使用者資料夾。

## 後續延伸

此 workflow 只負責建立外立面視圖與樣板。帷幕表後續階段可再分成：

- 每張立面自動標註 panel/mullion 尺寸。
- 將立面放置到 sheet。
- 產生帷幕表索引。
- 匯出 PDF 或圖紙集。
