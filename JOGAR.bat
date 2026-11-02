@echo off
title Yu-Gi-Oh! GX Tag Force 3 PT-BR
cd /d "%~dp0"

set "PPSSPP=%~dp0PPSSPP\PPSSPPWindows64.exe"
if not exist "%PPSSPP%" (
    set "PPSSPP=C:\Program Files\PPSSPP\PPSSPPWindows64.exe"
)

set "GAME=%~dp0Yu-Gi-Oh! GX Tag Force 3 PTBR.cso"

if not exist "%PPSSPP%" (
    echo [ERRO] PPSSPP nao encontrado!
    pause
    exit /b
)

if not exist "%GAME%" (
    echo [ERRO] Jogo nao encontrado em: %GAME%
    pause
    exit /b
)

echo ========================================================
echo   Iniciando Yu-Gi-Oh! GX Tag Force 3 PT-BR (PPSSPP)
echo ========================================================
start "" "%PPSSPP%" "%GAME%"
exit
