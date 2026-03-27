@echo off

echo Building project...
dotnet build

if %errorlevel% neq 0 (
    echo Build failed. Fix errors before running.
    pause
    exit /b
)

echo Running project (no rebuild)...
dotnet run --no-build

pause