@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Register-DevProtocol.ps1" %*
set "MYSTRAL_PROTOCOL_EXIT=%ERRORLEVEL%"
if not "%MYSTRAL_PROTOCOL_EXIT%"=="0" (
    echo.
    echo Mystral development protocol registration failed.
)
exit /b %MYSTRAL_PROTOCOL_EXIT%
