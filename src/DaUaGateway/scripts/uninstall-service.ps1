# Удаление службы шлюза. Запускать PowerShell ОТ ИМЕНИ АДМИНИСТРАТОРА.
$name = "DaUaGateway"
if (Get-Service $name -ErrorAction SilentlyContinue) {
    Stop-Service $name -Force -ErrorAction SilentlyContinue
    sc.exe delete $name | Out-Null
    Write-Host "Служба $name удалена" -ForegroundColor Green
} else {
    Write-Host "Служба $name не установлена" -ForegroundColor Yellow
}
