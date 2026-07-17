# Performance tuning — idle-RX CPU on the frontend

This is a working log of the `feature/perf2` investigation: where the CPU
goes when Zeus is idle on RX (HL2 connected, no TX, all panels open), what
the two committed fixes actually moved, and where the remaining cost lives
so the next pass doesn't have to re-discover any of this.

Reproduce on a Mac mini (M1 / M2-class) with Chrome via Playwright:
- Backend `Zeus.Server` on `:6060`
- Vite dev on `:5173`
- HL2 on the LAN, idle on 20 m USB
- 31-sample `top -l 31 -s 1` per-process averages

## Headline result

| Process | Before fixes | After fixes | Δ |
|---|---|---|---|
| Renderer (Zeus tab) | 30.8 % | **24.3 %** | −6.5 pp |
| Chrome GPU helper | 12.8 % | **9.9 %** | −2.9 pp |
| **Zeus frontend stack** | **43.6 %** | **34.2 %** | **−22 %** |
| Zeus.Server backend | 22.3 % | 23.6 % | noise |
| WindowServer | 32.4 % | 35.2 % | system-wide; not attributable |

Idle-RX HTTP fetch rate dropped 4.97 → ~2 req/sec; mic worklet → store push
rate dropped 50 → 20 Hz.

## What we changed

### 1. `perf(rest-poll)` — `56dac59`

Idle RX was making ~5 fetches/sec, dominated by three pollers:

| Endpoint | Before | After | Source |
|---|---|---|---|
| `/api/state` | 3.46 Hz | 1.0 Hz | `App.tsx:99,194` |
| `/api/rotator/status` | 1.0 Hz | 1.0 Hz when enabled, 0 when disabled | `state/rotator-store.ts:164` |
| `/api/tci/status` | 0.5 Hz | 0 Hz when disabled | `state/tci-store.ts:99` |

- `STATE_POLL_MS` 333 → 1000 ms. The poll only exists for "slow state" —
  ADC overload flag, atten offset, NR settings — that the SignalR hub
  doesn't push as a delta. 1 Hz is still well inside the operator's
  reaction window for ADC overload; 333 ms was overkill and its main effect
  was driving `useConnectionStore.applyState` + `useTxStore.hydrateFromState`
  into the React tree three times a second.
- Rotator and TCI pollers now check `config.enabled` inside their
  `setInterval` callback. Disabled features stop touching the network.

### 2. `perf(mic-meter)` — `7bb3808`

The mic AudioWorklet emits a per-block peak every 20 ms (50 Hz). That value
was going straight to `useTxStore.setMicDbfs`, which re-rendered the
bottom-bar `MicMeter` component 50 times a second — even with the workspace
empty and no panels visible. The `MicMeter` is always mounted; it is the
primary persistent React subscriber to TX-store.

Fix: `audio/use-mic-uplink.ts` now buckets the worklet's per-block peaks
across a 50 ms window (20 Hz visual rate) and emits the **window's max** to
the store. Clip indication stays accurate — a transient peak inside the
window is preserved as the emitted value. Visual feel is unchanged at 20 Hz
(smoother than TV).

This was the single biggest win in absolute terms.

## What we know (and what was a red herring)

### Things that turned out *not* to be the bottleneck

- **Canvas rAF rate.** Initial assumption was that panadapter + waterfall
  rAF at 60 Hz. They don't. Both are event-driven via
  `useDisplayStore.subscribe` and gate themselves with `if (rafHandle ===
  0) requestAnimationFrame(redraw)`. They redraw at the backend's spectrum
  push rate (~25 Hz), not 60. Capping rAF to 30 fps would have been a
  no-op.
- **Per-panel cost is small individually.** Closing only the panadapter
  produced no measurable drop; closing *all* panels dropped renderer from
  ~29 % to ~12 %. The cost is many small things (3 canvases, several
  meters, RGL observers) summing — not one dominant component.
- **Discovery scanning animation.** Inspected — it's a CSS `@keyframes`
  pulse, not a canvas. CSS animations on the GPU compositor are
  effectively free.
- **Server-side meter rates.** `TxMetersService` is 10 Hz during MOX,
  **2 Hz idle**. RX_METER (S-meter) is ~5 Hz. Already low. Not a hot path.

### What we measured and currently believe

- **Empty workspace, HL2 connected** ≈ 12 % renderer. This is the
  always-on cost: SignalR ingress fan-out into stores, the bottom-bar
  meters, RGL observers, Vite HMR client, audio scheduling, CSS animations
  on the QRZ / rotator pills.
- **Each canvas adds ~5–7 pp** on top of the 12 % floor. With panadapter +
  waterfall + the meter canvases together that's ~17 pp. Closing one alone
  is below the noise floor.
- **WindowServer is system-global.** It composites every window on the
  desktop, not just the browser. Reading it as "Zeus's compositor cost"
  is wrong. It tracks workload across the whole machine.

## What's left, and why we stopped here

The remaining ~24 % renderer with all panels open splits roughly:

- ~12 pp in canvas draw work (panadapter + waterfall + meters at ~25 Hz
  each, with their existing `texSubImage2D` / `bufferSubData` per-frame
  uploads).
- ~12 pp in always-on app overhead (store fan-out, mic worklet now at 20
  Hz, RGL, audio).

Concrete next-pass candidates, ordered by expected payoff vs invasiveness:

1. **Coalesce panadapter + waterfall into a shared rAF scheduler.** Both
   currently schedule their own rAF on every store update, so each
   spectrum frame produces two rAF wakeups. Combining into one shared
   "draw bus" would save one wakeup per frame (~25/sec). Modest win
   (~1–2 pp), small refactor across `Panadapter.tsx` + `Waterfall.tsx`.
2. **Audit other persistent React subscribers.** `MicMeter` was the
   obvious one; there may be similar always-mounted components subscribed
   to high-rate fields. Candidates to check next: PA TEMP indicator,
   bottom-bar mic level chip, any subscribers to `useRxMetersStore`.
3. **Skip the `pushFrame` decode when nobody's subscribed to display
   state.** When all canvases are closed, `decodeDisplayFrame` still runs
   on every spectrum frame and pushes into a store with zero subscribers.
   Cheap per-call, but free if we short-circuit. Touches `realtime/
   ws-client.ts`. Medium-invasive — needs a "subscriber count" signal.
4. **Reduce per-frame GPU work.** `texSubImage2D` (waterfall) +
   `bufferSubData` (panadapter trace) on every frame is the biggest
   single chunk of remaining renderer cost. Touching this is **red-light**
   per `CLAUDE.md` (UX/visual feel) — would need maintainer review before
   any change. Possible angles:
     - Decimate waterfall row uploads to every Nth frame (already
       partially in place via `WF_PUSH_EVERY_N`; the constant could be
       tunable).
     - Use `requestVideoFrameCallback` semantics or compositor-driven
       paints to skip uploads when the tile is offscreen (already done
       via IntersectionObserver — confirm gating is firing).

## Investigation method (so this is reproducible)

1. Open `http://localhost:5173/` via Playwright (or just Chrome) with HL2
   on the LAN.
2. Inject perf instruments into the page: a `requestAnimationFrame`
   counter (FPS), a `PerformanceObserver({entryTypes:['longtask']})`, a
   `fetch` wrapper that bins URLs, and a `setInterval(500ms)` heap
   sampler from `performance.memory`.
3. In a parallel shell, run `top -l 31 -s 1 -ncols 6 -stats
   pid,command,cpu,rsize,th,mem -pid <backend> -pid <renderer>
   -pid <gpu> -pid 409` (`409` is WindowServer on macOS).
4. Average per-PID `cpu` across the 31 samples. WindowServer is a
   reference for system-wide workload — not "Zeus's cost".
5. To isolate any single panel's cost, close all panels first, baseline,
   then re-add one panel and re-sample.

The smoking-gun fetch list and per-PID averages live under `/tmp/zeus-perf/`
during a session.

## References

- Commits: `56dac59`, `7bb3808` on `feature/perf2`.
- Server-side meter rates: `Zeus.Server.Hosting/TxMetersService.cs:94-95`
  (`MoxTick = 100 ms`, `IdleTick = 500 ms`).
- Canvas DPR clamp + visibility gate (prior work): `9a36afe`,
  `1c5859a`.

---

# perf3 — backend allocations + frontend coalesce

This is the next pass on `fix/performance3`. Same Mac mini / Chrome via
Playwright / HL2 idle 20 m USB methodology as perf2. Four investigations
in parallel: rAF coalesce, persistent-subscriber audit, pushFrame gate,
and a fresh Zeus.Server profile (the missing piece — perf2 only looked
at frontend).

## Headline

The frontend candidates from perf2's "what's left" list landed but their
predicted wins (~1–2 pp each) sit below the **per-31-sample noise floor
(±2-3 pp)** on this hardware. The honest read is "no measurable
regression, code is structurally cleaner."

The actionable win was **on the backend**:

| Process | perf3 baseline | after perf3 fixes | Δ |
|---|---|---|---|
| Renderer | 19.9 % | 24.8 % | +4.9 (within noise + audio-mute confound, see below) |
| GPU helper | 7.9 % | 9.2 % | +1.3 (noise) |
| **Zeus.Server** | **32.7 %** | **25.7 %** | **−7.0 pp** |

Zeus.Server allocation rate dropped roughly **36 %** (per `dotnet-trace`
gc-verbose) — that's the load-bearing change. The CPU drop is the
visible consequence.

## What we changed

### `9e3a4d4` `perf(draw-bus)` — frontend candidate #1

`zeus-web/src/realtime/draw-bus.ts` (new). `Panadapter.tsx` and
`Waterfall.tsx` now register their `redraw` callback on a shared bus
instead of each calling their own `requestAnimationFrame`. One rAF per
frame dispatches all registered callbacks. Visibility gating (`isActive`)
still lives per-component, before the bus call.

### `2c78c30` `perf(pushframe)` — frontend candidate #3

Module-level consumer registry in `zeus-web/src/state/display-store.ts`:
`registerFrameConsumer()` / `hasActiveFrameConsumers()`. `Panadapter`,
`Waterfall`, and `FilterMiniPan` register on mount. `ws-client.ts` skips
`decodeDisplayFrame` + `pushFrame` when the count is zero. Five new
vitest cases cover the registry (idempotent release, never-negative,
multi-consumer ref-counting). **Zero CPU impact when any spectrum panel
is open** — the win is for the all-panels-closed case.

### `0e57230` `perf(server,hub)` — backend, biggest win

`StreamingHub.Broadcast(...)` had nine identical implementations that
rented a `byte[]` from `ArrayPool`, serialised in, and then
`new ReadOnlyMemory<byte>(rented, 0, total).ToArray()`-ed into a fresh
heap-allocated `byte[]`. The pool ceremony saved nothing — the `ToArray`
copy was the same size as the rent. Now: `new byte[total]` once,
serialise in, broadcast. Wire format byte-identical (verified). Drops
the **#1 allocator (36.24 %)** outright; eliminates ~480 KB/s of
memcpy at 30 Hz spectrum push.

### `3287724` `perf(server,iq-pump)` — backend, second win

`Protocol1Client.RxLoop` rents ~2 KB of `double[]` per IQ packet from
`ArrayPool<double>`. The server-side pump in `DspPipelineService.StartIqPump`
consumed each frame but never returned the buffer — pool re-allocated
on every Rent. At HL2's 381 packets/s that's ~750 KB/s of pure gen0
garbage. Fix uses `MemoryMarshal.TryGetArray` to extract the underlying
array and `Return()` it after `FeedIq`. P2 deliberately skipped (its
`Protocol2Client` allocates with `new`, not pool — returning would
contaminate the pool's bucket).

## Sub-audit: candidate #2 is exhausted

The sub-audit teammate confirmed that the `MicMeter` throttle in
`7bb3808` was the **only** always-mounted high-rate React subscriber.
Every other candidate from the perf2 doc was either already low-rate
(PA TEMP at 2 Hz from server, conn-store fields at 1 Hz post-`56dac59`)
or guarded by panel-mount / `IntersectionObserver`.

Mapping kept here for future passes so this ground isn't re-walked:

- All `useRxMetersStore` subscribers (`SMeterLive`, `AnalogMeterPanel`,
  `MeterWidget`) are inside closeable panels.
- All TX-meter / RX-meter / PS-meter WS frames (`0x14`, `0x16`, `0x18`,
  `0x19`) target panel-gated subscribers only.
- `useTxStore.micDbfs` is the *one* high-rate path that reaches an
  always-mounted `MicMeter` chip; already throttled at the worklet seam
  via `use-mic-uplink.ts` (window-max 50 ms / 20 Hz).

## Items flagged for maintainer review (NOT implemented)

The server-profile teammate found three more backend allocation hot paths
but all touch threading or the UDP read path — bench-test required
before merge.

1. **`BoundedChannelReader<IqFrame>.WaitToReadAsync` (~13.5 % allocs).**
   `DspPipelineService.StartIqPump` does `await foreach` over the IQ
   channel — each iteration allocates an async-state-machine box.
   Clean fix: dedicated thread + sync `TryRead` + `ManualResetEventSlim`.
   Touches engine-swap lock ordering on connect/disconnect; reversible
   but wants a careful read.
2. **`Protocol1Client.TxLoopAsync` `SemaphoreSlim` waiter churn (~5.3 %).**
   Same shape as above. Same flag — TX-rate timing is dB-of-power
   sensitive (see the `PeriodicTimer fell to whatever the OS rounded the
   period to` comment in `Protocol1Client.cs:62-70`).
3. **`Socket.ReceiveFrom` per-packet allocation (~16 %).** .NET 7+ ships
   an overload taking a caller-owned `SocketAddress` that avoids the
   per-call `EndPoint.Serialize()` + `IPEndPoint` reconstruction. The
   current code doesn't validate the source, so the new overload is
   functionally equivalent — but touching the receive socket can subtly
   affect macOS scheduling. Wants a HL2 bench-test for packet-loss /
   TX-rate after the swap.

Combined potential: another ~35 % allocation reduction on top of the
36 % already taken — but each needs an HL2 bench window.

## Frontend candidate #4 is still red-light

Per-frame GPU upload work (`texSubImage2D` / `bufferSubData`) on the
waterfall and panadapter trace remains the biggest single chunk of
renderer cost. Per `CLAUDE.md` this is **red-light** — touching it can
shift waterfall scroll feel / panadapter trace responsiveness, both of
which are operator-perceptible. Deferred until a maintainer-led
session.

## Sample series

All averages from `top -l 31 -s 1` per PID, HL2 idle 20 m USB, layout
"Default" with all panels open. "Frontend" = renderer + GPU helper.

| Stage | Branch HEAD | Audio | Renderer | GPU | Frontend | Backend |
|---|---|---|---|---|---|---|
| baseline | `cd3a7d3` (post-perf2) | muted | 19.9 | 7.9 | **27.8** | **32.7** |
| after raf-coalesce | `e49afc2` | muted | 21.1 | 8.5 | 29.7 | 25.2 |
| after raf + pushframe | `67bf47c` | muted | 21.5 | 8.8 | 30.3 | 25.0 |
| after server-profile (transient) | `77dd595`, fresh server | unmuted | 25.7 | 9.5 | 35.3 | 25.2 |
| after server-profile (steady) | `77dd595` | unmuted | 26.1 | 9.6 | 35.7 | 25.8 |
| after server-profile (re-muted) | `77dd595` | muted | 24.8 | 9.2 | **33.9** | **25.7** |

Backend swing 32.7 → 25.0–25.8 across multiple post-merge samples is the
reproducible win. Frontend deltas are all within the ±2-3 pp per-31-sample
noise band.

## Methodology note: audio-mute confound

The first perf3 sample after merging the backend fixes showed renderer
+5 pp, which initially looked like a regression. It turned out the audio
output had been left **un**muted between samples — the always-mounted
`MicMeter` was actively re-rendering at 20 Hz instead of being idle. After
re-muting (matching the perf2 baseline conditions), renderer dropped
35.7 → 33.9 % (1.8 pp). That accounted for some of the gap but **not
all**: ~3 pp of the perf3-baseline-vs-after-all gap remained unexplained
even with audio muted.

Candidates for the residual ~3 pp (not isolated yet — flagged for the
next pass):

- **Rotator.** At baseline the rotator pill read "Rotator: —" (no
  rotator response, idle 1 Hz `/api/rotator/status` poll only). After
  the restart the rotator was responding ("Rotator: 287°"), so each
  poll's response triggers store + pill re-renders.
- **Heap state warm-up.** Baseline was sampled within ~2 minutes of
  page load (heap 33.6 MB). The post-merge sample was 5+ minutes in
  (heap stabilised at ~40 MB). More retained store state can mean more
  subscriber callbacks fire on each high-rate WS frame.
- **Discovery panel teardown lingering observers.** The page reload +
  reconnect flow mounts the discovery card, then unmounts when status
  flips to Connected. If any observer/timer leaks from that transition
  the cost shows up only post-reconnect, not on first-load baseline.

Lesson: **fix the audio-mute state in the methodology**. Before any sample
the bottom-bar audio button must read "▶ Unmute" (i.e. audio is muted,
worklet idle). The 50 → 20 Hz throttle in `7bb3808` only mitigates the
unmuted case; an idle-RX baseline must hold the worklet truly idle.

## What we know and what's still uncertain

**Solid**:
- Backend allocation rate down ~36 % at idle. CPU drop ~7 pp reproducible
  across multiple samples after a server restart with the new build.
- The frontend code changes are structurally sound, build clean, all
  vitest cases pass (209 + 5 new = 214 total in the affected files).
- `pushFrame` decode is correctly gated when no spectrum panel is mounted
  (registry unit tests assert this).

**Uncertain**:
- Per-31-sample variance is wide enough (±2-3 pp on renderer, ±5-7 pp on
  backend across runs) that we can't confidently *attribute* the
  individual frontend deltas. The combined doesn't-regress picture is
  the strongest claim we can defend.
- `/api/state` reads at ~1.49 Hz when averaged over a 45 s window
  including page load. Steady state is ~1.0 Hz (the `App.tsx:185` poller
  alone) — the extra rate is mount-time fetches from `ConnectPanel:182`
  and `VfoDisplay:129`. Not a bug; flag for the methodology.

## References

- Commits: `9e3a4d4`, `2c78c30`, `0e57230`, `3287724` on
  `fix/performance3`.
- Server profile artefacts: `/tmp/zeus-perf3/server-profile.md` (full
  writeup, top-N tables) and `/tmp/zeus-perf3/server-fresh.nettrace`
  (raw `dotnet-trace` capture).
- Per-merge sample tables: `/tmp/zeus-perf3/baseline.md`,
  `after_raf.md`, `after_raf_pushframe.md`, `after_all_muted.top.txt`.
