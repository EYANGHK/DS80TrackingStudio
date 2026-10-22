@echo off
setlocal
set "NATIVE_ROOT=%~dp0"
if not defined XVSDK_ROOT set "XVSDK_ROOT=C:\Program Files\xvsdk"
if not exist "%XVSDK_ROOT%\include\xvsdk\xv-sdk.h" (
    echo XVSDK headers not found. Set XVSDK_ROOT.
    exit /b 1
)
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "DS80_VS=%%i"
if not defined DS80_VS (
    echo MSVC x64 build tools not found.
    exit /b 1
)
call "%DS80_VS%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
if not exist "%NATIVE_ROOT%bin" mkdir "%NATIVE_ROOT%bin"
cl /nologo /utf-8 /std:c++17 /EHsc /O2 /MD /D_DISABLE_CONSTEXPR_MUTEX_CONSTRUCTOR /LD /I"%XVSDK_ROOT%\include\xvsdk" "%NATIVE_ROOT%DS80NativeBridge.cpp" /Fo"%NATIVE_ROOT%bin\DS80NativeBridge.obj" /link /LIBPATH:"%XVSDK_ROOT%\lib" xvsdk.lib /OUT:"%NATIVE_ROOT%bin\DS80NativeBridge.dll" /IMPLIB:"%NATIVE_ROOT%bin\DS80NativeBridge.lib"
exit /b %errorlevel%
