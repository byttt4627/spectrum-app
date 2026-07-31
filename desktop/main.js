const { app, BrowserWindow, ipcMain, dialog } = require('electron')
const path = require('path')

let mainWin = null

function createWindow() {
  mainWin = new BrowserWindow({
    width: 1280, height: 840,
    minWidth: 960, minHeight: 600,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false
    },
    title: '音频频谱图工具',
    backgroundColor: '#1a1a2e',
    show: false
  })
  mainWin.once('ready-to-show', () => mainWin.show())
  mainWin.loadFile('index.html')
  mainWin.setMenuBarVisibility(false)
}

app.whenReady().then(createWindow)
app.on('window-all-closed', () => app.quit())

ipcMain.handle('open-file', async () => {
  const { canceled, filePaths } = await dialog.showOpenDialog(mainWin, {
    title: '选择音频文件',
    properties: ['openFile'],
    filters: [
      { name: '音频文件', extensions: ['mp3','wav','ogg','flac','m4a','aac','opus','wma','aiff'] },
      { name: '所有文件', extensions: ['*'] }
    ]
  })
  return canceled ? null : filePaths[0]
})

ipcMain.on('set-title', (event, title) => {
  BrowserWindow.fromWebContents(event.sender)?.setTitle(title)
})
