# 018 — Le jeton du hub SignalR apparaissait dans les journaux nginx
Date : 2026-11-24 · Phase : 3 · Composant : Docker (nginx) · Lien : PR `feat/p3-backend-consumers`

## Symptôme
Relevé par l'audit de la tranche B : `GET /hubs/notifications?…&access_token=<JWT>` serait écrit dans les logs du conteneur `web` (lecture de la configuration ; non reproduit en exécution).

## Cause racine
Le navigateur ne peut pas poser d'en-tête sur un WebSocket, donc le JWT passe en query string ; le journal d'accès par défaut de nginx enregistre la requête complète, et `/hubs` était déjà proxifié.

## Solution
Bloc `location /hubs/` dédié avec `access_log off` (`docker/nginx.conf`), ADR 009 corrigé. Côté API, Serilog ne journalise que le chemin.

Limite : `access_log off` ne couvre pas le journal d'erreur de nginx, qui reprend la ligne de requête (query string comprise) lors d'un échec amont. Risque accepté et consigné dans l'ADR 009 (jeton valable 15 min, journaux non exposés).

## Pour l'éviter
Tout proxy devant l'API doit exclure la query string du hub de ses journaux.
