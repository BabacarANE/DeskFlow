# 014 — Les consommateurs restaient morts après une coupure de connexion
Date : 2026-11-24 · Phase : 3 · Composant : Worker · Lien : PR `feat/p3-backend-messaging`

## Symptôme
Après une fermeture de connexion côté serveur ou un redémarrage du broker, un consommateur ne se réabonnait jamais, et `/health/ready` du Worker restait « prêt ».

## Contexte
Test d'intégration qui coupe la connexion puis redémarre le conteneur RabbitMQ.

## Cause racine
Le canal fermé par le serveur n'était pas recréé par la reprise automatique, et rien ne surveillait l'état des consommateurs.

## Solution
Chaque consommateur a une boucle d'abonnement supervisée (attentes de 1, 2 puis 5 s) ; la connexion des consommateurs n'a pas de reprise automatique, pour qu'un redémarrage ne laisse pas deux abonnements sur une file ; `/health/ready` du Worker inclut un contrôle « consumers ». Après `docker stop` puis `docker start`, le même Worker reprend seul.

## Pour l'éviter
Tests de `MessagingFailureTests` (connexion fermée par le serveur, redémarrage du broker).
