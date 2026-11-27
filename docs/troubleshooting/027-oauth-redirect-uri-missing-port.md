# 027 — L'URI de redirection OAuth perdait le port (`http://localhost/signin-google`)
Date : 2026-11-27 · Phase : 5 · Composant : Docker · Lien : branche `fix/p5-oauth-redirect-port`

## Symptôme
Sur la pile Docker Compose (application sur le port 8080), `GET /api/v1/auth/external/google` redirigeait vers Google avec `redirect_uri=http://localhost/signin-google`, sans `:8080` (idem pour GitHub). L'URI déclarée chez le fournisseur contient le port : la connexion ne pouvait pas aboutir.

## Contexte
Trouvé avant la première création des identifiants OAuth, en lisant `docker/nginx.conf` puis en reproduisant avec des identifiants factices. Les tests d'intégration appellent l'API sans nginx et ne pouvaient pas le voir.

## Cause racine
nginx transmettait `proxy_set_header Host $host`. La variable `$host` ne contient pas le port ; ASP.NET construit l'URI de redirection à partir de l'en-tête `Host` reçu.

## Solution
`proxy_set_header Host $http_host;` (l'en-tête tel qu'envoyé par le navigateur), pour `/api`, `/health`, `/signin-*` et `/hubs/`.

## Pour l'éviter
Test E2E `web/e2e/oauth-redirect.spec.ts` à travers nginx ; la CI renseigne des identifiants factices pour enregistrer les deux fournisseurs.
