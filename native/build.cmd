@echo off
rem Builds ext4core.dll and e4cli.exe with MSVC + the CMake/Ninja bundled with Visual Studio.
setlocal
set VSROOT=C:\Program Files\Microsoft Visual Studio\2022\Community
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath 2^>nul`) do set VSROOT=%%i
call "%VSROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set PATH=%VSROOT%\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin;%VSROOT%\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja;%PATH%
rem e2fsprogs is a git submodule; our MSVC portability fixes live in patches\.
set E2FS=%~dp0..\third_party\e2fsprogs
if not exist "%E2FS%\lib\ext2fs\ext2fs.h" git -C "%~dp0.." submodule update --init --depth 1 || exit /b 1
git -C "%E2FS%" apply --reverse --check "%~dp0patches\e2fsprogs-msvc.patch" 2>nul || git -C "%E2FS%" apply "%~dp0patches\e2fsprogs-msvc.patch" || exit /b 1
set CFG=%1
if "%CFG%"=="" set CFG=Release
cmake -S "%~dp0." -B "%~dp0build\%CFG%" -G Ninja -DCMAKE_BUILD_TYPE=%CFG% || exit /b 1
cmake --build "%~dp0build\%CFG%" || exit /b 1
