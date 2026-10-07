@echo off
REM ========================================
REM LdFileProcessor Service Installation
REM ========================================
REM Installs the LdFileProcessor Windows Service,
REM configures it to start automatically on Windows startup
REM and to restart automatically if it ever crashes.
REM (Service will NOT be started immediately; the LdPosService
REM  desktop app starts it when the BOOutBox folder is selected.)
REM
REM IMPORTANT: Run this file as Administrator
REM ========================================

echo.
echo ========================================
echo LdFileProcessor Service Installer
echo ========================================
echo.

REM Check if running as Administrator
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: This script must be run as Administrator!
    echo.
    echo Please:
    echo 1. Right-click this file
    echo 2. Select "Run as administrator"
    echo.
    pause
    exit /b 1
)

echo [1/5] Checking if service already exists...
sc query LdFileProcessor >nul 2>&1
if %errorLevel% equ 0 (
    echo Service already exists. Removing old service...
    sc stop LdFileProcessor >nul 2>&1
    timeout /t 3 /nobreak >nul
    sc delete LdFileProcessor >nul 2>&1
    timeout /t 2 /nobreak >nul
    echo Old service removed.
) else (
    echo Service does not exist. Proceeding with installation...
)

echo.
echo [2/5] Installing LdFileProcessor service...
sc create LdFileProcessor binPath= "C:\ProgramData\LotteryDisplayPOS\LdFileProcessor\LdFileProcessor.exe" start= auto
if %errorLevel% neq 0 (
    echo ERROR: Failed to create service!
    pause
    exit /b 1
)
echo Service installed successfully.

echo.
echo [3/5] Configuring service to run as Network Service account...
sc config LdFileProcessor obj= "NT AUTHORITY\NetworkService" password= ""
if %errorLevel% neq 0 (
    echo ERROR: Failed to configure service account!
    pause
    exit /b 1
)
echo Service account configured successfully.

echo.
echo [4/5] Configuring service to start automatically on Windows startup...
sc config LdFileProcessor start= auto >nul
if %errorLevel% neq 0 (
    echo ERROR: Failed to configure auto-start!
    pause
    exit /b 1
)
echo Auto-start configured successfully.

echo.
echo [5/5] Configuring automatic restart on crash...
REM If the service process ever dies, Windows restarts it after 5s, 10s, then 30s.
REM The failure counter resets after 24 hours (86400 seconds).
sc failure LdFileProcessor reset= 86400 actions= restart/5000/restart/10000/restart/30000
if %errorLevel% neq 0 (
    echo WARNING: Failed to configure crash recovery. Service will still work,
    echo but will not auto-restart if it crashes.
) else (
    REM Also apply the recovery when the service stops with an error code without crashing
    sc failureflag LdFileProcessor 1 >nul
    echo Crash recovery configured: restarts after 5s, 10s, then 30s.
)

echo.
echo ========================================
echo SUCCESS! Service installed.
echo Service Account: NT AUTHORITY\NetworkService
echo Startup type: Automatic
echo Crash recovery: Enabled
echo ========================================
echo.
echo IMPORTANT: Ensure the Network Service account has
echo appropriate permissions to access required files/folders
echo.
echo The service is NOT started yet. Open LdPosService.exe,
echo log in and select the BOOutBox folder to start it.
echo.
pause
