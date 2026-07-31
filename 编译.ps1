# 查找 csc.exe
$csc = $null
$frameworks = @(
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "C:\Windows\Microsoft.NET\Framework64\v3.5\csc.exe",
    "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe",
    "C:\Windows\Microsoft.NET\Framework\v3.5\csc.exe"
)
foreach ($p in $frameworks) {
    if (Test-Path $p) { $csc = $p; break }
}

if (-not $csc) {
    Write-Host "未找到 csc.exe，请安装 .NET Framework" -ForegroundColor Red
    pause; exit 1
}

Write-Host "编译器: $csc"
Write-Host "正在编译..."

$src    = "D:\音频频谱图工具\SpectrumApp.cs"
$output = "D:\音频频谱图工具\频谱图工具.exe"

& $csc /target:winexe /out:$output `
    /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll `
    /reference:System.dll `
    $src

if ($LASTEXITCODE -eq 0) {
    Write-Host "编译成功！" -ForegroundColor Green
} else {
    Write-Host "编译失败，请检查上方错误" -ForegroundColor Red
}
Read-Host "按 Enter 退出"
