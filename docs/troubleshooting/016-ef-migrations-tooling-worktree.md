# 016 — `dotnet ef` échoue dans un worktree et formate mal les migrations
Date : 2026-11-24 · Phase : 3 · Composant : Outillage · Lien : PR `feat/p3-backend-consumers`

## Symptôme
`dotnet ef migrations add` répondait « Unable to retrieve project metadata », puis « startup project doesn't reference Design ». Les fichiers générés faisaient échouer `dotnet format --verify-no-changes`.

## Cause racine
Aucun `dotnet restore` n'avait été fait dans le worktree ; `Desk.Api` ne référence pas EF Design ; EF génère un BOM UTF-8, du CRLF et un espace de noms en bloc, contraires à `.editorconfig`.

## Solution
`dotnet restore`, puis `--startup-project src/Desk.Infrastructure` ; `dotnet format` sur les fichiers générés.

## Pour l'éviter
Toujours lancer `dotnet format` après `dotnet ef migrations add`.
