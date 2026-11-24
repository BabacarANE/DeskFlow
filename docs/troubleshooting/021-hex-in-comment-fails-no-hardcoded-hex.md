# 021 — Un hex dans un commentaire faisait échouer le test anti-hex
Date : 2026-11-25 · Phase : 4 · Composant : Frontend (tests) · Lien : PR `feat/p4-frontend`

## Symptôme
`no-hardcoded-hex.spec` échouait alors qu'aucun composant ne contenait de couleur en dur.

## Cause racine
Un commentaire de `contrast.ts` citait `#0B2A3C` ; le test ne distingue pas un commentaire d'un littéral.

## Solution
Reformulation du commentaire sans hex.

## Pour l'éviter
La règle « aucun hex hors `tokens.css` » vaut aussi pour les commentaires de code.
