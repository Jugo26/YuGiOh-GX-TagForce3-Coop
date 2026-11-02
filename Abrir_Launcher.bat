@echo off
title Yu-Gi-Oh! GX Tag Force 3 PT-BR - Launcher Co-op
cd /d "%~dp0"

REM 1. Se o executavel nativo Launcher_Coop.exe ja existir na pasta, inicia direto (nao precisa de Python!)
if exist "%~dp0Launcher_Coop.exe" (
    start "" "%~dp0Launcher_Coop.exe"
    exit /b
)

REM 2. Se nao existir ainda (ex: pasta antiga), baixa automaticamente do GitHub (apenas 48 KB!)
echo Baixando Launcher_Coop.exe atualizado do GitHub...
powershell -NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/Jugo26/YuGiOh-GX-TagForce3-Coop/main/Launcher_Coop.exe' -OutFile '%~dp0Launcher_Coop.exe' -UseBasicParsing"

if exist "%~dp0Launcher_Coop.exe" (
    start "" "%~dp0Launcher_Coop.exe"
    exit /b
)

REM 3. Se tiver o codigo-fonte C# local, compila com o compilador nativo do Windows (.NET Framework)
if exist "%~dp0Launcher_Coop.cs" (
    if exist "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" (
        "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:"%~dp0Launcher_Coop.exe" "%~dp0Launcher_Coop.cs"
        if exist "%~dp0Launcher_Coop.exe" (
            start "" "%~dp0Launcher_Coop.exe"
            exit /b
        )
    )
)

REM 4. Fallback final: Abre o jogo direto no PPSSPP incluso
call "%~dp0JOGAR.bat"
exit
