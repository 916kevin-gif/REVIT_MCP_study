/**
 * 除錯腳本 - 檢查區段分割邏輯
 */

import WebSocket from 'ws';

const ws = new WebSocket('ws://localhost:8964');
const CORRIDOR_ID = 254533;

ws.on('open', function () {
    console.log('=== 除錯: 檢查邊界線段 ===\n');

    const command = {
        CommandName: 'get_room_info',
        Parameters: { roomId: CORRIDOR_ID },
        RequestId: 'debug_' + Date.now()
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
    const segments = roomData.BoundarySegments;

    console.log(`邊界線段數量: ${segments.length}\n`);

    // 顯示每條線段的角度
    segments.forEach((seg, i) => {
        const dx = seg.EndX - seg.StartX;
        const dy = seg.EndY - seg.StartY;
        let angle = Math.atan2(dy, dx) * 180 / Math.PI;
        if (angle < 0) angle += 360;

        console.log(`線段 ${i + 1}:`);
        console.log(`  起點: (${seg.StartX}, ${seg.StartY})`);
        console.log(`  終點: (${seg.EndX}, ${seg.EndY})`);
        console.log(`  長度: ${seg.Length.toFixed(1)} mm`);
        console.log(`  角度: ${angle.toFixed(1)}°`);

        if (i > 0) {
            const prevDx = segments[i - 1].EndX - segments[i - 1].StartX;
            const prevDy = segments[i - 1].EndY - segments[i - 1].StartY;
            let prevAngle = Math.atan2(prevDy, prevDx) * 180 / Math.PI;
            if (prevAngle < 0) prevAngle += 360;

            let angleDiff = Math.abs(angle - prevAngle);
            if (angleDiff > 180) angleDiff = 360 - angleDiff;

            console.log(`  與前一線段角度差: ${angleDiff.toFixed(1)}°`);
        }
        console.log('');
    });

    ws.close();
});

ws.on('error', function (error) {
    console.error('連線錯誤:', error.message);
});

ws.on('close', function () {
    console.log('除錯完成');
    process.exit(0);
});

setTimeout(() => {
    console.log('\n⏱️  超時');
    process.exit(1);
}, 10000);
