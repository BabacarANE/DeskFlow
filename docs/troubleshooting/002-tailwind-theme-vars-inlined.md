# 002 — Les teintes dérivées du thème ne suivent pas le thème d'entreprise (Tailwind v4)
Date : 2026-11-23 · Phase : 1 · Composant : Web · Lien : PR `feat/p1-frontend-identity`

## Symptôme
Les teintes dérivées (`color-mix(... var(--color-primary) ...)`) déclarées dans `@theme` ressortaient dans le CSS compilé avec les hex par défaut inlinés : elles ne changeaient pas quand le thème de l'entreprise réécrivait `--color-primary` sur `:root`.

## Contexte
Angular 22, Tailwind CSS 4.3.5, tokens dans `web/src/styles/tokens.css`, thème appliqué à l'exécution par `ThemeService` (SPEC section 10).

## Cause racine
Tailwind résout les `var()` internes aux tokens déclarés dans `@theme`, au lieu de les laisser dynamiques.

## Solution
Définir les teintes (`--tint-*`) sur `:root` et les exposer à Tailwind avec `@theme static inline`. Le CSS compilé contient un repli avec hex, puis la version `var()` dans un `@supports`, appliquée par les navigateurs modernes.

## Pour l'éviter
Ne pas déclarer dans `@theme` de token qui dépend d'une variable modifiée à l'exécution ; définir la dérivation sur `:root`. Les tests du `ThemeService` couvrent les variables écrites sur `:root`.
