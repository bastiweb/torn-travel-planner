# Torn Stock Travel

Windows desktop dashboard for Torn travel stock, inventory, restock timing, wallet/vault funds, and next-trip recommendations.

## Security

Torn API keys are entered in the app and stored locally under the current Windows user profile using Windows DPAPI protection. API keys are not hardcoded in the source code and should not be committed to this repository.

## Development

```powershell
dotnet run --project .\TornStockTravel\TornStockTravel.csproj
```

The app fetches YATA, Torn, and DroqsDB API data on startup and then refreshes on the configured interval.

## Build a standalone Windows app

```powershell
dotnet publish .\TornStockTravel\TornStockTravel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The standalone app will be created under:

```text
TornStockTravel\bin\Release\net6.0-windows\win-x64\publish\
```
