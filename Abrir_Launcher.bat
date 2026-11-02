@echo off
title Yu-Gi-Oh! GX Tag Force 3 PT-BR - Launcher Co-op
cd /d "%~dp0"

where pythonw >nul 2>nul
if %errorlevel% equ 0 (
    start "" pythonw "%~dp0Launcher.pyw"
    exit /b
)

where python >nul 2>nul
if %errorlevel% equ 0 (
    start "" python "%~dp0Launcher.pyw"
    exit /b
)

echo [AVISO] Python nao encontrado neste PC. Instalando Python automaticamente via winget...
winget install -e --id Python.Python.3.12 --accept-package-agreements --accept-source-agreements
if %errorlevel% equ 0 (
    start "" "%LocalAppData%\Programs\Python\Python312\pythonw.exe" "%~dp0Launcher.pyw"
    exit /b
)

call "%~dp0JOGAR.bat"
exit
