@echo off
setlocal
pushd "%~dp0"

dotnet run --project "src\PodcastVideoEditor.Ui\PodcastVideoEditor.Ui.csproj"

popd
endlocal
