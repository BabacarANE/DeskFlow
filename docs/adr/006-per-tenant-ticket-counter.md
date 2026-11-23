# ADR 006 — Numéro de ticket par compteur d'entreprise

Statut : accepté · Date : 2026-11-23

## Contexte
Le numéro de ticket est séquentiel par entreprise et unique sur `(TenantId, Number)` (SPEC sections 4 et 6). Un `MAX(Number) + 1` échoue sous concurrence.

## Décision
Une table `TicketCounters` (une ligne par entreprise) incrémentée par `INSERT … ON CONFLICT DO UPDATE … RETURNING` dans la même transaction que l'insertion du ticket. Le verrou de ligne sérialise les créations d'une même entreprise ; si l'insertion échoue, la transaction annule aussi le compteur (pas de trou). La table porte un filtre de tenant.

## Conséquences
- Numéros consécutifs sans trou, testés avec 25 créations parallèles.
- Les créations d'une même entreprise sont sérialisées : acceptable à l'échelle du MVP.
