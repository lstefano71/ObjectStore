@echo off
REM Build script for C benchmarks
REM Requires Visual Studio developer command prompt (vcvars64.bat)

echo Building ObjectStore benchmark...
cl /O2 /W3 /nologo bench_objstore.c /I"..\..\src\ObjectStore.Native" /Fe:bench_objstore.exe
if errorlevel 1 (
    echo FAILED to build bench_objstore.exe
    exit /b 1
)

echo Building SQLite benchmark...
cl /O2 /W3 /nologo /DSQLITE_THREADSAFE=0 /DSQLITE_OMIT_LOAD_EXTENSION bench_sqlite.c sqlite3.c /Fe:bench_sqlite.exe
if errorlevel 1 (
    echo FAILED to build bench_sqlite.exe
    exit /b 1
)

echo.
echo Build complete. Run:
echo   bench_objstore.exe ^<path_to_ObjectStore.Native.dll^> [directory]
echo   bench_sqlite.exe [directory]
