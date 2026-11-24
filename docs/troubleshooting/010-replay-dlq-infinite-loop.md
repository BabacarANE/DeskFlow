# 010 — `replay-dlq` boucle sans fin sur un message qui échoue toujours
Date : 2026-11-23 · Phase : 3 · Composant : Worker · Lien : PR `feat/p3-backend-messaging`

## Symptôme
Avec un consommateur actif et un message qui continue d'échouer, `replay-dlq` republiait le même message indéfiniment.

## Contexte
Rejeu par lecture successive (`BasicGet`) de `desk.dlq` ; le message rejoué, une fois ses reprises épuisées, retourne en DLQ.

## Cause racine
Le message dead-letteré de nouveau revenait en queue de DLQ, et la boucle de lecture le reprenait.

## Solution
Fixer au départ le nombre de messages à lire d'après la profondeur de la DLQ (`QueueDeclare` renvoie `MessageCount`).

## Pour l'éviter
Test `A_poisoned_message_is_replayed_to_its_origin_and_abandoned_again`.
