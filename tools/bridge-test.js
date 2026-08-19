// Builds the two appsettings files for a local end-to-end bridge test.
//
//   "cloud"   - no reader hardware, accepts a bridge. Stands in for library.sma-techno.net.
//   "library" - holds the real reader and dials the cloud instance.
//
// Both point at the same local database, which is fine for the test: what is being proved is that
// reads picked up by the library instance appear in the cloud instance's kiosk, and that is
// carried by the WebSocket rather than the database.
const fs = require('fs');
const path = require('path');

const [, , cloudDir, secret] = process.argv;
if (!cloudDir || !secret) {
    console.error('usage: node tools/bridge-test.js <cloudDir> <secret>');
    process.exit(1);
}

const base = JSON.parse(fs.readFileSync('appsettings.json', 'utf8').replace(/^﻿/, ''));

// ---- cloud: listens, drives no hardware -----------------------------------
const cloud = JSON.parse(JSON.stringify(base));
cloud.Rfid = Object.assign({}, base.Rfid, { AutoConnect: false, AutoDiscover: false });
cloud.Rfid.Bridge = { Secret: secret, ReaderId: 1 };
fs.writeFileSync(path.join(cloudDir, 'appsettings.json'), JSON.stringify(cloud, null, 2));

// ---- library: holds the reader, dials out ---------------------------------
const library = JSON.parse(JSON.stringify(base));
library.Rfid = Object.assign({}, base.Rfid, { AutoConnect: true, AutoDiscover: true });
library.Rfid.Bridge = {
    Url: 'ws://localhost:5100/rfid/bridge',
    Secret: secret,
    ReaderId: 1,
    HeartbeatSeconds: 5
};
fs.writeFileSync('appsettings.json', JSON.stringify(library, null, 2));

console.log('cloud   : reader off, bridge server on');
console.log('library : reader on, dialling ws://localhost:5100/rfid/bridge');
