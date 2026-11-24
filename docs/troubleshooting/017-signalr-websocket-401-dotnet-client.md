# 017 — WebSocket du hub refusé en 401 alors que la négociation passait (tests)
Date : 2026-11-24 · Phase : 3 · Composant : Tests · Lien : PR `feat/p3-backend-consumers`

## Symptôme
Dans les tests du hub, la négociation réussissait mais la connexion WebSocket était refusée en 401.

## Cause racine
Le client .NET envoie le jeton dans l'en-tête `Authorization`, que l'API n'accepte pas sur le WebSocket ; le navigateur, lui, passe `access_token` dans l'URL.

## Solution
La fabrique WebSocket des tests ajoute `access_token` à la requête, comme le client JS.

## Pour l'éviter
Tester le hub avec le jeton en requête, comme le fait le frontend.
