# ADR 002 — Base partagée avec colonne `TenantId`

Statut : accepté · Date : 2026-11-23

## Contexte
La plateforme est multi-entreprises. L'isolation des données est une exigence non négociable (SPEC section 4), mais le MVP doit rester simple à exploiter.

## Décision
Une seule base PostgreSQL ; chaque ligne métier porte un `TenantId`. L'isolation est appliquée automatiquement : filtre global EF Core (`HasQueryFilter`) alimenté par `ITenantContext` (claim `tid` du JWT), intercepteur `SaveChanges` qui renseigne et vérifie `TenantId`, et `TenantId` dans chaque message RabbitMQ. Seuls les endpoints SuperAdmin désactivent le filtre.

## Conséquences
- Une migration et une sauvegarde pour toutes les entreprises ; coût d'exploitation minimal.
- Le risque de fuite entre entreprises est porté par le code : il est couvert par des tests d'intégration obligatoires (SPEC section 4).
- Une ressource d'une autre entreprise renvoie 404, pas 403.
- Alternatives écartées : un schéma ou une base par entreprise (migrations et connexions plus lourdes pour peu de gain au MVP).
