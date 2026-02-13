/**
 * 使用 get_rooms_by_level 替代方案
 */

import WebSocket from 'ws';

const ws = new WebSocket('ws://localhost:8964');

ws.on('open', function () {
    console.log('=== 測試 get_rooms_by_level 命令 ===\n');

    const command = {
        CommandName: 'get_rooms_by_level',
        Parameters: {
            level: 'FL1'  // 假設樓層名稱是 FL1
        },
        RequestId: 'test_rooms_' + Date.now()
    };

    console.log('發送命令:', JSON.stringify(command, null, 2));
    ws.send(JSON.stringify(command));
});

ws.on('message', function (data) {
    console.log('\n收到回應:');
    const response = JSON.parse(data.toString());

    console.log('Success:', response.Success);
    console.log('Error:', response.Error);

    if (response.Data) {
        console.log('\n房間資料:');
        console.log('Level:', response.Data.Level);
        console.log('TotalRooms:', response.Data.TotalRooms);

        if (response.Data.Rooms && response.Data.Rooms.length > 0) {
            console.log('\n房間列表:');
            response.Data.Rooms.forEach(room => {
                console.log(`  - ${room.Name} (ID: ${room.ElementId})`);
            });
        }
    }

    ws.close();
});

ws.on('error', function (error) {
    console.error('連線錯誤:', error.message);
});

ws.on('close', function () {
    console.log('\n連線已關閉');
    process.exit(0);
});

setTimeout(() => {
    console.log('\n⏱️  超時');
    process.exit(1);
}, 10000);
