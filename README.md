# Bouwplannen – backend

.NET 10 Web API (minimal APIs) die de projecten van de [Bouwplannen-app](https://github.com/hjeverts/bouwplannen_frontend) bewaart, zodat tablet, telefoon en pc dezelfde metingen zien.

## Ontwerpkeuzes

- **De metingen zijn van de app.** De server bewaart per project de naam, tijdstippen, groep, de instellingen (`settings`, bijv. de eenheid voor maten) en de lijst `items` precies zoals de frontend ze stuurt. Nieuwe soorten metingen in de app vragen dus geen wijziging in de backend.
- **Opslag als JSON-bestanden** in de datamap: `projects/<id>.json` per project en `accounts.json` met gebruikers en groepen. Snel, transparant en makkelijk te back-uppen. Schrijven is atomair (tijdelijk bestand + rename). Alle projectopslag gaat via `IProjectRepository`, dus een database-variant kan later zonder de endpoints te raken.
- **Geen externe NuGet-pakketten**: alleen het .NET-framework zelf (cookie-login, rate limiting en PBKDF2 zitten erin).
- **Synchroniseren zonder dataverlies**: elke wijziging verhoogt `version` (ook als `ETag`). Met `If-Match` schrijf je alleen als niemand anders tussendoor iets heeft veranderd; anders krijg je `409` met de huidige versie. Verwijderde projecten blijven als *tombstone* bestaan, zodat andere apparaten ook horen dat ze weg zijn.

## Gebruikers, groepen en toegang

- **Inloggen** met gebruikersnaam en wachtwoord geeft een cookie (`HttpOnly`, `SameSite=Strict`, `Secure` achter HTTPS) die 90 dagen geldig blijft zolang je de app gebruikt.
- **Wachtwoorden**: PBKDF2-SHA256, 600.000 iteraties, eigen salt per wachtwoord, minstens 10 tekens.
- **Raden tegengaan**: na 5 mislukte pogingen is een gebruikersnaam 30 seconden geblokkeerd, verdubbelend tot 15 minuten. Per IP-adres maximaal 10 pogingen per minuut. Een onbekende naam en een fout wachtwoord krijgen hetzelfde antwoord, in dezelfde tijd.
- **CSRF**: elke wijzigende aanvraag moet de header `X-Requested-With: bouwplannen` hebben.
- **Overal uitloggen**: bij een nieuw wachtwoord, *overal uitloggen* of blokkeren verandert de *security stamp* van de gebruiker; alle bestaande logins vervallen bij de volgende aanvraag.
- **Groepen**: elke gebruiker heeft een **privégroep** die niet te delen is. Iedereen kan groepen aanmaken en wordt daar beheerder van. Beheerders voegen leden toe op gebruikersnaam (eventueel ook als beheerder) en verwijderen ze; leden kunnen zelf vertrekken. Een groep met projecten kan niet weg.
- **Projecten** horen bij precies één groep. Alle leden mogen ze lezen en wijzigen. Projecten van groepen waar je geen lid van bent bestaan voor jou niet (`404`) en kunnen niet overschreven worden. Verplaatsen kan als je lid bent van beide groepen; leden van de oude groep krijgen het project in `removed` bij hun volgende synchronisatie.
- **Bestaande projecten** van vóór de accounts komen bij de eerste beheerder in de privégroep.

## Accounts beheren

Accounts maak je op de server aan; er is geen registratie via de site. In de Docker-opzet via `./user.sh` in de deploy-repo, anders rechtstreeks:

```bash
dotnet run --project src/Bouwplannen.Api -- users add hans --name "Hans Everts"   # eerste account = beheerder
dotnet run --project src/Bouwplannen.Api -- users list
dotnet run --project src/Bouwplannen.Api -- users passwd hans
dotnet run --project src/Bouwplannen.Api -- users disable piet
```

Het wachtwoord wordt gevraagd (zonder het te tonen), of komt uit `BOUWPLANNEN_PASSWORD`. Wijzigingen werken direct, ook als de API al draait.

## Endpoints

| Methode | Pad | Wat |
|---|---|---|
| `GET` | `/api/health` | Status, zonder login |
| `POST` | `/api/auth/login` | `{ username, password }` → cookie + gebruiker en groepen |
| `POST` | `/api/auth/logout` · `/api/auth/logout-all` | Uitloggen op dit apparaat · overal |
| `GET` | `/api/auth/me` | Ingelogde gebruiker met groepen |
| `POST` | `/api/auth/password` | `{ current, new }` |
| `GET` `POST` | `/api/groups` | Mijn groepen (met leden en aantal projecten) · nieuwe groep `{ name }` |
| `PUT` `DELETE` | `/api/groups/{id}` | Hernoemen · verwijderen (alleen leeg) |
| `POST` | `/api/groups/{id}/members` | `{ username, owner }` toevoegen of rol wijzigen |
| `DELETE` | `/api/groups/{id}/members/{userId}` | Lid verwijderen, of zelf vertrekken |
| `GET` | `/api/projects` | Projecten uit je groepen + `serverTime` + `groupIds` |
| `GET` | `/api/projects?since=<serverTime>` | Alleen wat daarna veranderde, met verwijderingen en `removed` (naar een andere groep verplaatst) |
| `GET` | `/api/projects/{id}` | Eén project, met `ETag` |
| `PUT` | `/api/projects/{id}` | Aanmaken (`201`) of bijwerken (`200`), `groupId` verplicht. Optioneel `If-Match: "3"` |
| `DELETE` | `/api/projects/{id}` | Verwijderen (`204`), optioneel met `If-Match` |

Fouten komen terug als ProblemDetails met een Nederlandse `title`. Maximale grootte van een verzoek: 5 MB.

## Instellingen

| Sleutel | Omgevingsvariabele | Standaard |
|---|---|---|
| `Storage:DataDirectory` | `Storage__DataDirectory` | `data` |

De API vertrouwt `X-Forwarded-Proto` en `X-Forwarded-For`: draai hem alleen achter de proxy (in de Docker-opzet is hij niet rechtstreeks bereikbaar).

## Draaien

```bash
dotnet run --project src/Bouwplannen.Api          # http://localhost:5094
dotnet run --project tests/Bouwplannen.Api.Tests  # 35 tests
```

Voor productie: zie de deploy-repo (Docker Compose, nginx, `update.sh`, `user.sh`).

## Tests

`tests/Bouwplannen.Api.Tests` is een kleine testrunner zonder testframework-pakket. Hij start de echte API op een vrije poort met een eigen tijdelijke map en test via HTTP met cookies, net als een browser: inloggen en blokkeren na foute pogingen, CSRF, cookie-instellingen, uitloggen (ook overal), wachtwoord wijzigen, groepen en rechten, verplaatsen tussen groepen, privéprojecten, migratie van oude projecten, versieconflicten, `since`-synchronisatie, herstart, een beschadigd bestand, parallelle schrijfacties, te grote verzoeken en het commandoregelgedeelte. Exitcode `1` bij een fout.
