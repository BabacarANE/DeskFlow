# 007 — L'ordre des événements créés dans une même opération n'est pas celui de l'ajout
Date : 2026-11-23 · Phase : 2 · Composant : API · Lien : PR `feat/p2-backend-tickets`

## Symptôme
L'historique d'un ticket affichait `Created, PriorityChanged, CategoryChanged, StatusChanged, AssigneeChanged` au lieu de l'ordre d'ajout.

## Contexte
Plusieurs `TicketEvent` insérés dans un même `SaveChanges`, avec le même instant `At`.

## Cause racine
EF Core ne garantit pas l'ordre des `INSERT` d'une même table dans un lot, donc l'identité `Seq` ne reflète pas l'ordre d'ajout.

## Solution
Tri par `At`, puis par type d'événement (ordre de l'enum), puis `Seq`.

## Pour l'éviter
Un test vérifie l'ordre de l'historique après des changements simultanés.
