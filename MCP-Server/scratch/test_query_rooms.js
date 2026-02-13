/**
 * 簡化測試：直接查詢房間
 */

import WebSocket from 'ws';

const ws = new WebSocket('ws://localhost:8964');

ws.on('open', function () {
    console.log('=== 測試 query_elements 命令 ===\n');

    const command = {
        CommandName: 'query_elements',
        Parameters: {
            category: 'Rooms'
        },
        RequestId: 'test_query_' + Date.now()
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
        const elements = response.Data.Elements || response.Data.Rooms || [];
        console.log('Elements 數量:', elements.length);

        if (elements.length > 0) {
            console.log('\n第一個元素:');
            console.log(JSON.stringify(elements[0], null, 2));
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
