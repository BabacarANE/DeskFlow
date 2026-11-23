# ADR 005 — `xmin` PostgreSQL comme jeton de concurrence des tickets

Statut : accepté · Date : 2026-11-23

## Contexte
Le SPEC section 6 exige une concurrence optimiste sur les tickets via `RowVersion` : le client renvoie le jeton, un conflit renvoie 409.

## Décision
Mapper `RowVersion` sur la colonne système `xmin` (`Property(x => x.RowVersion).IsRowVersion()`) et l'exposer en chaîne décimale opaque. Aucune colonne dédiée : `xmin` change à chaque écriture de la ligne. Un commentaire met aussi à jour la ligne (`UpdatedAt`) et change donc le jeton ; les commentaires concurrents sont rejoués (jusqu'à 10 tentatives). Un PATCH qui ne change rien ne modifie pas le jeton.

## Conséquences
- Pas de colonne ni de trigger à maintenir ; le SQL de migration ne contient pas `xmin`.
- Un jeton mal formé donne 422, un jeton périmé 409 (`…/concurrency-conflict`).
- Le jeton est propre à PostgreSQL ; un changement de base obligerait à le remplacer.
