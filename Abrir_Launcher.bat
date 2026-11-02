@echo off
title Yu-Gi-Oh! GX Tag Force 3 PT-BR - Launcher Co-op v1.3.0
cd /d "%~dp0"

REM 1. Fecha qualquer instancia antiga do Launcher antes de abrir/atualizar
taskkill /f /im Launcher_Coop.exe >nul 2>&1

REM 2. Compila automaticamente o Launcher_Coop.cs mais recente baixado do GitHub (.NET Framework nativo do Windows)
if exist "%~dp0Launcher_Coop.cs" (
    if exist "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" (
        "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:"%~dp0Launcher_Coop.exe" "%~dp0Launcher_Coop.cs" >nul 2>&1
    ) else if exist "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" (
        "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" /nologo /target:winexe /out:"%~dp0Launcher_Coop.exe" "%~dp0Launcher_Coop.cs" >nul 2>&1
    )
)

REM 3. Inicia o executavel nativo Launcher_Coop.exe (Nao precisa de Python!)
if exist "%~dp0Launcher_Coop.exe" (
    start "" "%~dp0Launcher_Coop.exe"
    exit /b
)

REM 4. Fallback: Baixa direto do GitHub caso nao exista
powershell -NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/Jugo26/YuGiOh-GX-TagForce3-Coop/main/Launcher_Coop.exe' -OutFile '%~dp0Launcher_Coop.exe' -UseBasicParsing"
if exist "%~dp0Launcher_Coop.exe" (
    start "" "%~dp0Launcher_Coop.exe"
    exit /b
)

call "%~dp0JOGAR.bat"
exit
