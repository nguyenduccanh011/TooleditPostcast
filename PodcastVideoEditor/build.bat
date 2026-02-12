@echo off
setlocal
pushd "%~dp0"

dotnet build "src\PodcastVideoEditor.sln"

popd
endlocal
