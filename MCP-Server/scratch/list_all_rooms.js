
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
        const levelData = await call('get_all_levels');
        for (const level of levelData.Levels) {
            const roomData = await call('get_rooms_by_level', { level: level.Name });
            console.log(`樓層 ${level.Name} 的房間：`);
            roomData.Rooms.forEach(r => console.log(` - [${r.ElementId}] ${r.Name}`));
        }
    } catch (e) { console.error(e); }
    ws.close();
});
