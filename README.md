# Exploit Gemeni .NET

Backend ASP.NET Core pour proxy Gemini.

## Endpoints

- `GET /health`
- `GET /api/ask?prompt=hello`
- `POST /api/ask`

## Run local

```bash
dotnet restore
dotnet run
```

Le serveur demarre par defaut sur `http://localhost:5055`.

## Gemini config

Le backend attend un fichier local non versionne:

```text
Config/gemini_request.json
```

Le repo contient seulement:

```text
Config/gemini_request.example.json
```

Copie ce fichier puis remplace les placeholders avec une vraie capture Gemini.

## Notes

- Le vrai `gemini_request.json` est ignore par git pour eviter de publier des cookies ou headers sensibles.
- CORS est ouvert pour faciliter les appels depuis le frontend Angular local.
