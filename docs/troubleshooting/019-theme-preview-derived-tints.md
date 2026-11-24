# 019 — L'aperçu du thème ne changeait pas de teinte
Date : 2026-11-25 · Phase : 4 · Composant : Frontend · Lien : PR `feat/p4-frontend`

## Symptôme
Dans l'écran de thème, l'aperçu en direct n'affichait pas les nouvelles couleurs saisies (les teintes dérivées restaient celles du thème enregistré).

## Contexte
L'aperçu écrit les variables CSS sur son propre conteneur, jamais sur `:root` tant que le thème n'est pas enregistré.

## Cause racine
Les teintes dérivées (`--tint-*`) sont déclarées sur `:root` avec `var(--color-primary)` : elles sont résolues à cet endroit. Surcharger `--color-primary` sur un conteneur ne les fait donc pas suivre.

## Solution
La classe `.theme-scope` (`web/src/styles.css`) redéclare les teintes dérivées dans le conteneur d'aperçu, sans nouveau token ni hex.

## Pour l'éviter
Test dédié : `:root` reste intact pendant la saisie et l'aperçu porte les variables recalculées.
