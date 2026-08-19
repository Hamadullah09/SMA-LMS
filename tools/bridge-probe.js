// Sends one observation over the bridge and reports what the far end does with it.
//
// This exercises the same path a real read takes once it has left the library PC: authenticate,
// hello, observation, and then the server's ingest into the ordinary scan pipeline. Useful when
// there is no tag physically on the pad to wait for.
//
//   node tools/bridge-probe.js <wsUrl> <secret> <epc> [readerId]

const [, , url, secret, epc, readerIdArg] = process.argv;

if (!url || !secret || !epc) {
    console.error('usage: node tools/bridge-probe.js <wsUrl> <secret> <epc> [readerId]');
    process.exit(1);
}

const readerId = Number(readerIdArg || 1);
const socket = new WebSocket(url, { headers: { 'X-Bridge-Secret': secret } });

socket.addEventListener('open', () => {
    console.log('  connected');

    socket.send(JSON.stringify({
        type: 'hello', readerId, readerName: 'Bridge probe', online: true
    }));

    socket.send(JSON.stringify({
        type: 'observation',
        readerId,
        epc,
        observedUtc: new Date().toISOString(),
        rssi: -52,
        antenna: 1
    }));

    console.log('  sent an observation for ' + epc);

    // Give the far end a moment to ingest before the socket closes under it.
    setTimeout(() => { socket.close(); console.log('  closed'); }, 1500);
});

socket.addEventListener('error', e => {
    console.error('  socket error:', e.message || e.type);
    process.exit(1);
});
