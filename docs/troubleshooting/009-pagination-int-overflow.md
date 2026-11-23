# 009 — `GET /tickets?page=2147483647` répond 500 (débordement d'offset)
Date : 2026-11-23 · Phase : 2 · Composant : API · Lien : PR `feat/p2-backend-tickets`

## Symptôme
Une très grande valeur de `page` fait répondre 500 ; PostgreSQL refuse un `OFFSET` négatif.

## Contexte
Aide de pagination partagée par les endpoints de la phase 1 et de la phase 2. Détecté par l'audit, non par un test.

## Cause racine
`Skip((page - 1) * pageSize)` calculé en `int` déborde et devient négatif.

## Solution
Calculer l'offset en `long` ; si l'offset atteint ou dépasse le total, répondre 200 avec `items` vide, sans requête de page.

## Pour l'éviter
Test `HugePageNumber_GivesAnEmptyPage_NotA500_OnTicketsAndUsers` sur les tickets, les utilisateurs et les invitations.
