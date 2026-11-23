# ADR 007 — Conventions de l'API tickets non précisées par le SPEC

Statut : accepté · Date : 2026-11-23

## Contexte
Le SPEC et le contrat laissent plusieurs points ouverts ; l'audit de la phase 2 les a fait décider (règle du SPEC section 1 : l'option la plus simple).

## Décision
- **Types de problème** : `https://deskflow.dev/problems/{invalid-transition|concurrency-conflict|ticket-closed|category-in-use|duplicate-name}`. Le contrat ne fixe que le suffixe ; les clients comparent le suffixe. Le domaine est fictif, le préfixe est une simple convention d'identifiant.
- **SuperAdmin** : 403 sur le contenu des tickets, `GET /categories` renvoie 200 et une liste vide, `GET /tickets/{id}/events` renvoie 404. Aucune fuite.
- **PATCH** : un statut ou une priorité identique à l'actuel est un no-op (ni 409, ni événement). Un ticket `Closed` reste modifiable (priorité, assignation, catégorie) ; seuls les commentaires sont refusés.
- **Première réponse** : seul un commentaire public d'une personne différente du demandeur renseigne `firstResponseAt`.
- **Colonnes internes** `PendingSince` et `SlaPausedTotal` : elles décalent les échéances SLA à la sortie de Pending et ne sont pas exposées. `ResolvedAt` est posé à `Resolved`, effacé à la réouverture, conservé à `Closed`.
- **Pagination** : une page au-delà du total renvoie 200 avec `items` vide.

## Conséquences
Comportements couverts par des tests d'intégration ; à revoir si le SPEC les précise autrement.

## Écarts connus (phase 2)
- `POST /tickets` avec un `categoryId` mal formé répond 400 (échec de désérialisation) au lieu de 422 annoncé par le contrat ; `PATCH` répond bien 422. Cas marginal, à aligner plus tard.
- Les commentaires concurrents sur un même ticket sont rejoués 10 fois au plus, sans temporisation : au-delà d une dizaine de commentaires simultanés, certains reçoivent 409 `concurrency-conflict`.
