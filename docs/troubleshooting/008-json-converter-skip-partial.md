# 008 — Un identifiant mal formé dans un PATCH répond 500 (`Utf8JsonReader.Skip`)
Date : 2026-11-23 · Phase : 2 · Composant : API · Lien : PR `feat/p2-backend-tickets`

## Symptôme
`PATCH /tickets/{id}` avec un `assigneeId` mal formé répond 500 au lieu de 422 (« Cannot skip tokens on partial JSON »).

## Contexte
Convertisseur JSON maison `OptionalGuidConverter` (distingue champ absent et `null`), corps lu en flux.

## Cause racine
`Utf8JsonReader.Skip()` échoue dans un convertisseur quand le corps est lu en flux et que le jeton n'est pas entièrement en mémoire tampon.

## Solution
Consommer la valeur avec `JsonDocument.ParseValue(ref reader)`, puis signaler le champ mal formé au validateur (422 avec `errors.<champ>`).

## Pour l'éviter
Des valeurs mal formées (texte, nombre, objet) sont testées.
