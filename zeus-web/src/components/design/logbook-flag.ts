// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Licensed under the GNU General Public License v2.0-or-later. See the LICENSE
// file at the repository root for the full text.

/**
 * Country-name → flag emoji for the logbook detail card and Top-Countries
 * dashboard. The logbook stores `country` as the free-form DXCC entity name
 * that arrives from QRZ / ADIF (e.g. "United States", "England"), so we map by
 * name rather than by an ISO code we don't carry. Unknown names resolve to a
 * neutral globe glyph so the UI never renders a blank.
 *
 * Flags are built from Unicode regional-indicator pairs so no image assets are
 * needed and rendering stays self-contained (matters for the desktop webview).
 */

/** Turn a 2-letter ISO-3166 alpha-2 code into its flag emoji. */
export function flagEmoji(iso2: string): string {
  const cc = iso2.trim().toUpperCase();
  if (!/^[A-Z]{2}$/.test(cc)) return '🌐';
  const base = 0x1f1e6; // regional indicator 'A'
  const a = base + (cc.charCodeAt(0) - 65);
  const b = base + (cc.charCodeAt(1) - 65);
  return String.fromCodePoint(a, b);
}

// DXCC entity / QRZ country names → ISO alpha-2. Covers the entities a typical
// operator actually works; the long tail falls back to the globe. Keys are
// lower-cased for a case-insensitive match. A few common ADIF spelling variants
// ("USA", "Great Britain") are aliased to the same code.
const COUNTRY_TO_ISO: Record<string, string> = {
  'united states': 'US', 'united states of america': 'US', usa: 'US', 'u.s.a.': 'US',
  canada: 'CA', mexico: 'MX',
  england: 'GB', scotland: 'GB', wales: 'GB', 'northern ireland': 'GB',
  'united kingdom': 'GB', 'great britain': 'GB',
  ireland: 'IE', france: 'FR', germany: 'DE', spain: 'ES', portugal: 'PT',
  italy: 'IT', netherlands: 'NL', belgium: 'BE', luxembourg: 'LU',
  switzerland: 'CH', austria: 'AT', denmark: 'DK', norway: 'NO', sweden: 'SE',
  finland: 'FI', iceland: 'IS', poland: 'PL', 'czech republic': 'CZ', czechia: 'CZ',
  slovakia: 'SK', hungary: 'HU', romania: 'RO', bulgaria: 'BG', greece: 'GR',
  croatia: 'HR', slovenia: 'SI', serbia: 'RS', ukraine: 'UA', belarus: 'BY',
  'russia': 'RU', 'russian federation': 'RU', 'european russia': 'RU', 'asiatic russia': 'RU',
  estonia: 'EE', latvia: 'LV', lithuania: 'LT', 'north macedonia': 'MK', macedonia: 'MK',
  albania: 'AL', 'bosnia-herzegovina': 'BA', 'bosnia and herzegovina': 'BA',
  montenegro: 'ME', moldova: 'MD', malta: 'MT', 'san marino': 'SM',
  japan: 'JP', china: 'CN', 'south korea': 'KR', 'republic of korea': 'KR',
  'north korea': 'KP', taiwan: 'TW', 'hong kong': 'HK', mongolia: 'MN',
  india: 'IN', pakistan: 'PK', bangladesh: 'BD', 'sri lanka': 'LK', nepal: 'NP',
  thailand: 'TH', vietnam: 'VN', philippines: 'PH', indonesia: 'ID',
  malaysia: 'MY', singapore: 'SG', 'saudi arabia': 'SA', israel: 'IL',
  turkey: 'TR', 'united arab emirates': 'AE', qatar: 'QA', kuwait: 'KW',
  iran: 'IR', iraq: 'IQ', jordan: 'JO', lebanon: 'LB', kazakhstan: 'KZ',
  australia: 'AU', 'new zealand': 'NZ', fiji: 'FJ', 'papua new guinea': 'PG',
  brazil: 'BR', argentina: 'AR', chile: 'CL', uruguay: 'UY', paraguay: 'PY',
  peru: 'PE', colombia: 'CO', venezuela: 'VE', ecuador: 'EC', bolivia: 'BO',
  'costa rica': 'CR', panama: 'PA', guatemala: 'GT', 'el salvador': 'SV',
  honduras: 'HN', nicaragua: 'NI', cuba: 'CU', 'dominican republic': 'DO',
  'puerto rico': 'PR', jamaica: 'JM', 'trinidad and tobago': 'TT', 'trinidad & tobago': 'TT',
  bahamas: 'BS', barbados: 'BB', 'south africa': 'ZA', egypt: 'EG', morocco: 'MA',
  algeria: 'DZ', tunisia: 'TN', libya: 'LY', kenya: 'KE', tanzania: 'TZ',
  nigeria: 'NG', ghana: 'GH', ethiopia: 'ET', 'canary islands': 'IC',
  'azores': 'PT', madeira: 'PT',
};

/**
 * Best-effort flag emoji for a DXCC / QRZ country name. Returns a neutral globe
 * for unknown or empty input so callers can render unconditionally.
 */
export function countryToFlag(country: string | null | undefined): string {
  if (!country) return '🌐';
  const iso = COUNTRY_TO_ISO[country.trim().toLowerCase()];
  return iso ? flagEmoji(iso) : '🌐';
}
