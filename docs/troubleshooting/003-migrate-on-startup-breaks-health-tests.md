# 003 — Les tests de santé échouent après l'ajout des migrations au démarrage
Date : 2026-11-23 · Phase : 1 · Composant : API · Lien : PR `feat/p1-backend-identity`

## Symptôme
Après l'ajout de la migration EF Core au démarrage de l'API, `HealthTests` ne démarre plus l'hôte : la base est volontairement injoignable dans ces tests.

## Contexte
`WebApplicationFactory<Program>` s'exécute en environnement `Development`, où l'API applique les migrations au démarrage (SPEC section 11).

## Cause racine
La migration au démarrage tente de se connecter à la base de test inexistante et fait échouer le démarrage avant les requêtes.

## Solution
Option `Database:MigrateOnStartup`, par défaut active en Development ou en mode démo, et forcée à `false` dans `HealthTests` via `UseSetting`. Aucune assertion du test n'a été modifiée.

## Pour l'éviter
Les tests qui ont besoin de la base utilisent Testcontainers (une base par test) ; ceux qui n'en ont pas désactivent explicitement la migration.
