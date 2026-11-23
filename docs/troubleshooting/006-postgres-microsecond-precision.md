# 006 — Un horodatage renvoyé diffère de celui relu en base (précision)
Date : 2026-11-23 · Phase : 2 · Composant : API · Lien : PR `feat/p2-backend-tickets`

## Symptôme
Un test comparant `firstResponseAt` renvoyé par l'API à la valeur relue en base échouait : `…2859277` contre `…2859270`.

## Contexte
`DateTimeOffset` .NET, colonne `timestamptz` PostgreSQL.

## Cause racine
.NET garde une précision de 100 ns, PostgreSQL de 1 µs.

## Solution
Tronquer l'horloge à la microseconde dans `TicketService` avant de stocker et de renvoyer.

## Pour l'éviter
Les tests comparent les valeurs exactes renvoyées puis relues.
