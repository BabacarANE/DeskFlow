# 012 — Erreur `404 no exchange 'desk.dlx'` juste après le démarrage de l'hôte
Date : 2026-11-23 · Phase : 3 · Composant : Worker · Lien : PR `feat/p3-backend-messaging`

## Symptôme
Dans les tests, `404 no exchange 'desk.dlx'` ou une DLQ introuvable apparaissait juste après le démarrage d'un hôte Worker sans consommateur.

## Contexte
La topologie RabbitMQ est déclarée en arrière-plan au démarrage, pour qu'un broker absent ne bloque pas l'API.

## Cause racine
Une publication ou une lecture pouvait arriver avant la fin de la déclaration des exchanges et des files.

## Solution
Le dispatcher d'outbox attend la topologie avant de publier ; l'aide de test attend `EnsureDeclaredAsync`.

## Pour l'éviter
Toute nouvelle publication ou lecture de file passe par l'attente de la topologie.
