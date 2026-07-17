// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Licensed under the GNU General Public License v2.0-or-later. See the LICENSE
// file at the repository root for the full text.

import { useEffect, useMemo, useState, type HTMLAttributes } from 'react';
import type { LogEntry, UpdateLogEntryRequest } from '../../api/log';
import { useOperatorStore } from '../../state/operator-store';
import { useLoggerStore } from '../../state/logger-store';
import { countryToFlag } from './logbook-flag';
import { isQrzPublished } from './LogbookLive';

// The station-detail card for the popped-out Logbook workspace: the full read of
// the QSO focused in the table. Every value shown here is a real logged field.

function fmtLongDateUtc(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  return d.toLocaleDateString([], { month: 'short', day: 'numeric', year: 'numeric', timeZone: 'UTC' });
}

function fmtTimeUtc(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false, timeZone: 'UTC' });
}

function fmtStampUtc(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  return `${fmtLongDateUtc(iso)} ${fmtTimeUtc(iso)}`;
}

function fmtFreq(freq: number | null | undefined): string {
  return typeof freq === 'number' && Number.isFinite(freq) ? freq.toFixed(3) : '—';
}

function orDash(v: string | null | undefined): string {
  return v && v.trim() ? v : '—';
}

function Field({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="lbd-field">
      <span className="lbd-field-label">{label}</span>
      <span className={`lbd-field-value${mono ? ' mono' : ''}`}>{value}</span>
    </div>
  );
}

function qslLabel(value: string | null | undefined): string {
  const v = (value ?? 'N').toUpperCase();
  if (v === 'Y') return 'Yes';
  if (v === 'R') return 'Requested';
  if (v === 'Q') return 'Queued';
  if (v === 'I') return 'Ignored';
  return 'No';
}

function nextQsl(value: string | null | undefined): string {
  const v = (value ?? 'N').toUpperCase();
  if (v === 'N') return 'Y';
  if (v === 'Y') return 'R';
  return 'N';
}

function isoDateOnly(value: string | null | undefined): string | null {
  if (!value) return null;
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return null;
  return d.toISOString().slice(0, 10);
}

function todayUtcDate(): string {
  return new Date().toISOString().slice(0, 10);
}

function InlineText({
  label,
  value,
  enabled,
  onSave,
  inputMode,
}: {
  label: string;
  value: string | number | null | undefined;
  enabled: boolean;
  onSave: (value: string) => void;
  inputMode?: HTMLAttributes<HTMLInputElement>['inputMode'];
}) {
  const [draft, setDraft] = useState(value == null ? '' : String(value));
  useEffect(() => setDraft(value == null ? '' : String(value)), [value]);
  const commit = () => {
    if (!enabled) return;
    const next = draft.trim();
    if (next !== (value == null ? '' : String(value).trim())) onSave(next);
  };
  return (
    <label className="lbd-inline-field">
      <span className="lbd-field-label">{label}</span>
      {enabled ? (
        <input
          value={draft}
          inputMode={inputMode}
          onChange={(event) => setDraft(event.target.value)}
          onBlur={commit}
          onKeyDown={(event) => {
            if (event.key === 'Enter') event.currentTarget.blur();
            if (event.key === 'Escape') {
              setDraft(value == null ? '' : String(value));
              event.currentTarget.blur();
            }
          }}
        />
      ) : (
        <span className="lbd-field-value">{orDash(value == null ? null : String(value))}</span>
      )}
    </label>
  );
}

export function LogbookDetail({ entry }: { entry: LogEntry | null }) {
  const operatorCall = useOperatorStore((s) => s.resolvedCall);
  const capabilities = useLoggerStore((s) => s.capabilities);
  const tagList = useLoggerStore((s) => s.tagList);
  const updateEntry = useLoggerStore((s) => s.updateEntry);
  const canEdit = Boolean(capabilities?.canEdit);
  const canTags = Boolean(capabilities?.canTags);
  const canQsl = Boolean(capabilities?.canQsl);
  const [editingNote, setEditingNote] = useState(false);
  const [noteDraft, setNoteDraft] = useState('');
  const [tagDraft, setTagDraft] = useState('');
  const tags = useMemo(() => entry?.tags ?? [], [entry?.tags]);
  const tagOptions = useMemo(() => {
    const used = new Set(tags.map((tag) => tag.toLowerCase()));
    const q = tagDraft.trim().toLowerCase();
    return tagList
      .filter((tag) => !used.has(tag.toLowerCase()))
      .filter((tag) => !q || tag.toLowerCase().includes(q))
      .slice(0, 6);
  }, [tagDraft, tagList, tags]);

  useEffect(() => {
    setEditingNote(false);
    setNoteDraft(entry?.comment ?? '');
    setTagDraft('');
  }, [entry?.id, entry?.comment]);

  if (!entry) {
    return (
      <div className="lbd lbd--empty">
        <div className="lbd-empty-glyph" aria-hidden="true">◎</div>
        <div className="lbd-empty-copy">Select a QSO to see full station detail.</div>
      </div>
    );
  }

  const published = isQrzPublished(entry);
  const location = [entry.grid, entry.state, entry.country].filter(Boolean).join(' · ') || '—';
  const qrzUrl = `https://www.qrz.com/db/${encodeURIComponent(entry.callsign)}`;
  const save = (patch: UpdateLogEntryRequest) => void updateEntry(entry.id, patch);
  const commitNote = () => {
    const next = noteDraft.trim();
    if (next !== (entry.comment ?? '').trim()) save({ comment: next });
    setEditingNote(false);
  };
  const addTag = (raw: string) => {
    const tag = raw.trim();
    if (!tag || tags.some((t) => t.toLowerCase() === tag.toLowerCase())) return;
    save({ tags: [...tags, tag] });
    setTagDraft('');
  };
  const removeTag = (tag: string) => save({ tags: tags.filter((t) => t !== tag) });
  const patchQsl = (field: 'qslSent' | 'qslRcvd', value: string | null | undefined) => {
    const next = nextQsl(value);
    const dateField = field === 'qslSent' ? 'qslSentDate' : 'qslRcvdDate';
    const clearField = field === 'qslSent' ? 'clearQslSentDate' : 'clearQslRcvdDate';
    save({
      [field]: next,
      ...(next === 'Y' ? { [dateField]: todayUtcDate() } : { [clearField]: true }),
    } as UpdateLogEntryRequest);
  };

  return (
    <div className="lbd">
      <div className="lbd-head">
        <div className="lbd-call">{entry.callsign}</div>
        {published && <span className="lbd-qrz-pill">✓ QRZ</span>}
      </div>

      <div className="lbd-station">
        <span className="lbd-flag" aria-hidden="true">{countryToFlag(entry.country)}</span>
        <div className="lbd-station-text">
          <div className="lbd-name">{orDash(entry.name)}</div>
          <div className="lbd-loc">{location}</div>
        </div>
      </div>

      <div className="lbd-primary">
        <div className="lbd-primary-fields">
          <Field label="Date" value={fmtLongDateUtc(entry.qsoDateTimeUtc)} mono />
          <Field label="Time (UTC)" value={`${fmtTimeUtc(entry.qsoDateTimeUtc)}Z`} mono />
          <Field label="Mode" value={orDash(entry.mode)} mono />
          <Field label="RST" value={`${orDash(entry.rstSent)} / ${orDash(entry.rstRcvd)}`} mono />
        </div>
        <div className="lbd-freq">
          <span className="lbd-freq-val mono">{fmtFreq(entry.frequencyMhz)}</span>
          <span className="lbd-freq-band mono">{orDash(entry.band)}</span>
        </div>
      </div>

      <div className="lbd-section">
        <div className="lbd-section-label">Notes</div>
        {editingNote && canEdit ? (
          <textarea
            className="lbd-note-edit"
            value={noteDraft}
            autoFocus
            onChange={(event) => setNoteDraft(event.target.value)}
            onBlur={commitNote}
            onKeyDown={(event) => {
              if ((event.metaKey || event.ctrlKey) && event.key === 'Enter') event.currentTarget.blur();
              if (event.key === 'Escape') {
                setNoteDraft(entry.comment ?? '');
                setEditingNote(false);
              }
            }}
          />
        ) : (
          <button
            type="button"
            className={`lbd-note${entry.comment ? '' : ' lbd-note--empty'} ${canEdit ? 'is-editable' : ''}`}
            disabled={!canEdit}
            onClick={() => canEdit && setEditingNote(true)}
          >
            {entry.comment || 'No note recorded'}
          </button>
        )}
      </div>

      <div className="lbd-section">
        <div className="lbd-section-label">Tags</div>
        {canTags ? (
          <div className="lbd-tag-editor">
            <div className="lbd-tags">
              {tags.length === 0 && <span className="lbd-tags-empty">No tags</span>}
              {tags.map((tag) => (
                <span key={tag} className="lbd-tag">
                  {tag}
                  <button type="button" onClick={() => removeTag(tag)} aria-label={`Remove ${tag}`}>
                    ×
                  </button>
                </span>
              ))}
            </div>
            <input
              value={tagDraft}
              onChange={(event) => setTagDraft(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter') {
                  event.preventDefault();
                  addTag(tagDraft);
                }
              }}
              placeholder="Add tag"
              list="logbook-known-tags"
            />
            <datalist id="logbook-known-tags">
              {tagOptions.map((tag) => <option key={tag} value={tag} />)}
            </datalist>
          </div>
        ) : (
          <div className="lbd-tags-empty" title="Update the Logbook plugin to enable QSO tags">
            Tagging coming soon
          </div>
        )}
      </div>

      <div className="lbd-section">
        <div className="lbd-section-label">QSL Info</div>
        <div className="lbd-grid2">
          <button
            type="button"
            className="lbd-qsl-toggle"
            disabled={!canQsl}
            onClick={() => patchQsl('qslSent', entry.qslSent)}
          >
            <span>Paper Sent</span>
            <strong>{qslLabel(entry.qslSent)}</strong>
            <small>{isoDateOnly(entry.qslSentDate) ?? '—'}</small>
          </button>
          <button
            type="button"
            className="lbd-qsl-toggle"
            disabled={!canQsl}
            onClick={() => patchQsl('qslRcvd', entry.qslRcvd)}
          >
            <span>Paper Rcvd</span>
            <strong>{qslLabel(entry.qslRcvd)}</strong>
            <small>{isoDateOnly(entry.qslRcvdDate) ?? '—'}</small>
          </button>
          <Field label="LoTW Sent" value={fmtStampUtc(entry.lotwQslSentUtc)} mono />
          <Field label="LoTW Rcvd" value={fmtStampUtc(entry.lotwQslRcvdUtc)} mono />
          <Field label="QRZ Rcvd" value={fmtStampUtc(entry.qrzQslRcvdUtc)} mono />
          <InlineText label="Rig" value={entry.rig} enabled={canEdit} onSave={(value) => save({ rig: value })} />
          <InlineText label="Antenna" value={entry.antenna} enabled={canEdit} onSave={(value) => save({ antenna: value })} />
          <InlineText
            label="Power W"
            value={entry.txPowerW}
            enabled={canEdit}
            inputMode="decimal"
            onSave={(value) => {
              const parsed = Number(value);
              save(value ? { txPowerW: Number.isFinite(parsed) ? parsed : entry.txPowerW } : { clearTxPowerW: true });
            }}
          />
        </div>
      </div>

      <div className="lbd-section">
        <div className="lbd-grid3">
          <Field label="Grid" value={orDash(entry.grid)} mono />
          <Field label="ITU Zone" value={entry.ituZone != null ? String(entry.ituZone) : '—'} mono />
          <Field label="CQ Zone" value={entry.cqZone != null ? String(entry.cqZone) : '—'} mono />
        </div>
      </div>

      <div className="lbd-section">
        <div className="lbd-grid2">
          <Field label="Operator" value={orDash(operatorCall)} mono />
          <Field label="Source" value="Zeus Logbook" />
          <Field label="Added" value={fmtStampUtc(entry.createdUtc)} mono />
          <Field label="Country" value={orDash(entry.country)} />
        </div>
      </div>

      <div className="lbd-foot">
        <a className="lbd-qrz-link mono" href={qrzUrl} target="_blank" rel="noreferrer">
          QRZ.COM ↗
        </a>
      </div>
    </div>
  );
}
