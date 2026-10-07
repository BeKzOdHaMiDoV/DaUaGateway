# Установка шлюза как службы Windows. Запускать PowerShell ОТ ИМЕНИ АДМИНИСТРАТОРА.
# По умолчанию берёт DaUaGateway.exe из папки на уровень выше (bin\Release\net48).
param(
    [string]$ExePath = (Join-Path $PSScriptRoot "..\DaUaGateway.exe"),
    [int]$Port = 4840
)

$ErrorActionPreference = "Stop"
$name = "DaUaGateway"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Запусти PowerShell от имени администратора!" -ForegroundColor Red; exit 1
}

$ExePath = (Resolve-Path $ExePath).Path

if (Get-Service $name -ErrorAction SilentlyContinue) {
    Write-Host "Служба $name уже установлена. Сначала запусти uninstall-service.ps1" -ForegroundColor Yellow; exit 1
}

# New-Service сам корректно передаёт путь с пробелами (кавычки — защита от unquoted service path)
New-Service -Name $name -BinaryPathName ('"' + $ExePath + '"') `
    -DisplayName "OPC DA-UA Gateway" -StartupType Automatic `
    -Description "Шлюз OPC DA -> OPC UA (автопоиск DA-серверов)" | Out-Null

# отложенный автозапуск (после старта сети и COM) + перезапуск при сбое через 1 минуту
sc.exe config $name start= delayed-auto | Out-Null
sc.exe failure $name reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

$rule = "OPC UA DaUaGateway ($Port)"
if (-not (Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $rule -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow | Out-Null
}

Start-Service $name
Get-Service $name | Format-Table Name, Status, StartType
Write-Host ("Готово. Логи: " + (Join-Path (Split-Path $ExePath) "logs")) -ForegroundColor Green
