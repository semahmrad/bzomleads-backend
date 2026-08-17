# Lead Radar SaaS - backend

Backend ASP.NET Core de l'application Lead Radar.

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

## Google AI

L'application utilise l'API officielle Google Generative Language. Chaque utilisateur
configure sa propre cle Google AI Studio lors de sa premiere connexion, puis peut la
modifier depuis **Mon compte**.

- modele par defaut : `gemma-3-27b-it` ;
- cle chiffree par ASP.NET Core Data Protection avant son stockage ;
- cle jamais renvoyee au navigateur apres son enregistrement ;
- validation de la cle par Google avant son activation.

## Notes

- Les quotas gratuits, modeles disponibles et limites sont geres par Google et peuvent evoluer.
- En production, configurez des origines CORS explicites et un stockage durable partage
  pour les cles Data Protection si plusieurs instances du backend sont utilisees.
