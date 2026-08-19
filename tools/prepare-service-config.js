// Makes an installed service configuration fit to run in Production.
//
// A service has no launchSettings.json, so it starts in Production, where ProductionGuards refuses
// anything unsafe. The config copied out of a development source tree is not that, and the first
// symptom is a service that will not start with nothing in the PowerShell output to say why.
const fs = require('fs');

const path = process.argv[2];
const listenUrl = process.argv[3];

const raw = fs.readFileSync(path, 'utf8');
const bom = raw.charCodeAt(0) === 0xFEFF;
const o = JSON.parse(bom ? raw.slice(1) : raw);
const changes = [];

// Would insert fictional books and simulated tags into the live catalogue.
if (o.SeedSampleData?.Enabled) {
    o.SeedSampleData.Enabled = false;
    changes.push('SeedSampleData:Enabled -> false');
}

for (const key of ['SeedDemoUsers', 'SeedStudentAccounts']) {
    if (o[key]?.Enabled) {
        o[key].Enabled = false;
        changes.push(key + ':Enabled -> false');
    }
}

// The administrator already exists in the database, so nothing needs seeding - and with seeding
// off the guard stops demanding a password be kept in this file at all.
o.SeedAdmin = o.SeedAdmin || {};
if (o.SeedAdmin.Enabled !== false) {
    o.SeedAdmin.Enabled = false;
    changes.push('SeedAdmin:Enabled -> false (the database already holds the administrator)');
}
if (o.SeedAdmin.Password) {
    delete o.SeedAdmin.Password;
    changes.push('SeedAdmin:Password removed (not needed once seeding is off)');
}

// Email is unconfigured. The guard allows starting anyway, but only when told to explicitly.
o.EmailSettings = o.EmailSettings || {};
const emailIncomplete =
    !o.EmailSettings.SmtpServer || !o.EmailSettings.SenderEmail || !o.EmailSettings.Password;

if (emailIncomplete && o.EmailSettings.AllowMissing !== true) {
    o.EmailSettings.AllowMissing = true;
    changes.push('EmailSettings:AllowMissing -> true (password reset will not send until SMTP is set)');
}

// The console copy used during development holds 5000; a service that cannot bind its port exits.
if (listenUrl && o.Urls !== listenUrl) {
    o.Urls = listenUrl;
    changes.push('Urls -> ' + listenUrl);
}

// Turned off so a restart cannot re-run a destructive import.
for (const [section, flag] of [['Catalogue', 'FreshImport'], ['CredentialReset', 'Enabled']]) {
    if (o[section]?.[flag]) {
        o[section][flag] = false;
        changes.push(section + ':' + flag + ' -> false');
    }
}

fs.writeFileSync(path, JSON.stringify(o, null, 2));

if (changes.length === 0) {
    console.log('  already fit for Production; nothing changed.');
} else {
    changes.forEach(c => console.log('  ' + c));
}
