#!/usr/bin/env bash
# Run on the server by .github/workflows/deploy.yml: pulls the images of one tag and restarts the stack.
# Usage: deploy.sh <deploy-dir> <image-tag> <registry-user>   (the registry token is read from stdin)
set -euo pipefail

dir="$1"
tag="$2"
registry_user="$3"

cd "$dir"
test -f .env || { echo ".env is missing in $dir: see docs/deploy.md" >&2; exit 1; }

docker login ghcr.io -u "$registry_user" --password-stdin
trap 'docker logout ghcr.io >/dev/null 2>&1 || true' EXIT

# Pin the tag in .env so that a later manual "docker compose up" keeps the same version.
if grep -q '^IMAGE_TAG=' .env; then
  sed -i "s|^IMAGE_TAG=.*|IMAGE_TAG=${tag}|" .env
else
  echo "IMAGE_TAG=${tag}" >> .env
fi

docker compose -f docker-compose.prod.yml pull
docker compose -f docker-compose.prod.yml up -d --remove-orphans --wait --wait-timeout 300
docker image prune -f
