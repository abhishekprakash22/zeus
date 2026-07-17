import { MAX_CRASH_PER_CALLSIGN } from './crash-validate';

/**
 * Storage-backed crash record implementation shared by the Durable Object wrapper
 * and Node tests. No Cloudflare runtime imports live here.
 */

interface CrashIndexEntry {
  id: string;
  receivedAt: number;
  ip: string | null;
  country: string | null;
  chunks: number;
}

interface OperatorCrashSummary {
  callsign: string;
  count: number;
  lastReceivedAt: number;
  lastIp: string | null;
}

type CrashStorage = {
  get<T = unknown>(key: string): Promise<T | undefined>;
  put<T = unknown>(key: string, value: T): Promise<void>;
  delete(key: string | string[]): Promise<unknown>;
};

export type CrashRootStorage = CrashStorage & {
  transaction<T>(fn: (txn: CrashStorage) => Promise<T>): Promise<T>;
};

const OPERATOR_INDEX_KEY = 'crashes:operators';
const RECORD_CHUNK_BYTES = 60 * 1024;

export class CrashStoreCore {
  constructor(private readonly storage: CrashRootStorage) {}

  async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);
    const action = url.pathname.replace(/^\/+/, '');
    const callsign = (url.searchParams.get('callsign') ?? '').trim().toUpperCase();
    const now = Date.now();

    switch (action) {
      case 'put': {
        if (!callsign) return new Response('callsign required', { status: 400 });
        let record: unknown;
        try {
          record = await request.json();
        } catch {
          return new Response('invalid json', { status: 400 });
        }
        const ip = (url.searchParams.get('ip') ?? '').trim() || null;
        const country = (url.searchParams.get('country') ?? '').trim().toUpperCase() || null;
        let count = 0;
        await this.storage.transaction(async (txn) => {
          const index = await loadCrashIndex(txn, callsign);
          const id = `${now}-${crypto.randomUUID()}`;
          const chunks = encodeRecord(record);
          for (let i = 0; i < chunks.length; i++) {
            await txn.put(recordChunkKey(callsign, id, i), chunks[i]);
          }

          index.push({ id, receivedAt: now, ip, country, chunks: chunks.length });
          const evicted: CrashIndexEntry[] = [];
          while (index.length > MAX_CRASH_PER_CALLSIGN) {
            const stale = index.shift();
            if (stale) evicted.push(stale);
          }
          for (const stale of evicted) {
            await deleteRecordChunks(txn, callsign, stale);
          }

          await txn.put(crashIndexKey(callsign), index);
          await saveOperatorSummary(txn, callsign, index);
          count = index.length;
        });
        return Response.json({ ok: true, count });
      }
      case 'list': {
        if (!callsign) return new Response('callsign required', { status: 400 });
        const index = await loadCrashIndex(this.storage, callsign);
        const crashes = await Promise.all([...index]
          .reverse()
          .map(async (e) => ({
            receivedAt: e.receivedAt,
            ip: e.ip,
            country: e.country,
            record: await readRecord(this.storage, callsign, e),
          })));
        return Response.json({ callsign, count: crashes.length, crashes });
      }
      case 'index': {
        const operators = (await loadOperatorIndex(this.storage))
          .filter((o) => o.count > 0)
          .sort((a, b) => b.lastReceivedAt - a.lastReceivedAt);
        return Response.json({ operators });
      }
      case 'clear': {
        let cleared = 0;
        await this.storage.transaction(async (txn) => {
          cleared = callsign
            ? await clearOperatorCrashes(txn, callsign)
            : await clearAllCrashes(txn);
        });
        return Response.json({ ok: true, callsign: callsign || null, cleared });
      }
      default:
        return new Response('not found', { status: 404 });
    }
  }
}

function crashIndexKey(callsign: string): string {
  return `crashes:${callsign}`;
}

function recordChunkKey(callsign: string, id: string, chunk: number): string {
  return `crash:${callsign}:${id}:${chunk}`;
}

async function loadCrashIndex(storage: CrashStorage, callsign: string): Promise<CrashIndexEntry[]> {
  return (await storage.get<CrashIndexEntry[]>(crashIndexKey(callsign))) ?? [];
}

async function loadOperatorIndex(storage: CrashStorage): Promise<OperatorCrashSummary[]> {
  return (await storage.get<OperatorCrashSummary[]>(OPERATOR_INDEX_KEY)) ?? [];
}

async function clearOperatorCrashes(storage: CrashStorage, callsign: string): Promise<number> {
  const index = await loadCrashIndex(storage, callsign);
  for (const entry of index) {
    await deleteRecordChunks(storage, callsign, entry);
  }
  await storage.delete(crashIndexKey(callsign));
  const operators = (await loadOperatorIndex(storage)).filter((o) => o.callsign !== callsign);
  await storage.put(OPERATOR_INDEX_KEY, operators);
  return index.length;
}

async function clearAllCrashes(storage: CrashStorage): Promise<number> {
  const operators = await loadOperatorIndex(storage);
  let cleared = 0;
  for (const op of operators) {
    const callsign = (op.callsign ?? '').trim().toUpperCase();
    if (callsign) cleared += await clearOperatorCrashes(storage, callsign);
  }
  await storage.put(OPERATOR_INDEX_KEY, []);
  return cleared;
}

async function saveOperatorSummary(
  storage: CrashStorage,
  callsign: string,
  index: CrashIndexEntry[],
): Promise<void> {
  let operators = await loadOperatorIndex(storage);
  operators = operators.filter((o) => o.callsign !== callsign);
  const last = index[index.length - 1];
  if (last) {
    operators.push({
      callsign,
      count: index.length,
      lastReceivedAt: last.receivedAt,
      lastIp: last.ip,
    });
  }
  await storage.put(OPERATOR_INDEX_KEY, operators);
}

function encodeRecord(record: unknown): ArrayBuffer[] {
  const bytes = new TextEncoder().encode(JSON.stringify(record));
  const chunks: ArrayBuffer[] = [];
  for (let offset = 0; offset < bytes.byteLength; offset += RECORD_CHUNK_BYTES) {
    const slice = bytes.slice(offset, offset + RECORD_CHUNK_BYTES);
    chunks.push(slice.buffer.slice(slice.byteOffset, slice.byteOffset + slice.byteLength));
  }
  return chunks;
}

async function readRecord(
  storage: CrashStorage,
  callsign: string,
  entry: CrashIndexEntry,
): Promise<unknown> {
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (let i = 0; i < entry.chunks; i++) {
    const stored = await storage.get<ArrayBuffer>(recordChunkKey(callsign, entry.id, i));
    if (!stored) return null;
    const bytes = new Uint8Array(stored);
    chunks.push(bytes);
    total += bytes.byteLength;
  }

  const all = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    all.set(chunk, offset);
    offset += chunk.byteLength;
  }

  try {
    return JSON.parse(new TextDecoder().decode(all));
  } catch {
    return null;
  }
}

async function deleteRecordChunks(
  storage: CrashStorage,
  callsign: string,
  entry: CrashIndexEntry,
): Promise<void> {
  const keys: string[] = [];
  for (let i = 0; i < entry.chunks; i++) {
    keys.push(recordChunkKey(callsign, entry.id, i));
  }
  if (keys.length > 0) await storage.delete(keys);
}
