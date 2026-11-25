# 025 — Un corps vide, `null` ou d'un mauvais type répondait 500
Date : 2026-11-25 · Phase : 5 · Composant : API · Lien : branche `feat/p5-backend`

## Symptôme
`POST /api/v1/articles` (et toutes les routes à corps obligatoire) avec un corps vide, le littéral `null` ou un JSON d'un autre type (`[]`, `42`) répondait 500 au lieu d'un 400. Déjà noté en mineur à la phase 4 (corps vide) ; le test `NulCharacterEdgeTests` l'actait même comme « réponse du framework ».

## Contexte
Phase 5, durcissement des entrées. Le garde `NulCharacterGuard` laissait volontairement passer un corps vide (routes sans corps comme `publish`) et ne traitait pas `null`.

## Cause racine
Le binder des API minimales lève une `BadHttpRequestException` (400 normalement) ou une `JsonException` quand le corps est absent ou de mauvais type. Le middleware `UseExceptionHandler` de l'application attrape toute exception et la transforme en 500 ; seul `TenantViolationException` était traité par notre `IExceptionHandler`.

## Solution
Deux points, pas un par route : le garde de requête sait si l'endpoint exige un corps (`IAcceptsMetadata` non optionnel) et refuse en 400 un corps vide, blanc ou `null` ; `ApiExceptionHandler` convertit `BadHttpRequestException` et `JsonException` en leur statut 4xx (400, 413, 415). Voir `docs/api/errors.md`.

## Pour l'éviter
`EmptyBodyTests` : test paramétré sur chaque route à corps obligatoire (9 formes de corps), plus un test qui compare cette liste aux endpoints réellement enregistrés, pour qu'une nouvelle route ne reste pas sans couverture.
