# 024 — Des `PUT /sla-policies` simultanés provoquaient un deadlock
Date : 2026-11-24 · Phase : 4 · Composant : API · Lien : PR `feat/p4-backend`

## Symptôme
Un test de 8 écritures simultanées sur une entreprise sans lignes SLA : 7 réponses sur 8 étaient des 500.

## Cause racine
Deadlock PostgreSQL (40P01) : les `UPDATE` des quatre lignes partaient dans des ordres différents, et les `INSERT` concurrents entraient en conflit avec l'index unique `(TenantId, Priority)`.

## Solution
`PUT /sla-policies` s'exécute dans une transaction qui prend un verrou consultatif par entreprise (`pg_advisory_xact_lock`, clé paramétrée, libéré au commit ou au rollback) : les écritures d'une même entreprise passent l'une après l'autre.

## Pour l'éviter
Test `Concurrent_first_writes_on_a_company_without_rows_…` (raisonnable mais non strictement déterministe).
