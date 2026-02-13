/**
 * 除錯腳本：檢查房間邊界資料
 */

import WebSocket from 'ws';

const ws = new WebSocket('ws://localhost:8964');

ws.on('open', function () {
    console.log('=== 除錯：檢查房間 254533 的邊界資料 ===\n');

    const command = {
        CommandName: 'get_room_info',
        Parameters: { roomId: 254533 },
        RequestId: 'debug_room_' + Date.now()
    };
    ws.send(JSON.stringify(command));
});

ws.on('message', function (data) {
    const response = JSON.parse(data.toString());

    console.log('完整回應:');
    console.log(JSON.stringify(response, null, 2));

    if (response.Success && response.Data) {
        const roomData = response.Data;
        console.log('\n--- 房間資訊 ---');
        console.log('名稱:', roomData.Name);
        console.log('ID:', roomData.ElementId);
        console.log('面積:', roomData.Area, 'm²');

        console.log('\n--- 邊界線段 ---');
        if (roomData.BoundarySegments) {
            console.log('線段數量:', roomData.BoundarySegments.length);
            roomData.BoundarySegments.forEach((seg, i) => {
                console.log(`\n線段 ${i + 1}:`);
                console.log('  長度:', seg.Length, 'mm');
                console.log('  起點:', seg.Start);
                console.log('  終點:', seg.End);
            });
        } else {
            console.log('⚠️ BoundarySegments 為 null 或 undefined');
        }
    } else {
        console.error('錯誤:', response.Error);
    }

    ws.close();
});

ws.on('error', function (error) {
    console.error('連線錯誤:', error.message);
});

setTimeout(() => {
    console.log('\n⏱️  超時');
    process.exit(1);
}, 10000);
