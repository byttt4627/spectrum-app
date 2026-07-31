// STFT Web Worker — runs entirely off the main thread
self.onmessage = function(e) {
  const { rawData, fftSize, hop, useBins, numFrames } = e.data

  // Hann window (pre-allocated)
  const win = new Float32Array(fftSize)
  for (let i = 0; i < fftSize; i++)
    win[i] = 0.5 * (1 - Math.cos(2 * Math.PI * i / (fftSize - 1)))

  // Reusable FFT buffers (no GC pressure per frame)
  const re = new Float32Array(fftSize)
  const im = new Float32Array(fftSize)

  const specData = new Float32Array(numFrames * useBins)
  let maxPow = -Infinity

  const CHUNK = 150
  for (let fs = 0; fs < numFrames; fs += CHUNK) {
    const fe = Math.min(fs + CHUNK, numFrames)
    for (let f = fs; f < fe; f++) {
      const off = f * hop
      for (let i = 0; i < fftSize; i++) {
        re[i] = (off + i < rawData.length ? rawData[off + i] : 0) * win[i]
        im[i] = 0
      }
      fft(re, im, fftSize)
      const base = f * useBins
      for (let b = 0; b < useBins; b++) {
        const p = 10 * Math.log10(re[b]*re[b] + im[b]*im[b] + 1e-10)
        specData[base + b] = p
        if (p > maxPow) maxPow = p
      }
    }
    self.postMessage({ type: 'progress', pct: (fs / numFrames * 100 + 0.5) | 0 })
  }

  // Transfer specData zero-copy back to main thread
  self.postMessage({ type: 'done', specData, maxPow }, [specData.buffer])
}

function fft(re, im, n) {
  // bit-reversal
  for (let i = 1, j = 0; i < n; i++) {
    let bit = n >> 1
    for (; j & bit; bit >>= 1) j ^= bit
    j ^= bit
    if (i < j) {
      let t = re[i]; re[i] = re[j]; re[j] = t
      t = im[i]; im[i] = im[j]; im[j] = t
    }
  }
  // butterfly
  for (let len = 2; len <= n; len <<= 1) {
    const ang = -2 * Math.PI / len
    const wR = Math.cos(ang), wI = Math.sin(ang)
    for (let i = 0; i < n; i += len) {
      let cR = 1, cI = 0
      const half = len >> 1
      for (let j = 0; j < half; j++) {
        const uR = re[i+j],    uI = im[i+j]
        const vR = re[i+j+half]*cR - im[i+j+half]*cI
        const vI = re[i+j+half]*cI + im[i+j+half]*cR
        re[i+j]      = uR + vR;  im[i+j]      = uI + vI
        re[i+j+half] = uR - vR;  im[i+j+half] = uI - vI
        const nR = cR*wR - cI*wI; cI = cR*wI + cI*wR; cR = nR
      }
    }
  }
}
