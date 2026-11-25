# Déploiement (VPS + Docker Compose)

> **État de validation.** Ce flux n'a **pas été testé sur un vrai serveur**. Ont été vérifiés : la validité de `docker-compose.prod.yml` (`docker compose config`), celle du `Caddyfile` (`caddy validate`) et la syntaxe de `deploy.yml`. Le build et le push GHCR, l'émission du certificat HTTPS, le routage Caddy et l'étape SSH n'ont jamais tourné en conditions réelles. Attendez-vous à ajuster quelques détails au premier déploiement, puis corrigez ce document.

## Vue d'ensemble

```
Internet ──443──> Caddy ──> api:8080   (/api, /hubs, /health, /signin-*)
                       └──> web:80     (application Angular, nginx)
api, worker ──> postgres, rabbitmq (réseau interne uniquement)
```

- Seuls les ports 80 et 443 de Caddy sont publics. PostgreSQL n'est pas publié. L'interface de management RabbitMQ (et Mailpit, si activé) n'écoute que sur `127.0.0.1` du serveur : on y accède par tunnel SSH.
- Les trois images (`deskflow-api`, `deskflow-worker`, `deskflow-web`) sont construites par `.github/workflows/deploy.yml` et publiées sur `ghcr.io/<propriétaire>/`.
- Caddy obtient et renouvelle seul le certificat Let's Encrypt. Il envoie les routes d'API directement à l'API (et non via le nginx du front) pour que l'API voie `X-Forwarded-Proto: https`, dont dépendent l'URI de redirection OAuth et le cookie `Secure` du refresh token.

## Prérequis

- Un VPS Linux (1 vCPU et 2 Go de RAM suffisent pour une démo) avec Docker Engine et le plugin Compose.
- Un nom de domaine dont l'enregistrement DNS (A/AAAA) pointe vers le serveur ; ports 80 et 443 ouverts dans le pare-feu.
- Un serveur SMTP pour les e-mails (ou, pour une démo, le profil Mailpit, voir plus bas).

## Variables d'environnement

Elles se placent dans un fichier `.env` à côté de `docker-compose.prod.yml` sur le serveur (jamais dans le dépôt). Le modèle est `.env.example`.

| Variable | Obligatoire | Rôle |
| --- | --- | --- |
| `DOMAIN` | oui | Nom d'hôte public (`desk.exemple.fr`). Sert aussi à `App__PublicBaseUrl` (`https://DOMAIN`), utilisé dans les liens des e-mails. |
| `GHCR_OWNER` | oui | Propriétaire GitHub des images, **en minuscules**. |
| `IMAGE_TAG` | non | Version à lancer (`sha-xxxxxxx`, `v1.0.0` ou `latest`). Le workflow de déploiement l'écrit dans `.env`. |
| `JWT_KEY` | oui | Clé de signature des jetons : `openssl rand -hex 32`. Changer la clé déconnecte tout le monde. |
| `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | oui | Base de données. Mot de passe aléatoire. |
| `RABBITMQ_USER`, `RABBITMQ_PASSWORD` | oui | Broker. Le mot de passe est placé dans une URI `amqp://` : uniquement des caractères sûrs (`openssl rand -hex 16`). |
| `SUPERADMIN_EMAIL` | conseillé | Premier SuperAdmin de la plateforme (il se connecte avec Google ou GitHub avec cet e-mail vérifié). |
| `DEMO_ENABLED` | non | `false` par défaut en production. `true` active `/auth/demo`, le seeder de données fictives et la bannière de démo (démo publique). |
| `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `GITHUB_CLIENT_ID`, `GITHUB_CLIENT_SECRET` | non | Connexion OAuth, voir ci-dessous. |
| `SMTP_HOST`, `SMTP_PORT`, `SMTP_USE_TLS`, `SMTP_USER`, `SMTP_PASSWORD`, `SMTP_FROM` | non | Serveur d'envoi. Sans `SMTP_HOST`, l'hôte est `mailpit` (profil `mailpit`). |

## URI de redirection OAuth

Voir `docs/setup-oauth.md` pour la création des identifiants. En production, déclarer :

| Fournisseur | URI de redirection |
| --- | --- |
| Google | `https://<DOMAIN>/signin-google` |
| GitHub | `https://<DOMAIN>/signin-github` |

Sans identifiants OAuth, l'application démarre et seule la connexion de démo (si `DEMO_ENABLED=true`) fonctionne.

## Premier démarrage

1. Sur le serveur, récupérer les fichiers (la méthode la plus simple est de cloner le dépôt) :
   ```bash
   git clone https://github.com/<propriétaire>/DeskFlow.git && cd DeskFlow
   cp .env.example .env
   ```
2. Éditer `.env` : renseigner au minimum `DOMAIN`, `GHCR_OWNER`, `JWT_KEY`, `POSTGRES_PASSWORD`, `RABBITMQ_PASSWORD`, `SUPERADMIN_EMAIL`. Supprimer ou ignorer les ports de développement (`WEB_PORT`, etc.) : le fichier de production ne les lit pas. Restreindre les droits : `chmod 600 .env`.
3. Publier les images : lancer le workflow **Deploy** (Actions, « Run workflow ») ou pousser un tag `v0.1.0`. L'étape SSH est ignorée tant que ses secrets n'existent pas.
4. Les paquets GHCR d'un dépôt privé sont privés. Soit les rendre publics (page du paquet, « Package settings »), soit se connecter une fois sur le serveur avec un jeton personnel ayant le droit `read:packages` :
   ```bash
   echo "$GHCR_TOKEN" | docker login ghcr.io -u <utilisateur> --password-stdin
   ```
5. Démarrer :
   ```bash
   docker compose -f docker-compose.prod.yml pull
   docker compose -f docker-compose.prod.yml up -d --wait
   # Démo sans fournisseur d'e-mail : SMTP_HOST=mailpit dans .env, puis ajouter --profile mailpit
   ```
6. Ouvrir `https://<DOMAIN>`. Les migrations s'appliquent au démarrage de l'API. Le certificat est émis au premier accès ; `docker compose -f docker-compose.prod.yml logs caddy` en cas de problème (DNS, port 80 bloqué).

## Déploiement automatique (GitHub Actions)

`deploy.yml` se déclenche à la main (`workflow_dispatch`) ou sur un tag `v*`. Il construit et pousse les trois images (`sha-xxxxxxx`, plus `vX.Y.Z` et `latest` sur un tag), puis, **seulement si les trois secrets existent**, déploie par SSH.

| Secret ou variable | Rôle |
| --- | --- |
| `DEPLOY_HOST` (secret) | Nom ou adresse du serveur. |
| `DEPLOY_USER` (secret) | Utilisateur SSH (membre du groupe `docker`). |
| `DEPLOY_SSH_KEY` (secret) | Clé privée dédiée au déploiement (`ssh-keygen -t ed25519`), clé publique dans `~/.ssh/authorized_keys` du serveur. |
| `DEPLOY_KNOWN_HOSTS` (secret, optionnel) | Sortie de `ssh-keyscan <hôte>` pour épingler la clé du serveur. Sans elle, la clé est acceptée à la première connexion (TOFU). |
| `DEPLOY_PATH`, `DEPLOY_PORT` (variables, optionnelles) | Dossier sur le serveur (défaut `deskflow`, relatif au dossier personnel) et port SSH (défaut 22). |

Le job utilise l'environnement GitHub `production` : on peut y exiger une validation manuelle. L'étape copie `docker-compose.prod.yml`, `docker/Caddyfile` et `docker/deploy.sh`, se connecte à GHCR avec le jeton éphémère du workflow (passé sur l'entrée standard, jamais en argument), mémorise `IMAGE_TAG` dans `.env`, puis fait `pull` et `up -d --wait`. Si le dépôt est cloné dans un autre dossier que `deskflow` (ex. `DeskFlow`, la casse compte sous Linux), définir la variable `DEPLOY_PATH` en conséquence, sinon le workflow copie les fichiers dans un second dossier. Il écrase `docker-compose.prod.yml` et `docker/Caddyfile` du clone : un `git pull` ultérieur peut signaler un conflit local. Le `.env` du serveur doit exister (premier démarrage) : le workflow ne le crée pas et ne le modifie que pour `IMAGE_TAG`.

## Sauvegarde de PostgreSQL

Les données vivent dans le volume `deskflow-prod_postgres-data`. Sauvegarde logique, à planifier (cron quotidien) et à copier hors du serveur :

```bash
cd ~/DeskFlow
docker compose -f docker-compose.prod.yml exec -T postgres \
  sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --format=custom' \
  > "backup-$(date +%F).dump"
```

Exemple de cron (`crontab -e`), avec conservation de 14 jours :

```
0 3 * * * cd /home/deploy/DeskFlow && docker compose -f docker-compose.prod.yml exec -T postgres sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --format=custom' > /home/deploy/backups/deskflow-$(date +\%F).dump && find /home/deploy/backups -name 'deskflow-*.dump' -mtime +14 -delete
```

Restauration (sur une base vide ; l'API arrêtée) :

```bash
docker compose -f docker-compose.prod.yml stop api worker
docker compose -f docker-compose.prod.yml exec -T postgres \
  sh -c 'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists --no-owner' < backup-2026-01-01.dump
docker compose -f docker-compose.prod.yml start api worker
```

Une sauvegarde qui n'a jamais été restaurée n'est pas une sauvegarde : testez la restauration sur une machine jetable. Les files RabbitMQ ne sont pas sauvegardées : les événements en attente sont dans l'outbox PostgreSQL.

## Mise à jour

- Automatique : relancer le workflow **Deploy** (ou pousser un nouveau tag `v*`).
- À la main, sur le serveur :
  ```bash
  git pull   # met à jour docker-compose.prod.yml et le Caddyfile
  sed -i 's|^IMAGE_TAG=.*|IMAGE_TAG=v0.2.0|' .env
  docker compose -f docker-compose.prod.yml pull
  docker compose -f docker-compose.prod.yml up -d --remove-orphans --wait
  ```
- Retour arrière : remettre l'ancien `IMAGE_TAG` et relancer `up -d`. Les migrations ne sont pas inversées : faites une sauvegarde avant toute mise à jour qui en contient.

## Administration à distance

```bash
# Interface RabbitMQ (management) : http://localhost:15672
ssh -L 15672:127.0.0.1:15672 <utilisateur>@<serveur>
# Mailpit (profil mailpit) : http://localhost:8025
ssh -L 8025:127.0.0.1:8025 <utilisateur>@<serveur>
# Journaux
docker compose -f docker-compose.prod.yml logs -f api worker
```

La procédure de rejeu de la dead-letter queue est dans `docs/runbooks/dlq.md`.

## Points de vigilance

- Une démo publique (`DEMO_ENABLED=true`) permet à n'importe qui de se connecter avec des comptes fictifs : n'y mettez aucune donnée réelle.
- Le pare-feu du VPS ne doit autoriser que 22, 80 et 443. Docker publie les ports en contournant certains pare-feu (ufw) : c'est pourquoi PostgreSQL n'est pas publié du tout et les interfaces de management le sont sur `127.0.0.1` uniquement.
- Les mises à jour de sécurité du système et des images de base (`postgres`, `rabbitmq`, `caddy`) ne sont pas automatisées.
