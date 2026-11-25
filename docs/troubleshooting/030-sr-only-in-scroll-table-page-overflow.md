# 030 — Défilement horizontal de toute la page sur mobile (admin, Utilisateurs)
Date : 2026-11-25 · Phase : 5 · Composant : Web · Lien : branche `feat/p5-e2e`

## Symptôme
À 390 px de large, la page `/admin` (onglet Utilisateurs) défilait horizontalement : `scrollWidth - clientWidth` valait 238 px, alors que le tableau est dans un conteneur `.table-wrap` en `overflow-x: auto`. Constaté lors de la première vérification dans un vrai navigateur (Playwright, Chromium), pas par les tests jsdom.

## Contexte
Les cellules du tableau contiennent des `<span class="sr-only">` (texte réservé aux lecteurs d'écran), qui sont en `position: absolute`.

## Cause racine
Un élément en `position: absolute` est borné par son ancêtre positionné le plus proche, pas par un ancêtre `overflow: auto` non positionné. `.table-wrap` n'était pas positionné : les `sr-only` situés dans les colonnes hors écran échappaient au clipping et élargissaient la page.

## Solution
`.table-wrap` devient `relative` (`src/styles.css`) : il devient le bloc conteneur des éléments `sr-only` et les clippe. Vérifié : débordement de 238 px à 0 sur les cinq onglets d'administration.

## Pour l'éviter
Tout conteneur `overflow-x-auto` qui peut contenir du `sr-only` doit être `relative`. Le test E2E n'automatise pas cette mesure ; elle se vérifie à 390 px avec `document.documentElement.scrollWidth - clientWidth`.
