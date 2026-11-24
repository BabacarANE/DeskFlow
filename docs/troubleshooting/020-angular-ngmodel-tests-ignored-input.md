# 020 — Les tests de recherche ne déclenchaient aucun appel API
Date : 2026-11-25 · Phase : 4 · Composant : Frontend (tests) · Lien : PR `feat/p4-frontend`

## Symptôme
Dans les tests de la liste de la base de connaissances, taper dans le champ de recherche n'appelait jamais l'API.

## Cause racine
`NgModel` dans un `<form>` enregistre ses contrôles dans une microtâche : les événements envoyés juste après le premier rendu étaient ignorés. De plus, les effets de `toObservable` sont liés à la vue : `TestBed.tick()` seul ne les exécute pas.

## Solution
`await fixture.whenStable()` avant de saisir, et `fixture.detectChanges()` pour exécuter les effets.

## Pour l'éviter
Même séquence dans tout nouveau test de champ lié à `NgModel` ou à `toObservable`.
