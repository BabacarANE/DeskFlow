# DeskFlow

- Source de vérité : docs/SPEC.md. Lis les sections utiles avant d'agir.
- Code, identifiants et commits en anglais (Conventional Commits). Interface et docs en français.
- Jamais de push direct sur main : branche, pull request, CI verte.
- Jamais de secret dans le dépôt : variables d'environnement, .env ignoré, .env.example à jour.
- Aucune pull request sans rapport de l'auditor (SPEC section 16, porte qualité).
- Tout problème réel résolu : une entrée dans docs/troubleshooting (SPEC section 12), jamais inventée.
- Palette verrouillée (SPEC section 10) : aucun hex en dur dans les composants.
