**Download `NoFocusSteal.exe` below and run it.** No installer and no runtime to install: it uses .NET Framework 4.8, which is already part of Windows 10 and 11. It sits in the tray; left-click the icon for the focus log.

This build was compiled by GitHub Actions from the tagged source; the `.sha256` file lets you verify it.

Windows SmartScreen may say "Windows protected your PC" because the exe isn't code-signed yet. Click **More info → Run anyway**, or build it yourself with `dotnet build src/NoFocusSteal -c Release`.
