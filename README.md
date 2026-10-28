# IPTV Desktop — Avalonia

.NET 10 Avalonia desktop IPTV project. Includes Core, Desktop and tests.

## Build on Windows

Install .NET SDK 10.0.401 (see global.json), then run:

    dotnet restore Iptv.sln
    dotnet build Iptv.sln -c Release
    dotnet test Iptv.sln -c Release --no-build
    dotnet publish src/Iptv.Desktop/Iptv.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false

The Windows startup issue is still under investigation. Hardware playback and interactive startup require Windows machine verification. See THIRD-PARTY-NOTICES.md and LICENSES for dependency licensing.
