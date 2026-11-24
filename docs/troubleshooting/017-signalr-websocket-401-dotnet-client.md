# 017 — WebSocket du hub refusé en 401 dans les tests alors que la négociation passait
Date : 2026-11-24 · Phase : 3 · Composant : Tests · Lien : PR `feat/p3-backend-consumers`

## Symptôme
Dans les tests du hub, la négociation réussissait mais la connexion WebSocket était refusée en 401.

## Cause racine
Non démontrée. Le chemin WebSocket des tests (TestServer, avec une fabrique WebSocket personnalisée) n'a pas transmis le jeton. L'API, elle, lit l'en-tête `Authorization` sur tous les chemins et accepte `access_token` en query string seulement pour `/hubs/notifications`. L'hypothèse (le `WebSocketClient` de TestServer ne reporte pas l'en-tête du client .NET) n'a pas été vérifiée en retirant la fabrique.

## Solution
La fabrique WebSocket des tests ajoute `access_token` à la requête, comme le fait le client JS du navigateur.

## Pour l'éviter
Tester le hub avec le jeton en query string, comme le frontend.
