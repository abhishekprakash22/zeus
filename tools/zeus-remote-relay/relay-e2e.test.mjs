// E2E: fake radio host + fake browser client through the real relay.
import { WebSocket } from 'ws';
const relay = 'ws://127.0.0.1:8787';
// fake radio host (mirrors RemoteBrokerClient: answers any offer)
const host = new WebSocket(`${relay}/signal?role=host&callsign=VU2XYZ`);
host.on('message', (d) => {
  const m = JSON.parse(d.toString());
  if (m.t === 'offer') host.send(JSON.stringify({ t: 'answer', sdp: 'ANSWER-SDP-FOR:' + m.sdp }));
});
await new Promise((r) => host.on('open', r));
// fake browser client (mirrors brokerSignal)
const answer = await new Promise((resolve, reject) => {
  const c = new WebSocket(`${relay}/signal?role=client&callsign=vu2xyz`);
  const t = setTimeout(() => reject(new Error('timeout')), 8000);
  c.on('open', () => c.send(JSON.stringify({ t: 'offer', sdp: 'OFFER-1' })));
  c.on('message', (d) => { const m = JSON.parse(d.toString());
    if (m.t === 'answer') { clearTimeout(t); resolve(m.sdp); }
    if (m.t === 'offline') { clearTimeout(t); reject(new Error('offline')); } });
});
console.log('CASE1 answer routed:', answer === 'ANSWER-SDP-FOR:OFFER-1' ? 'PASS' : 'FAIL ' + answer);
// offline case
const off = await new Promise((resolve) => {
  const c = new WebSocket(`${relay}/signal?role=client&callsign=NOSUCH`);
  c.on('open', () => c.send(JSON.stringify({ t: 'offer', sdp: 'X' })));
  c.on('message', (d) => resolve(JSON.parse(d.toString()).t));
});
console.log('CASE2 offline:', off === 'offline' ? 'PASS' : 'FAIL');
host.close(); process.exit(0);
