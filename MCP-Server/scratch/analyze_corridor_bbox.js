/**
 * 走廊寬度分析腳本 (使用 BoundingBox 替代方案)
 * 當 BoundarySegments 不可用時，使用 BoundingBox 估算寬度
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
    const response = JSON.parse(data.toString());

    if (step === 'GET_ROOMS') {
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

    step = 'ANALYZING_CORRIDOR';
    const command = {
        CommandName: 'get_room_info',
        Parameters: { roomId: nextCorridor.ElementId },
        RequestId: `get_room_${nextCorridor.ElementId}`
    };
    ws.send(JSON.stringify(command));
}

function analyzeCorridor(corridor, roomData) {
    // 策略 1: 使用 BoundarySegments (如果可用)
    if (roomData.BoundarySegments && roomData.BoundarySegments.length >= 2) {
        try {
            const { width, orientation } = calculateCorridorWidth(roomData.BoundarySegments);
            const status = width >= MIN_WIDTH_MM ? 'PASS' : 'FAIL';

            results.push({
                name: corridor.Name,
                id: corridor.ElementId,
                width: width,
                status: status,
                method: 'BoundarySegments',
                orientation: orientation,
                message: width >= MIN_WIDTH_MM ? '符合標準' : `寬度不足 (${(MIN_WIDTH_MM - width).toFixed(0)}mm)`
            });
            return;
        } catch (e) {
            console.log(`  [警告] BoundarySegments 分析失敗: ${e.message}，改用 BoundingBox`);
        }
    }

    // 策略 2: 使用 BoundingBox (備用方案)
    if (roomData.BoundingBox) {
        const bbox = roomData.BoundingBox;
        const widthX = Math.abs(bbox.MaxX - bbox.MinX);
        const widthY = Math.abs(bbox.MaxY - bbox.MinY);

        // 走廊通常是長條形，取較小的邊作為寬度
        const width = Math.min(widthX, widthY);
        const status = width >= MIN_WIDTH_MM ? 'PASS' : 'FAIL';

        results.push({
            name: corridor.Name,
            id: corridor.ElementId,
            width: width,
            status: status,
            method: 'BoundingBox (估算)',
            message: width >= MIN_WIDTH_MM ? '符合標準 (估算)' : `寬度不足 (${(MIN_WIDTH_MM - width).toFixed(0)}mm, 估算)`
        });
        return;
    }

    // 策略 3: 無法分析
    results.push({
        name: corridor.Name,
        id: corridor.ElementId,
        width: 0,
        status: 'ERROR',
        method: 'N/A',
        message: '無法取得幾何資訊'
    });
}

function calculateCorridorWidth(segments) {
    // 簡單幾何分析邏輯
    const lines = segments.map(seg => {
        const dx = seg.End.X - seg.Start.X;
        const dy = seg.End.Y - seg.Start.Y;
        return {
            start: seg.Start,
            end: seg.End,
            length: Math.sqrt(dx * dx + dy * dy),
            angle: (Math.atan2(dy, dx) * 180 / Math.PI + 360) % 180
        };
    }).filter(l => l.length > 500); // 忽略 < 50cm 的短線

    if (lines.length < 2) throw new Error('有效長邊線段不足');

    // 分組找出主要方向
    const groups = {};
    lines.forEach(line => {
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

    // 計算寬度
    const longLines = groups[mainDir];
    const angleRad = parseFloat(mainDir) * Math.PI / 180;
    const perpX = -Math.sin(angleRad);
    const perpY = Math.cos(angleRad);

    const projections = longLines.map(l => {
        const midX = (l.start.X + l.end.X) / 2;
        const midY = (l.start.Y + l.end.Y) / 2;
        return midX * perpX + midY * perpY;
    });

    projections.sort((a, b) => a - b);
    const width = Math.abs(projections[projections.length - 1] - projections[0]);

    return {
        width: Math.round(width * 100) / 100,
        orientation: mainDir
    };
}

function printReport() {
    console.log('\n=== 分析報告 (標準: >= 1.2m) ===');
    console.log('---------------------------------------------------------------------------------');
    console.log('| ID        | 名稱                | 寬度 (mm) | 狀態 | 方法 | 說明');
    console.log('---------------------------------------------------------------------------------');

    results.forEach(r => {
        let widthStr = r.width === 'N/A' || r.width === 0 ? '---' : r.width.toFixed(1);
        let statusIcon = r.status === 'PASS' ? '✅' : (r.status === 'ERROR' ? '⚠️' : '❌');
        let method = r.method || 'N/A';

        console.log(`| ${r.id.toString().padEnd(9)} | ${r.name.padEnd(15)} | ${widthStr.padStart(9)} | ${statusIcon} ${r.status} | ${method.padEnd(20)} | ${r.message}`);
    });
    console.log('---------------------------------------------------------------------------------');
}

ws.on('error', function (error) {
    console.error('連線錯誤:', error.message);
});

setTimeout(() => {
    console.log('\n⏱️  分析超時');
    process.exit(1);
}, TIMEOUT_MS);
