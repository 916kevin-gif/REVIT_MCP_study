
import WebSocket from 'ws';

const ws = new WebSocket('ws://localhost:8964');

const call = (command, params = {}) => {
    return new Promise((resolve, reject) => {
        const requestId = `${command}_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
        const timeout = setTimeout(() => {
            reject(new Error(`Command ${command} timed out`));
        }, 30000);

        const listener = (data) => {
            const response = JSON.parse(data.toString());
            // console.log(`Debug Response for ${command}:`, JSON.stringify(response, null, 2));
            if (response.RequestId === requestId) {
                ws.removeListener('message', listener);
                clearTimeout(timeout);
                if (response.Success) {
                    resolve(response.Data);
                } else {
                    reject(new Error(response.Error || 'Unknown error'));
                }
            }
        };

        ws.on('message', listener);
        ws.send(JSON.stringify({
            CommandName: command,
            Parameters: params,
            RequestId: requestId
        }));
    });
};

ws.on('open', async () => {
    console.log('=== 走廊寬度分析 (完整版) ===\n');

    try {
        // 1. 取得所有樓層
        const levelData = await call('get_all_levels');
        const levels = levelData.Levels || [];
        console.log(`找到 ${levels.length} 個樓層。`);

        let allCorridors = [];

        // 2. 遍歷樓層取得房間
        for (const level of levels) {
            const roomData = await call('get_rooms_by_level', { level: level.Name });
            if (roomData && roomData.Rooms) {
                const corridorsInLevel = roomData.Rooms.filter(room => {
                    const name = room.Name || '';
                    return name.includes('走廊') ||
                        name.toLowerCase().includes('corridor') ||
                        name.includes('廊道') ||
                        name.includes('通道') ||
                        name.includes('廊下');
                });

                // 為每個走廊取得詳細資訊（特別是 BoundingBox）
                for (const basicRoom of corridorsInLevel) {
                    try {
                        const detailedRoom = await call('get_room_info', { roomId: basicRoom.ElementId });
                        allCorridors.push(detailedRoom);
                    } catch (e) {
                        console.warn(`無法取得房間 ${basicRoom.ElementId} 的詳細資訊: ${e.message}`);
                    }
                }
            }
        }

        if (allCorridors.length === 0) {
            console.log('未找到任何走廊房間。');
            ws.close();
            return;
        }

        console.log(`\n找到 ${allCorridors.length} 個走廊：\n`);
        console.log('----------------------------------------------------------------------');
        console.log('ID\t名稱\t\t樓層\t寬度(m)\t結果');
        console.log('----------------------------------------------------------------------');

        allCorridors.forEach(room => {
            let width = 0;
            let result = '未知';

            if (room.BoundingBox) {
                const dx = Math.abs(room.BoundingBox.MaxX - room.BoundingBox.MinX);
                const dy = Math.abs(room.BoundingBox.MaxY - room.BoundingBox.MinY);
                // 估計寬度為較短的那一邊
                width = Math.min(dx, dy) / 1000; // 轉換為公尺
                result = width >= 1.2 ? '✅ 合規' : '❌ 過窄';
            }

            const nameStr = (room.Name || 'N/A').padEnd(12);
            const levelStr = (room.Level || 'N/A').padEnd(8);
            const widthStr = width > 0 ? width.toFixed(2) : 'N/A';

            console.log(`${room.ElementId}\t${nameStr}\t${levelStr}\t${widthStr}\t${result}`);
        });
        console.log('----------------------------------------------------------------------');

    } catch (err) {
        console.error('執行過程中發生錯誤:', err.message);
    } finally {
        ws.close();
    }
});

ws.on('error', function (error) {
    console.error('連線錯誤:', error.message);
});

ws.on('close', function () {
    process.exit(0);
});
