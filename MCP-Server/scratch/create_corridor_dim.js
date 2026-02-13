
import WebSocket from 'ws';

const ws = new WebSocket('ws://localhost:8964');

const call = (command, params = {}) => {
    return new Promise((resolve, reject) => {
        const requestId = `${command}_${Date.now()}`;
        const listener = (data) => {
            const response = JSON.parse(data.toString());
            if (response.RequestId === requestId) {
                ws.removeListener('message', listener);
                if (response.Success) resolve(response.Data);
                else reject(new Error(response.Error));
            }
        };
        ws.on('message', listener);
        ws.send(JSON.stringify({ CommandName: command, Parameters: params, RequestId: requestId }));
    });
};

ws.on('open', async () => {
    try {
        console.log('=== 建立走廊標註 ===');

        // 1. 取得目前視圖
        const activeView = await call('get_active_view');
        console.log(`目前視圖: ${activeView.Name} (ID: ${activeView.ElementId})`);

        if (activeView.ViewType !== 'FloorPlan') {
            console.error('錯誤: 目前視圖並非平面圖，無法建立標註。');
            ws.close();
            return;
        }

        // 2. 取得走廊詳細資訊 (ID: 254533)
        const corridor = await call('get_room_info', { roomId: 254533 });
        const bbox = corridor.BoundingBox;

        if (!bbox) {
            console.error('錯誤: 找不到走廊的 BoundingBox');
            ws.close();
            return;
        }

        const centerPoint = {
            x: (bbox.MinX + bbox.MaxX) / 2,
            y: (bbox.MinY + bbox.MaxY) / 2
        };

        const dx = Math.abs(bbox.MaxX - bbox.MinX);
        const dy = Math.abs(bbox.MaxY - bbox.MinY);

        let startX, startY, endX, endY;

        // 如果是橫向走廊 (dx > dy)，則標註 Y 方向 (寬度)
        if (dx > dy) {
            console.log('判斷為橫向走廊，標註 Y 方向寬度');
            startX = centerPoint.x;
            startY = bbox.MinY;
            endX = centerPoint.x;
            endY = bbox.MaxY;
        } else {
            console.log('判斷為縱向走廊，標註 X 方向寬度');
            startX = bbox.MinX;
            startY = centerPoint.y;
            endX = bbox.MaxX;
            endY = centerPoint.y;
        }

        // 3. 建立標註
        const result = await call('create_dimension', {
            viewId: activeView.ElementId,
            startX: startX,
            startY: startY,
            endX: endX,
            endY: endY,
            offset: 0 // 置中於中心點
        });

        console.log(`✅ 標註建立成功！ID: ${result.DimensionId}, 數值: ${result.Value} mm`);

    } catch (err) {
        console.error('發生錯誤:', err.message);
    } finally {
        ws.close();
    }
});
