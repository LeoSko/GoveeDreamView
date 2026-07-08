@echo off
rem Build the tray app. Output: bin\Release\net48\GoveeDreamView.exe (~19 KB).
dotnet build -c Release "%~dp0GoveeDreamView.csproj"
