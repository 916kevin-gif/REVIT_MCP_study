/**
 * 簡單的連線測試腳本
 */

import WebSocket from 'ws';

console.log('測試 WebSocket 連線到 localhost:8964...\n');

const ws = new WebSocket('ws://localhost:8964');

ws.on('open', function () {
    console.log('✅ 連線成功！');

    // 測試簡單的查詢
    const command = {
        CommandName: 'get_project_info',
        Parameters: {},
        RequestId: 'test_' + Date.now()
    };

    console.log('發送測試命令: get_project_info');
    ws.send(JSON.stringify(command));
});

ws.on('message', function (data) {
    console.log('\n✅ 收到回應:');
    const response = JSON.parse(data.toString());
    console.log(JSON.stringify(response, null, 2));
    ws.close();
});

ws.on('close', function () {
    console.log('\n連線已關閉');
    process.exit(0);
});

ws.on('error', function (error) {
    console.error('❌ 連線錯誤:', error.message);
    console.error('完整錯誤:', error);
    process.exit(1);
});

setTimeout(() => {
    console.log('\n⏱️  連線超時');
    process.exit(1);
}, 5000);
