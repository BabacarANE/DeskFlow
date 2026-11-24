# 022 — Une couleur `#123456\n` était acceptée par la validation du thème
Date : 2026-11-25 · Phase : 4 · Composant : API · Lien : PR `feat/p4-backend`

## Symptôme
Un test unitaire a montré que `"#123456\n"` passait la validation du format `#RRGGBB`. Enregistrée, la valeur de 8 caractères aurait dépassé la colonne de 7 caractères et provoqué un 500 sur `PUT /theme`.

## Cause racine
En .NET, `$` accepte un retour à la ligne final.

## Solution
Ancrage par `\z` à la place de `$`, avec test.

## Pour l'éviter
Même ancrage pour toute regex de validation (même défaut corrigé pour l'identifiant de corrélation à la phase 3).
