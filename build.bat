@echo off
rem launch VS Command Prompt
call "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\VC\Auxiliary\Build\vcvarsall.bat" amd64_x86

echo WindowsSDKVersion: %WindowsSDKVersion%

pushd %~dp0\Code
FBuild.exe All-x64-Release -clean
popd

pause