# Torn Stock Travel

Windows desktop dashboard for Torn travel stock, inventory, restock timing, wallet/vault funds, and next-trip recommendations.

## Security

Torn API keys are entered in the app and stored locally under the current Windows user profile using Windows DPAPI protection. API keys are not hardcoded in the source code and should not be committed to this repository.

## Development

```powershell
dotnet run --project .\TornStockTravel\TornStockTravel.csproj
```

The app fetches YATA, Torn, and DroqsDB API data on startup and then refreshes on the configured interval.

## Discord webhook alerts

Discord webhook alerts are optional. They use the same alert events as the local Windows reminders.

1. In Discord, open your server settings.
2. Go to `Integrations` > `Webhooks`.
3. Create a new webhook, choose the target channel, and copy the webhook URL.
4. In Torn Stock Travel, open `Settings` > `Alerts`.
5. Paste the webhook URL into `Discord webhook URL`.
6. Enable `Send Discord webhook`.
7. Enable the alert types you want, such as departure, landing, nerve, or restock alerts.
8. Optionally adjust `Discord message template`.
9. Click `Save`.

Use `Test Discord` to send a one-off test message to the webhook URL currently entered in the field. This test does not require `Send Discord webhook` to be enabled. Use `Test Windows` to check the local Windows notification path.

The Discord message template supports these placeholders:

- `{title}`: alert title
- `{message}`: alert body
- `{app}`: app name
- `{time}`: local PC time when the message is sent
- `{newline}`: line break

Default template:

```text
**{title}**
{message}
```

Webhook URLs are stored locally with Windows DPAPI protection, just like the Torn API key. Do not commit webhook URLs to GitHub.

## Build a standalone Windows app

```powershell
dotnet publish .\TornStockTravel\TornStockTravel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The standalone app will be created under:

```text
TornStockTravel\bin\Release\net6.0-windows\win-x64\publish\
```
