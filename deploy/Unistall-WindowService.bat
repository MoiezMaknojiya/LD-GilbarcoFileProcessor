@echo off
REM ========================================
REM LdFileProcessor Service Removal Script
REM ========================================
REM IMPORTANT: Run this file as Administrator
REM ========================================

echo.
echo ========================================
echo LdFileProcessor Service Remover
echo ========================================
echo.

REM Check if running as Administrator
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: This script must be run as Administrator!
    echo.
    echo Please right-click this file and choose:
    echo "Run as administrator"
    echo.
    pause
    exit /b 1
)

echo Checking if service exists...
sc query LdFileProcessor >nul 2>&1
if %errorLevel% neq 0 (
    echo Service "LdFileProcessor" does not exist.
    echo Nothing to remove.
    echo.
    pause
    exit /b 0
)

echo.
echo Stopping service (if running)...
sc stop LdFileProcessor >nul 2>&1
timeout /t 5 /nobreak >nul

echo Deleting service...
sc delete LdFileProcessor
if %errorLevel% neq 0 (
    echo ERROR: Failed to delete service!
    echo You may need to reboot and try again.
    echo.
    pause
    exit /b 1
)

echo.
echo ========================================
echo SUCCESS!
echo LdFileProcessor service has been removed.
echo ========================================
echo.

pause
