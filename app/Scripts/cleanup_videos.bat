@echo off
powershell -NoLogo -ExecutionPolicy Bypass -File "%~dp0cleanup_videos.ps1"
exit /b %ERRORLEVEL%
