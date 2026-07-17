import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fetchSupportIceServers } from '../src/session.js';

test('fetchSupportIceServers posts to broker /turn and returns iceServers', async () => {
  let seenUrl = '';
  let seenMethod = '';
  const fetchFn = async (url: string | URL | Request, init?: RequestInit): Promise<Response> => {
    seenUrl = String(url);
    seenMethod = init?.method ?? '';
    return Response.json({
      iceServers: [
        { urls: ['stun:stun.cloudflare.com:3478'] },
        {
          urls: [
            'turn:turn.example.test:3478?transport=udp',
            'turn:turn.example.test:80?transport=tcp',
            'turns:turn.example.test:443?transport=tcp',
          ],
          username: 'u',
          credential: 'c',
        },
      ],
    });
  };

  const servers = await fetchSupportIceServers('wss://remote.example.test', { fetchFn });

  assert.equal(seenUrl, 'https://remote.example.test/turn');
  assert.equal(seenMethod, 'POST');
  // TLS/TCP TURN entries are filtered out (matching the host-side parser).
  assert.equal(servers.length, 2);
  assert.deepEqual(servers[1], {
    urls: 'turn:turn.example.test:3478?transport=udp',
    username: 'u',
    credential: 'c',
  });
});

test('fetchSupportIceServers falls back to legacy STUN on errors and non-2xx', async () => {
  const failed = await fetchSupportIceServers('ws://127.0.0.1:8787/', {
    fetchFn: async () => new Response('nope', { status: 503 }),
  });
  assert.deepEqual(failed, [{ urls: 'stun:stun.cloudflare.com:3478' }]);

  const thrown = await fetchSupportIceServers('ws://127.0.0.1:8787', {
    fetchFn: async () => {
      throw new Error('offline');
    },
  });
  assert.deepEqual(thrown, [{ urls: 'stun:stun.cloudflare.com:3478' }]);
});
