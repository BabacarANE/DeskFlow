# Erreurs de requête : corps absent ou illisible

Toutes les erreurs sont des `ProblemDetails` (RFC 7807, `application/problem+json`).

| Situation | Statut | Où c'est traité |
| --- | --- | --- |
| Route à corps obligatoire : corps vide, uniquement des espaces / retours à la ligne, uniquement un BOM, ou littéral JSON `null` (avec ou sans `Content-Type`) | **400** | `NulCharacterGuard` (`IAcceptsMetadata` non optionnel de l'endpoint) |
| Corps qui n'est pas du JSON valide, UTF-8 invalide, imbrication trop profonde | **400** | `NulCharacterGuard` |
| JSON valide mais d'un autre type que l'objet attendu (`[]`, `42`, `"texte"`, `true`) | **400** | `ApiExceptionHandler` (`BadHttpRequestException`, `JsonException`) |
| `Content-Type` non JSON avec un corps non vide | **415** | `ApiExceptionHandler` |
| Corps de plus de 1 Mo | **413** | `NulCharacterGuard` |
| Document bien formé mais contenu invalide (champ manquant, trop long, valeur interdite, caractère NUL) | **422** avec `errors` par champ | validateurs FluentValidation, `NulCharacterGuard` |

Choix : 400 pour « la requête n'est pas lisible comme le contrat l'exige » et 422 pour « elle est lisible mais les données sont refusées », comme déjà pratiqué sur les routes de tickets et d'articles. Aucune de ces situations ne répond 500.

Les routes sans corps (`publish`, `unpublish`, `logout`, `refresh`, les `GET`) ne sont pas concernées : un corps JSON vide y reste accepté.

La correction est unique et générale : elle s'applique à toute route qui déclare un corps obligatoire, y compris les futures. Le test `EmptyBodyTests` énumère les routes à corps obligatoire depuis le `EndpointDataSource` de l'application et échoue si l'une d'elles n'est pas couverte.
