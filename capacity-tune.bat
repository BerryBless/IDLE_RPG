@echo off
setlocal
rem ============================================================================
rem  capacity-tune.bat  (RUN AS ADMINISTRATOR - one time)
rem    Expands the Windows ephemeral (dynamic) TCP port range so a single box
rem    can sustain hundreds of thousands of outbound loopback connections, and
rem    so the server listener ports (7777+) sit BELOW the ephemeral range and
rem    never collide with an ephemeral allocation (WSAEACCES 10013 on bind).
rem
rem  Why: this machine's default dynamic range starts near 1024, which overlaps
rem  the server ports. Moving it to start=10000 frees 1024..9999 for listeners
rem  and gives ~55000 ephemeral ports for the load generator.
rem
rem  Revert with:  netsh int ipv4 set dynamicport tcp start=49152 num=16384
rem  ASCII only (repo rule, see plan/docker_compose_0719.md 7-2).
rem ============================================================================

net session >nul 2>&1
if errorlevel 1 (
    echo [ERROR] This script must be run as Administrator.
    echo         Right-click capacity-tune.bat -^> "Run as administrator".
    pause
    exit /b 2
)

echo Current TCP dynamic port range:
netsh int ipv4 show dynamicport tcp
echo.
echo Current TCP excluded port ranges (bind here fails with WSAEACCES):
netsh int ipv4 show excludedportrange tcp
echo.

echo Setting dynamic port range to start=10000 num=55000 ...
netsh int ipv4 set dynamicport tcp start=10000 num=55000
if errorlevel 1 (
    echo [ERROR] Failed to set dynamic port range.
    pause
    exit /b 1
)

echo.
echo New TCP dynamic port range:
netsh int ipv4 show dynamicport tcp
echo.
echo Done. Server listener ports 7777.. now sit below the ephemeral range.
echo Optional (churn-heavy reruns): shorten TIME_WAIT via registry
echo   HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters TcpTimedWaitDelay=30
echo (Not set by this script.) Revert range: netsh int ipv4 set dynamicport tcp start=49152 num=16384
pause
