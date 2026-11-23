# 004 — Les messages de validation des formulaires ne s'affichent pas (OnPush)
Date : 2026-11-23 · Phase : 2 · Composant : Web · Lien : PR `feat/p2-frontend-tickets`

## Symptôme
Après un envoi invalide, ou après une erreur 422 posée par `applyFieldErrors`, les messages de validation de `FieldError` n'apparaissaient pas sous les champs.

## Contexte
Angular 22, composants standalone ; `OnPush` est la stratégie de détection par défaut. Le défaut touchait aussi les formulaires de la phase 1 (invitations), faute de test à l'époque.

## Cause racine
`FieldError` ne se rafraîchissait pas quand l'état du contrôle changeait, car son entrée `control` garde la même référence.

## Solution
`FieldError` s'abonne à `control.events` et dérive son affichage d'un `computed` (commit `2a1ee90`), avec un test qui échoue sans le correctif.

## Pour l'éviter
Tout composant qui lit l'état d'un contrôle de formulaire doit s'abonner à `control.events` (ou à un signal dérivé). Le test dédié (`field-error.spec.ts`) protège ce comportement.
