# ADR 004 — Fenêtre de validité de l'access token après désactivation

Statut : accepté · Date : 2026-11-23

## Contexte
L'access token JWT dure 15 minutes et est validé sans accès à la base (SPEC section 5). Désactiver un utilisateur, changer son rôle ou suspendre son entreprise n'invalide donc pas un access token déjà émis.

## Décision
Accepter cette fenêtre de 15 minutes au maximum. Les refresh tokens, eux, sont révoqués immédiatement à la désactivation ou à la suspension, et `/auth/refresh` et `/me` refusent aussitôt le compte. Aucune vérification en base à chaque requête dans le MVP.

## Conséquences
- Un utilisateur désactivé ou rétrogradé garde ses droits jusqu'à 15 minutes.
- Si cette fenêtre devient inacceptable : réduire la durée du token, ou vérifier `IsActive` et le rôle à chaque requête (coût : une lecture par appel).
- `POST /auth/logout` exige un access token valide (contrat) : le front rafraîchit avant de se déconnecter.
