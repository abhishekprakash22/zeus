import { test } from 'node:test';
import assert from 'node:assert/strict';

import { CrashStoreCore } from '../src/crash-store-core.ts';
import { MAX_CRASH_PER_CALLSIGN } from '../src/crash-validate.ts';

class MemoryStorage {
  readonly values = new Map<string, unknown>();

  async get<T = unknown>(key: string): Promise<T | undefined> {
    return this.values.get(key) as T | undefined;
  }

  async put<T = unknown>(key: string, value: T): Promise<void> {
    this.values.set(key, value);
  }

  async delete(key: string | string[]): Promise<void> {
    for (const k of Array.isArray(key) ? key : [key]) {
      this.values.delete(k);
    }
  }

  async transaction<T>(fn: (txn: MemoryStorage) => Promise<T>): Promise<T> {
    return await fn(this);
  }
}

function crashStore(storage: MemoryStorage): CrashStoreCore {
  return new CrashStoreCore(storage);
}

async function json<T>(res: Response): Promise<T> {
  return (await res.json()) as T;
}

test('CrashStore put/list/index persists through storage-backed instances', async () => {
  const storage = new MemoryStorage();
  const largeNote = 'x'.repeat(140_000);
  const put = await crashStore(storage).fetch(
    new Request('https://do/put?callsign=n9war&ip=1.2.3.4&country=us', {
      method: 'POST',
      body: JSON.stringify({ schemaVersion: 1, pid: 4321, note: largeNote }),
    }),
  );
  assert.equal(put.status, 200);
  assert.deepEqual(await json(put), { ok: true, count: 1 });

  const chunkKeys = [...storage.values.keys()].filter((k) => k.startsWith('crash:N9WAR:'));
  assert.ok(chunkKeys.length > 1, 'large crash record should be split across storage chunks');

  const list = await json<{
    callsign: string;
    count: number;
    crashes: Array<{
      receivedAt: number;
      ip: string | null;
      country: string | null;
      record: { pid: number; note: string };
    }>;
  }>(await crashStore(storage).fetch(new Request('https://do/list?callsign=n9war')));
  assert.equal(list.callsign, 'N9WAR');
  assert.equal(list.count, 1);
  assert.equal(list.crashes[0].ip, '1.2.3.4');
  assert.equal(list.crashes[0].country, 'US');
  assert.equal(list.crashes[0].record.pid, 4321);
  assert.equal(list.crashes[0].record.note.length, largeNote.length);

  const index = await json<{
    operators: Array<{ callsign: string; count: number; lastReceivedAt: number; lastIp: string | null }>;
  }>(
    await crashStore(storage).fetch(new Request('https://do/index')),
  );
  assert.deepEqual(index.operators, [{ callsign: 'N9WAR', count: 1, lastReceivedAt: list.crashes[0].receivedAt, lastIp: '1.2.3.4' }]);
});

test('CrashStore cap-at-5 eviction persists and deletes old chunks', async () => {
  const storage = new MemoryStorage();
  const store = crashStore(storage);

  for (let i = 0; i < MAX_CRASH_PER_CALLSIGN + 5; i++) {
    const res = await store.fetch(
      new Request('https://do/put?callsign=kb2uka', {
        method: 'POST',
        body: JSON.stringify({ pid: i }),
      }),
    );
    assert.equal(res.status, 200);
  }

  const list = await json<{ count: number; crashes: Array<{ record: { pid: number } }> }>(
    await crashStore(storage).fetch(new Request('https://do/list?callsign=KB2UKA')),
  );
  assert.equal(list.count, MAX_CRASH_PER_CALLSIGN);
  assert.equal(list.crashes[0].record.pid, MAX_CRASH_PER_CALLSIGN + 4);
  assert.equal(list.crashes[list.crashes.length - 1].record.pid, 5);

  const index = await json<{ operators: Array<{ callsign: string; count: number }> }>(
    await crashStore(storage).fetch(new Request('https://do/index')),
  );
  assert.deepEqual(index.operators.map((o) => [o.callsign, o.count]), [['KB2UKA', MAX_CRASH_PER_CALLSIGN]]);
});

test('CrashStore clear removes one callsign and preserves other operators', async () => {
  const storage = new MemoryStorage();
  const store = crashStore(storage);

  for (const callsign of ['N9WAR', 'N9WAR', 'KB2UKA']) {
    const res = await store.fetch(
      new Request(`https://do/put?callsign=${callsign}`, {
        method: 'POST',
        body: JSON.stringify({ callsign }),
      }),
    );
    assert.equal(res.status, 200);
  }

  const clear = await crashStore(storage).fetch(
    new Request('https://do/clear?callsign=N9WAR', { method: 'DELETE' }),
  );
  assert.equal(clear.status, 200);
  assert.deepEqual(await json(clear), { ok: true, callsign: 'N9WAR', cleared: 2 });
  assert.equal([...storage.values.keys()].some((k) => k.startsWith('crash:N9WAR:')), false);

  const index = await json<{ operators: Array<{ callsign: string; count: number }> }>(
    await crashStore(storage).fetch(new Request('https://do/index')),
  );
  assert.deepEqual(index.operators.map((o) => [o.callsign, o.count]), [['KB2UKA', 1]]);
});

test('CrashStore clear without callsign removes every retained crash log', async () => {
  const storage = new MemoryStorage();
  const store = crashStore(storage);

  for (const callsign of ['N9WAR', 'KB2UKA', 'SA7BXW']) {
    const res = await store.fetch(
      new Request(`https://do/put?callsign=${callsign}`, {
        method: 'POST',
        body: JSON.stringify({ callsign }),
      }),
    );
    assert.equal(res.status, 200);
  }

  const clear = await crashStore(storage).fetch(
    new Request('https://do/clear', { method: 'DELETE' }),
  );
  assert.equal(clear.status, 200);
  assert.deepEqual(await json(clear), { ok: true, callsign: null, cleared: 3 });
  assert.equal([...storage.values.keys()].some((k) => k.startsWith('crash:')), false);

  const index = await json<{ operators: Array<{ callsign: string; count: number }> }>(
    await crashStore(storage).fetch(new Request('https://do/index')),
  );
  assert.deepEqual(index.operators, []);
});
