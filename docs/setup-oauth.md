# Configuration OAuth (Google et GitHub)

Action manuelle : ces identifiants se créent chez les fournisseurs et ne sont jamais commités. Les valeurs vont dans `.env` (ignoré par git) ou dans les `user-secrets` en développement local.

## URI de redirection à déclarer

| Environnement | Google | GitHub |
| --- | --- | --- |
| Docker Compose / dev | `http://localhost:8080/signin-google` | `http://localhost:8080/signin-github` |
| Démo déployée | `https://<domaine>/signin-google` | `https://<domaine>/signin-github` |

Les deux URI passent par nginx (même site que le front), ce qui permet au cookie `SameSite=Lax` de fonctionner.

## Google

1. Console Google Cloud, créer un projet, puis « API et services » → « Écran de consentement OAuth » (externe, scopes `openid`, `email`, `profile`).
2. « Identifiants » → « Créer des identifiants » → « ID client OAuth », type « Application Web ».
3. Ajouter l'URI de redirection ci-dessus, puis copier l'ID client et le secret.

## GitHub

1. Settings → Developer settings → OAuth Apps → New OAuth App.
2. « Authorization callback URL » : l'URI `signin-github` ci-dessus.
3. Générer un client secret, puis copier l'ID client et le secret. Le scope demandé est `user:email` ; seul l'e-mail principal vérifié est accepté.

## Variables d'environnement

```
Authentication__Google__ClientId=...
Authentication__Google__ClientSecret=...
Authentication__GitHub__ClientId=...
Authentication__GitHub__ClientSecret=...
```

Sans ces valeurs, l'API démarre et la connexion de démo fonctionne (si `Demo:Enabled=true`) ; seule la connexion Google ou GitHub répond une erreur claire.
