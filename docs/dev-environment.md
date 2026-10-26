# Utvecklingsmiljö (Windows, installerad 2026-10-25, utan administratörsrättigheter)

Allt ligger i användarprofilen. Inget är installerat systemomfattande.

| Del | Version | Plats |
|---|---|---|
| .NET SDK | 10.0.402 | `C:\Users\ricka\.dotnet` (systemets .NET 7 och 9 ligger kvar i `Program Files`) |
| MAUI-workload | `maui-android` (10.0.110) | i `.dotnet`. iOS/Mac Catalyst/Windows-workloads är inte installerade (iOS kräver Mac) |
| JDK | Temurin 17.0.20 | `C:\Users\ricka\tools\jdk\jdk-17.0.20.1+1` |
| Android SDK | platform-tools, platforms;android-36, build-tools;36.0.0 | `C:\Users\ricka\tools\android-sdk` (licenserna accepterade 2026-10-25) |
| Node.js | 24.22.1 LTS (npm 11.19.1) | `C:\Users\ricka\tools\node\node-v24.22.1-win-x64` |

Användarens miljövariabler `JAVA_HOME`, `ANDROID_HOME`, `DOTNET_ROOT` och `PATH` är satta. Öppna ett nytt terminalfönster för att få dem.

Bygga Android: `dotnet build -f net10.0-android`. Om SDK:erna inte hittas: lägg till `-p:AndroidSdkDirectory=%ANDROID_HOME% -p:JavaSdkDirectory=%JAVA_HOME%`.
Kärnprojekten (`Core`, `Audio`, tester) målar net8.0 och byggs med valfri SDK. Byt till net10.0 när du vill (DR-01).
