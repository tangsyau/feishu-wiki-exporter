import type {
  KnowledgeDocument,
  KnowledgePage,
  KnowledgeSearchIndex,
  SearchResult
} from "./types";
import type { KnowledgeProvider } from "./providers/knowledge-provider";

const maxCandidates = 80;

export async function searchKnowledge(
  query: string,
  documents: KnowledgeDocument[],
  index: KnowledgeSearchIndex,
  provider: KnowledgeProvider
): Promise<SearchResult[]> {
  const normalizedQuery = normalize(query.trim());
  if (!normalizedQuery) {
    return [];
  }

  const byId = new Map(documents.map(document => [document.id, document]));
  const titleTerms = getTitleTerms(normalizedQuery);
  const titleMatches = documents.filter(document =>
    isTitleMatch(document, normalizedQuery, titleTerms) ||
    normalize(document.breadcrumb).includes(normalizedQuery)
  );

  const tokens = tokenize(normalizedQuery);
  let postingIds: string[] = [];
  if (tokens.length > 0) {
    const postingLists = tokens.map(token => index.postings[token] ?? []);
    if (postingLists.every(list => list.length > 0)) {
      postingLists.sort((left, right) => left.length - right.length);
      const candidates = new Set(postingLists[0]);
      for (const list of postingLists.slice(1)) {
        const allowed = new Set(list);
        for (const id of candidates) {
          if (!allowed.has(id)) {
            candidates.delete(id);
          }
        }
      }
      postingIds = [...candidates];
    }
  }

  const candidateIds = new Set<string>(titleMatches.map(document => document.id));
  postingIds.forEach(id => candidateIds.add(id));
  const candidates = [...candidateIds]
    .map(id => byId.get(id))
    .filter((document): document is KnowledgeDocument => document !== undefined)
    .sort((left, right) =>
      Number(isTitleMatch(right, normalizedQuery, titleTerms)) - Number(isTitleMatch(left, normalizedQuery, titleTerms)) ||
      baseScore(right, normalizedQuery, titleTerms) - baseScore(left, normalizedQuery, titleTerms))
    .slice(0, maxCandidates);

  const results = await Promise.all(candidates.map(async document => {
    let page: KnowledgePage | null = null;
    if (document.pagePath) {
      try {
        page = await provider.loadPage(document.pagePath);
      } catch {
        page = null;
      }
    }

    const text = page?.text ?? "";
    const normalizedText = normalize(text);
    const phraseMatch = normalizedText.includes(normalizedQuery);
    const titleMatched = isTitleMatch(document, normalizedQuery, titleTerms);
    const score = baseScore(document, normalizedQuery, titleTerms) + (phraseMatch ? 240 : text ? 80 : 0);
    return {
      document,
      score,
      titleMatched,
      snippet: makeSnippet(text, query, tokens)
    } satisfies SearchResult;
  }));

  return results.sort((left, right) => Number(right.titleMatched) - Number(left.titleMatched) ||
    right.score - left.score ||
    left.document.title.localeCompare(right.document.title, "zh-CN")).slice(0, 50);
}

export function normalize(value: string): string {
  return value.normalize("NFKC").toLowerCase();
}

export function tokenize(value: string): string[] {
  const normalized = normalize(value);
  const tokens = new Set<string>();
  let previousCjk: string | null = null;
  let word = "";

  const flushWord = () => {
    if (word) {
      tokens.add(word);
      word = "";
    }
  };

  for (const character of normalized) {
    if (isCjk(character)) {
      flushWord();
      if (previousCjk) {
        tokens.add(previousCjk + character);
      }
      previousCjk = character;
      continue;
    }

    previousCjk = null;
    if (/^[\p{L}\p{N}]$/u.test(character)) {
      word += character;
    } else {
      flushWord();
    }
  }
  flushWord();
  return [...tokens];
}

function isCjk(character: string): boolean {
  const code = character.codePointAt(0) ?? 0;
  return (code >= 0x3400 && code <= 0x9fff) ||
    (code >= 0xf900 && code <= 0xfaff) ||
    (code >= 0x3040 && code <= 0x30ff) ||
    (code >= 0xac00 && code <= 0xd7af);
}

function getTitleTerms(query: string): string[] {
  return query.split(/\s+/u).filter(Boolean);
}

function isTitleMatch(document: KnowledgeDocument, query: string, terms: string[]): boolean {
  const title = normalize(document.title);
  return title.includes(query) || (terms.length > 1 && terms.every(term => title.includes(term)));
}

function baseScore(document: KnowledgeDocument, query: string, terms: string[]): number {
  const title = normalize(document.title);
  const breadcrumb = normalize(document.breadcrumb);
  let score = 0;
  if (title === query) score += 1200;
  else if (title.includes(query)) score += 800;
  else if (terms.length > 1 && terms.every(term => title.includes(term))) score += 650;
  if (breadcrumb.includes(query)) score += 220;
  if (document.kind === "docx") score += 10;
  return score;
}

function makeSnippet(text: string, query: string, tokens: string[]): string {
  const compact = text.replace(/\s+/g, " ").trim();
  if (!compact) {
    return "";
  }
  const lower = normalize(compact);
  const targets = [normalize(query), ...tokens].filter(Boolean);
  let position = -1;
  for (const target of targets) {
    position = lower.indexOf(target);
    if (position >= 0) break;
  }
  if (position < 0) position = 0;
  const start = Math.max(0, position - 55);
  const end = Math.min(compact.length, position + 130);
  return `${start > 0 ? "…" : ""}${compact.slice(start, end)}${end < compact.length ? "…" : ""}`;
}
