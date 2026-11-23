# 005 — Le tri des tickets par priorité répond 500 (expression LINQ non traduisible)
Date : 2026-11-23 · Phase : 2 · Composant : API · Lien : PR `feat/p2-backend-tickets`

## Symptôme
`GET /tickets?sort=priority` répond 500 : `LINQ expression 't => (int)t.Priority == 3 ? …' could not be translated`.

## Contexte
EF Core 10, Npgsql ; la priorité est stockée en texte (conversion de valeur) pour sérialiser les enums en chaînes.

## Cause racine
Un littéral d'enum dans la lambda est compilé en comparaison d'entiers, qu'EF ne peut pas appliquer à une colonne texte.

## Solution
Passer les valeurs d'enum par des variables locales capturées dans l'expression de rang (`CASE`).

## Pour l'éviter
Un test d'intégration couvre le tri par priorité.
