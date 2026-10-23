@echo off
rem Use this if Bluetooth is blocked when index.html is opened directly.
rem Serves this folder only to this PC (127.0.0.1) and opens it in Edge.
cd /d "%~dp0"
start "" msedge "http://127.0.0.1:8765/index.html"
echo Close this window to stop the local page server.
python -m http.server 8765 --bind 127.0.0.1
pause
