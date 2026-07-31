const { contextBridge, ipcRenderer } = require('electron')
const fs   = require('fs')
const path = require('path')

contextBridge.exposeInMainWorld('electronAPI', {
  openFile:   ()         => ipcRenderer.invoke('open-file'),
  setTitle:   (t)        => ipcRenderer.send('set-title', t),
  getFileName:(p)        => path.basename(p),
  getFileSize:(p)        => fs.statSync(p).size,
  readFile:   (filePath) => {
    const buf = fs.readFileSync(filePath)
    const ab  = new ArrayBuffer(buf.length)
    new Uint8Array(ab).set(buf)
    return ab
  }
})
