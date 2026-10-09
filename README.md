# Bouwplannen – backend

.NET 10 Web API (minimal APIs) die de projecten van de [Bouwplannen-app](https://github.com/hjeverts/bouwplannen_frontend) bewaart, zodat tablet, telefoon en pc dezelfde metingen zien.

## Ontwerpkeuzes

- **De metingen zijn van de app.** De server bewaart per project de naam, tijdstippen en de lijst `items` precies zoals de frontend ze stuurt. Nieuwe soorten metingen in de app vragen dus geen wijziging in de backend.
- **Opslag als JSON-bestand per project** in `data/projects/`. Voor één gebruiker is dat snel, transparant en makkelijk te back-uppen (bijvoorbeeld mee in je Nextcloud-back-up). Schrijven is atomair (tijdelijk bestand + rename) en loopt achter één slot. Alle opslag gaat via `IProjectRepository`, dus een EF Core/SQLite-variant kan later zonder de endpoints te raken.
- **Geen externe NuGet-pakketten**: alleen het .NET-framework zelf.
- **Synchronisatie zonder dataverlies**: elke wijziging verhoogt `version` (ook als `ETag`). Met `If-Match` schrijf je alleen als niemand anders tussendoor iets heeft veranderd; anders krijg je `409` met de huidige versie. Verwijderde projecten blijven als *tombstone* bestaan, zodat andere apparaten ook horen dat ze weg zijn.

## Endpoints

| Methode | Pad | Wat |
|---|---|---|
| `GET` | `/api/health` | Status, zonder sleutel |
| `GET` | `/api/projects` | Alle projecten + `serverTime` |
| `GET` | `/api/projects?since=<serverTime>` | Alleen wat daarna is gewijzigd, verwijderingen inbegrepen (`deleted: true`) |
| `GET` | `/api/projects/{id}` | Eén project, met `ETag` |
| `PUT` | `/api/projects/{id}` | Aanmaken (`201`) of bijwerken (`200`). Optioneel `If-Match: "3"`; `409` bij een oudere versie, `If-Match: *` = alleen als hij bestaat |
| `DELETE` | `/api/projects/{id}` | Verwijderen (`204`), optioneel met `If-Match` |

Body van `PUT`:

```json
{ "id": "…", "name": "Schuur", "created": "2026-10-09T09:00:00.000Z", "updated": "2026-10-09T10:00:00.000Z", "items": [ … ] }
```

Antwoorden bevatten daarnaast `version`, `serverModified` en `deleted`. Fouten in de invoer geven een `400` met per veld een melding (`ValidationProblemDetails`). Maximale grootte van een verzoek: 5 MB.

## Instellingen

| Sleutel | Omgevingsvariabele | Standaard |
|---|---|---|
| `Storage:DataDirectory` | `Storage__DataDirectory` | `data` |
| `Auth:ApiKey` | `Auth__ApiKey` | leeg = geen controle (alleen voor lokaal ontwikkelen) |
| `Cors:AllowedOrigins` | `Cors__AllowedOrigins__0`, `__1`, … | in Development: `http://localhost:4200` |

De app stuurt de sleutel mee in de header `X-Api-Key`. Zet de server achter je reverse proxy met HTTPS; de sleutel gaat anders leesbaar over het netwerk.

## Draaien

```bash
dotnet run --project src/Bouwplannen.Api          # http://localhost:5094
dotnet run --project tests/Bouwplannen.Api.Tests  # 16 tests
```

Met Docker:

```bash
echo "BOUWPLANNEN_API_KEY=$(openssl rand -hex 24)" > .env
echo "BOUWPLANNEN_FRONTEND_ORIGIN=https://bouwplannen.jouwdomein.nl" >> .env
docker compose up -d --build
```

De projecten staan dan in `./data/projects/`.

## Tests

`tests/Bouwplannen.Api.Tests` is een kleine testrunner zonder testframework-pakket. Hij start de echte API op een vrije poort met een eigen tijdelijke map en test via HTTP: sleutelcontrole, aanmaken en uitlezen, versieconflicten, validatie, `since`-synchronisatie met verwijderingen, herstart, een beschadigd bestand, parallelle schrijfacties, te grote verzoeken en CORS. Exitcode `1` bij een fout, dus bruikbaar in CI.

Omzetten naar xUnit is eenvoudig: elke test is een losse `async Task`-methode.
