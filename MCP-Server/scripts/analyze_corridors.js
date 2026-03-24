/**
 * 走廊寬度分析腳本
 * 依據 domain/corridor-analysis-protocol.md 執行檢查
 */

import WebSocket from 'ws';

const ws = new WebSocket('ws://localhost:8964');

// 配置
const MIN_WIDTH_MM = 1200; // 1.2m
const TIMEOUT_MS = 60000;

// 狀態
let step = 'GET_ROOMS';
let corridors = [];
let pendingCorridors = [];
let results = [];

ws.on('open', function () {
    console.log('=== 走廊寬度分析 (標準: >= 1.2m) ===\n');

    // Step 1: 取得所有房間
    const command = {
        CommandName: 'query_elements',
        Parameters: {
            category: 'Rooms'
        },
        RequestId: 'get_rooms_' + Date.now()
    };
    ws.send(JSON.stringify(command));
});

ws.on('message', function (data) {
    // console.log('收到訊息:', data.toString().substring(0, 100) + '...'); // Debug logging
    const response = JSON.parse(data.toString());

    if (step === 'GET_ROOMS') {
        // query_elements returns: { Elements: [...] }
        const rooms = response.Data ? (response.Data.Elements || response.Data.Rooms) : [];

        if (response.Success && rooms && rooms.length > 0) {
            console.log(`掃描到 ${rooms.length} 個房間`);

            // 篩選走廊
            corridors = rooms.filter(room =>
                room.Name && (
                    room.Name.includes('走廊') ||
                    room.Name.toLowerCase().includes('corridor') ||
                    room.Name.includes('廊道') ||
                    room.Name.includes('通道') ||
                    room.Name.includes('廊下') ||
                    room.Name.includes('廊')
                )
            );

            console.log(`識別出 ${corridors.length} 個走廊/通道`);

            if (corridors.length === 0) {
                console.log('未發現相關房間，結束分析。');
                ws.close();
                return;
            }

            pendingCorridors = [...corridors];
            step = 'ANALYZE_NEXT_CORRIDOR';
            processNextCorridor();

        } else {
            console.error('取得房間失敗:', response.Error);
            ws.close();
        }
    } else if (step === 'ANALYZING_CORRIDOR') {
        const currentCorridor = corridors[corridors.length - pendingCorridors.length - 1];

        if (response.Success && response.Data) {
            const roomData = response.Data;
            analyzeCorridor(currentCorridor, roomData);
        } else {
            console.error(`無法取得房間資訊 [${currentCorridor.Name}] (ID: ${currentCorridor.ElementId}):`, response.Error);
            results.push({
                name: currentCorridor.Name,
                id: currentCorridor.ElementId,
                width: 'N/A',
                status: 'ERROR',
                message: '無法取得幾何資訊'
            });
        }

        step = 'ANALYZE_NEXT_CORRIDOR';
        processNextCorridor();
    }
});

function processNextCorridor() {
    if (pendingCorridors.length === 0) {
        // 完成所有分析
        printReport();
        ws.close();
        return;
    }

    const nextCorridor = pendingCorridors.shift();
    // console.log(`正在分析: ${nextCorridor.Name} (ID: ${nextCorridor.ElementId})...`);

    step = 'ANALYZING_CORRIDOR';
    const command = {
        CommandName: 'get_room_info',
        Parameters: { roomId: nextCorridor.ElementId },
        RequestId: `get_room_${nextCorridor.ElementId}`
    };
    ws.send(JSON.stringify(command));
}

function analyzeCorridor(corridor, roomData) {
    if (!roomData.BoundarySegments || roomData.BoundarySegments.length < 2) {
        results.push({
            name: corridor.Name,
            id: corridor.ElementId,
            width: 0,
            status: 'ERROR',
            message: '無效的邊界資料'
        });
        return;
    }

    try {
        const { width, orientation } = calculateCorridorWidth(roomData.BoundarySegments);
        const status = width >= MIN_WIDTH_MM ? 'PASS' : 'FAIL';

        results.push({
            name: corridor.Name,
            id: corridor.ElementId,
            width: width,
            status: status,
            orientation: orientation,
            message: width >= MIN_WIDTH_MM ? '符合標準' : `寬度不足 (${(MIN_WIDTH_MM - width).toFixed(0)}mm)`
        });
    } catch (e) {
        results.push({
            name: corridor.Name,
            id: corridor.ElementId,
            width: 0,
            status: 'ERROR',
            message: e.message
        });
    }
}

function calculateCorridorWidth(segments) {
    // 簡單幾何分析邏輯 (參考 create_corridor_dimension.js)

    // 1. 正規化線段
    const lines = segments.map(seg => {
        const dx = seg.End.X - seg.Start.X;
        const dy = seg.End.Y - seg.Start.Y;
        return {
            start: seg.Start,
            end: seg.End,
            length: Math.sqrt(dx * dx + dy * dy), // 使用計算長度而非依賴屬性
            // 角度 0-180
            angle: (Math.atan2(dy, dx) * 180 / Math.PI + 360) % 180
        };
    }).filter(l => l.length > 500); // 忽略 < 50cm 的短線

    if (lines.length < 2) throw new Error('有效長邊線段不足');

    // 2. 分組找出主要方向
    const groups = {};
    lines.forEach(line => {
        // 5度容差分組
        const key = Math.round(line.angle / 5) * 5;
        if (!groups[key]) groups[key] = [];
        groups[key].push(line);
    });

    let mainDir = null;
    let maxLen = 0;

    for (const key in groups) {
        const totalLen = groups[key].reduce((sum, l) => sum + l.length, 0);
        if (totalLen > maxLen) {
            maxLen = totalLen;
            mainDir = key;
        }
    }

    if (!mainDir) throw new Error('無法判斷主要走向');

    // 3. 計算寬度
    const longLines = groups[mainDir];
    const angleRad = parseFloat(mainDir) * Math.PI / 180;
    // 垂直向量
    const perpX = -Math.sin(angleRad);
    const perpY = Math.cos(angleRad);

    // 投影到垂直軸 (Project onto perpendicular axis)
    // p = x * perpX + y * perpY
    const projections = longLines.map(l => {
        const midX = (l.start.X + l.end.X) / 2;
        const midY = (l.start.Y + l.end.Y) / 2;
        return midX * perpX + midY * perpY;
    });

    // 找出最大跨度
    projections.sort((a, b) => a - b);

    // 假設是最外圍的兩組牆 (Min and Max)
    // 這裡可能需要更複雜的邏輯來處理"中島"或"凹凸"，但由外向內的淨寬通常由最外側決定
    // 或者是尋找最大的 gap? 
    // 對於單純走廊，通常是 Max - Min
    const width = Math.abs(projections[projections.length - 1] - projections[0]);

    return {
        width: Math.round(width * 100) / 100, // round to 2 decimals
        orientation: mainDir
    };
}

function printReport() {
    console.log('\n=== 分析報告 (標準: >= 1.2m) ===');
    console.log('---------------------------------------------------------------------------------');
    console.log('| ID        | 名稱                | 寬度 (mm) | 狀態 | 說明');
    console.log('---------------------------------------------------------------------------------');

    results.forEach(r => {
        let widthStr = r.width === 'N/A' || r.width === 0 ? '---' : r.width.toFixed(1);
        let nameStr = r.name.padEnd(20).slice(0, 20); // 簡單截斷
        let statusIcon = r.status === 'PASS' ? '✅' : (r.status === 'ERROR' ? '⚠️' : '❌');

        console.log(`| ${r.id.toString().padEnd(9)} | ${r.name.padEnd(15)} | ${widthStr.padStart(9)} | ${statusIcon} ${r.status} | ${r.message}`);
    });
    console.log('---------------------------------------------------------------------------------');
}

ws.on('error', function (error) {
    console.error('連線錯誤:', error.message);
});

// 超時保護
setTimeout(() => {
    console.log('\n⏱️  分析超時');
    process.exit(1);
}, TIMEOUT_MS);
