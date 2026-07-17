// SPDX-License-Identifier: GPL-2.0-or-later

import type { LogEntry } from '../../api/log';
import { formatQsoDateUtc, formatQsoTimeUtc } from './logbook-formatters';

function normalizeSearchText(value: string): string {
  return value.trim().toLocaleLowerCase();
}

function compactSearchParts(parts: Array<string | number | null | undefined>): string {
  return parts
    .filter((part): part is string | number => part !== null && part !== undefined && part !== '')
    .join(' ')
    .toLocaleLowerCase();
}

export type LogEntryFilterOptions = {
  hideQrzPublished?: boolean;
};

export function isQrzPublished(entry: LogEntry): boolean {
  return Boolean(entry.qrzLogId || entry.qrzUploadedUtc);
}

export function logEntrySearchText(entry: LogEntry): string {
  const frequencyFixed = typeof entry.frequencyMhz === 'number' && Number.isFinite(entry.frequencyMhz)
    ? entry.frequencyMhz.toFixed(3)
    : null;
  return compactSearchParts([
    entry.callsign,
    formatQsoDateUtc(entry.qsoDateTimeUtc),
    formatQsoTimeUtc(entry.qsoDateTimeUtc),
    entry.qsoDateTimeUtc,
    frequencyFixed,
    entry.frequencyMhz,
    entry.band,
    entry.mode,
    entry.rstSent,
    entry.rstRcvd,
    entry.name,
    entry.grid,
    entry.country,
    entry.dxcc,
    entry.cqZone,
    entry.ituZone,
    entry.state,
    entry.comment,
    entry.qrzLogId,
    entry.qrzUploadedUtc,
    entry.qrzQslRcvdUtc,
    ...(entry.tags ?? []),
  ]);
}

function parseTerms(query: string): { text: string[]; tags: string[] } {
  const text: string[] = [];
  const tags: string[] = [];
  for (const term of normalizeSearchText(query).split(/\s+/).filter(Boolean)) {
    if (term.startsWith('tag:')) {
      const tag = term.slice(4).trim();
      if (tag) tags.push(tag);
    } else {
      text.push(term);
    }
  }
  return { text, tags };
}

export function filterLogEntries(
  entries: LogEntry[],
  query: string,
  options: LogEntryFilterOptions = {},
): LogEntry[] {
  const candidates = options.hideQrzPublished
    ? entries.filter((entry) => !isQrzPublished(entry))
    : entries;
  const terms = parseTerms(query);
  if (terms.text.length === 0 && terms.tags.length === 0) return candidates;

  return candidates.filter((entry) => {
    const haystack = logEntrySearchText(entry);
    const entryTags = (entry.tags ?? []).map((tag) => tag.toLocaleLowerCase());
    return terms.text.every((term) => haystack.includes(term))
      && terms.tags.every((term) => entryTags.some((tag) => tag.includes(term)));
  });
}
