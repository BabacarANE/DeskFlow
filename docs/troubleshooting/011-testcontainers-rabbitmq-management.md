# 011 — Testcontainers RabbitMQ : l'API de management est inutilisable par défaut
Date : 2026-11-23 · Phase : 3 · Composant : Tests · Lien : PR `feat/p3-backend-messaging`

## Symptôme
Les tests qui lisent la DLQ par l'API de management échouaient avec `Exposed port 15672/tcp is not mapped`, puis avec un 401.

## Contexte
Module `Testcontainers.RabbitMq`, image avec le plugin de management.

## Cause racine
Le module n'expose pas le port 15672 par défaut, et ses identifiants par défaut sont `rabbitmq` / `rabbitmq`, pas `guest` / `guest`.

## Solution
`WithPortBinding(15672, true)`, `WithUsername("guest")` et `WithPassword("guest")` sur le conteneur de test.

## Pour l'éviter
Les paramètres sont centralisés dans la fixture de messagerie (`MessagingFixtures`).
