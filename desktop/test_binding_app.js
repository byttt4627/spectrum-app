const fs = require('fs');
const Module = require('module');

let log = [];
function logMsg(m) { log.push(m); }

logMsg('process.type=' + process.type);
logMsg('versions.electron=' + process.versions.electron);
logMsg('builtinModules has electron: ' + Module.builtinModules.includes('electron'));

// Try _linkedBinding for all possible electron bindings
const bindingNames = [
  'electron_browser_app',
  'electron_browser_window',
  'electron_browser_ipc',
  'electron_browser_dialog',
  'electron_browser_menu',
  'electron_browser_screen',
  'electron_browser_webContents',
  'electron_browser_autoUpdater',
  'electron_browser_protocol',
  'electron_browser_powerSaveBlocker',
  'electron_common_clipboard',
  'electron_common_shell',
  'electron_renderer_ipc',
  'electron_renderer_webFrame',
  'electron_browser_session',
  'electron_browser_downloadItem',
  'electron_browser_notification',
  'electron_browser_tray',
];

for (const name of bindingNames) {
  try {
    const b = process._linkedBinding(name);
    const keys = Object.keys(b).slice(0,5).join(',');
    logMsg('OK ' + name + ' -> [' + keys + ']');
  } catch(e) {
    logMsg('NO ' + name + ': ' + e.message.slice(0,50));
  }
}

logMsg('=== DONE ===');
fs.writeFileSync('bindings_test.log', log.join('\n'));
process.exit(0);
