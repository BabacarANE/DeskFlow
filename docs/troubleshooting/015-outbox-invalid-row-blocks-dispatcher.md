# 015 — Une ligne d'outbox invalide bloquait le dispatcher
Date : 2026-11-24 · Phase : 3 · Composant : Worker · Lien : PR `feat/p3-backend-messaging`

## Symptôme
Une enveloppe invalide restait en attente, relue et journalisée en erreur chaque seconde ; à partir de 50 lignes (taille du lot), le dispatcher ne publiait plus rien.

## Contexte
Relevé par l'audit de la tranche A.

## Cause racine
La ligne invalide était simplement ignorée (`continue`) sans être marquée, donc reprise en tête de chaque lot.

## Solution
Migration additive `OutboxFailureMarker` (`FailedAt`, `FailureReason`) : la ligne est marquée en échec une seule fois et le dispatcher continue.

## Pour l'éviter
Test avec 60 lignes invalides et une ligne valide, qui est bien publiée. Limite connue : 50 lignes non routables ou plus en tête de l'outbox bloqueraient encore un lot.
