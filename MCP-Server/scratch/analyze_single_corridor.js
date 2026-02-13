/**
 * 走廊寬度分析 (支援多區段 L/T 型走廊)
 * 使用 Boundary Segments 精確計算
 */

import WebSocket from 'ws';
import {
    calculateCorridorWidth,
    analyzeMultiSegmentCorridor
} from '../src/utils/corridor-geometry.js';

const ws = new WebSocket('ws://localhost:8964');

// 已知的走廊 ID (從之前的測試)
const CORRIDOR_ID = 254533;
const MIN_WIDTH_MM = 1200;

ws.on('open', function () {
    console.log('=== 走廊寬度分析 (多區段支援) ===\n');
    console.log(`分析走廊 ID: ${CORRIDOR_ID}\n`);

    const command = {
        CommandName: 'get_room_info',
        Parameters: { roomId: CORRIDOR_ID },
        RequestId: 'analyze_corridor_' + Date.now()
    };

    ws.send(JSON.stringify(command));
});

ws.on('message', function (data) {
    const response = JSON.parse(data.toString());

    if (!response.Success) {
        console.error('❌ 錯誤:', response.Error);
        ws.close();
        return;
    }

    const roomData = response.Data;
    console.log('房間資訊:');
    console.log(`  名稱: ${roomData.Name}`);
    console.log(`  編號: ${roomData.Number}`);
    console.log(`  樓層: ${roomData.Level}`);
    console.log(`  面積: ${roomData.Area} m²`);

    // ========================================
    // 1. 單一寬度分析 (舊方法,僅供參考)
    // ========================================
    const singleResult = calculateCorridorWidth(
        roomData.BoundarySegments,
        roomData.BoundingBox
    );

    console.log(`\n【單一寬度分析】(僅分析最長區段)`);
    console.log(`  計算方法: ${singleResult.method}`);
    console.log(`  寬度: ${singleResult.width.toFixed(1)} mm`);

    // ========================================
    // 2. 多區段分析 (新方法)
    // ========================================
    const multiResult = analyzeMultiSegmentCorridor(
        roomData.BoundarySegments,
        MIN_WIDTH_MM
    );

    console.log(`\n【多區段分析】(分析所有區段)`);

    if (multiResult.error) {
        console.log(`  ❌ 錯誤: ${multiResult.error}`);
    } else {
        console.log(`  區段數量: ${multiResult.totalSegments}`);
        console.log(`  最小寬度: ${multiResult.minWidth.toFixed(1)} mm`);
        console.log(`  整體結果: ${multiResult.allPass ? '✅ PASS' : '❌ FAIL'}`);

        // 顯示每個區段的詳細資訊
        console.log(`\n  各區段詳情:`);
        multiResult.segments.forEach((seg, i) => {
            const icon = seg.status === 'PASS' ? '✅' : '❌';
            console.log(`    區段 ${i + 1} ${icon}:`);
            console.log(`      寬度: ${seg.width.toFixed(1)} mm`);
            console.log(`      方向: ${seg.direction}°`);
            console.log(`      長度: ${seg.length.toFixed(0)} mm`);
            console.log(`      中心點: (${seg.centerPoint.x}, ${seg.centerPoint.y})`);
        });

        // 如果有不合格區段,特別標註
        if (!multiResult.allPass) {
            console.log(`\n  ⚠️  不合格區段 (${multiResult.failedSegments.length} 個):`);
            multiResult.failedSegments.forEach((seg, i) => {
                console.log(`    區段 ${seg.segmentIndex + 1}:`);
                console.log(`      寬度: ${seg.width.toFixed(1)} mm (不足 ${(MIN_WIDTH_MM - seg.width).toFixed(0)} mm)`);
                console.log(`      位置: (${seg.centerPoint.x}, ${seg.centerPoint.y})`);
                console.log(`      建議: 需要加寬至 ${MIN_WIDTH_MM} mm`);
            });
        }
    }

    console.log(`\n標準: >= ${MIN_WIDTH_MM} mm (1.2m)`);

    ws.close();
});

ws.on('error', function (error) {
    console.error('連線錯誤:', error.message);
});

ws.on('close', function () {
    console.log('\n分析完成');
    process.exit(0);
});

setTimeout(() => {
    console.log('\n⏱️  分析超時');
    process.exit(1);
}, 10000);
