/** Limits of ArticleRequest (docs/api/openapi.json). */
export const TITLE_MIN = 3;
export const TITLE_MAX = 200;
export const SYMPTOM_MAX = 4000;
export const CAUSE_MAX = 4000;
export const SOLUTION_MAX = 8000;
export const TAG_MAX_COUNT = 10;
export const TAG_MAX_LENGTH = 30;

/** Splits the comma separated tag field: trimmed, no empty tag, duplicates removed. */
export function parseTags(text: string): string[] {
  const tags: string[] = [];
  for (const part of text.split(',')) {
    const tag = part.trim();
    if (tag && !tags.some((t) => t.toLowerCase() === tag.toLowerCase())) tags.push(tag);
  }
  return tags;
}

/** Validation message of the tag field, or null when it is valid. */
export function tagsError(text: string): string | null {
  const tags = parseTags(text);
  if (tags.length > TAG_MAX_COUNT) return `Au plus ${TAG_MAX_COUNT} étiquettes.`;
  if (tags.some((t) => t.length > TAG_MAX_LENGTH)) {
    return `Chaque étiquette comporte ${TAG_MAX_LENGTH} caractères au plus.`;
  }
  return null;
}

/** Publishing needs a title, a symptom and a solution (a cause may stay empty). */
export function missingForPublish(value: { title: string; symptom: string; solution: string }): {
  symptom: boolean;
  solution: boolean;
  title: boolean;
} {
  return {
    title: !value.title.trim(),
    symptom: !value.symptom.trim(),
    solution: !value.solution.trim(),
  };
}
