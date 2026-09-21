# Contributing to ANAN Core

Welcome — and thanks for thinking about contributing. ANAN Core is built by
hams, for hams. We move at a measured pace and we like the code to be honest
about what it does. If you're here to scratch a real itch on your own rig,
you're in the right place.

Before you write any code, please skim this whole page. It's short. Most of
what's here is the actual mental model used day-to-day, not ceremonial gates.

---

## What ANAN Core is, in one paragraph

ANAN Core is Apache Labs' station software for the ANAN G2 family, built
specifically for the **ANAN G2 Ultra** — its Raspberry Pi 5, 8-inch
1280×800 front panel, dual phase-coherent ADCs, and Protocol-2 / Saturn
architecture are the reference platform for every feature and every fix.
The backend is .NET (`Zeus.Server*` / `OpenhpsdrZeus`), the frontend is
Vite + React (`zeus-web/`), and the DSP engine is WDSP, loaded via
P/Invoke. **Thetis is the authoritative reference for protocol and DSP
behaviour** — when this code and Thetis disagree, Thetis is right by
default unless there's a documented reason otherwise (see `docs/lessons/`).

Other OpenHPSDR radios (Hermes-Lite 2, Protocol-1 boards, earlier ANANs)
generally keep working, but the G2 Ultra is where this project is aimed,
tuned, and bench-tested.

---

## Before you start

1. **Open an issue first** for anything bigger than a typo. The issue is
   where we agree on the *what* and the *shape*; the PR is where we agree
   on the *details*. Drive-by feature PRs without prior discussion get
   sent back through the tracker.

2. **Read [`CLAUDE.md`](CLAUDE.md)** at the repo root. It's nominally for
   AI coding agents, but it's the same standards every human contributor
   follows too.

3. **Don't touch the hot paths** (below) without explicit approval in an
   issue — even for what looks like unrelated cleanup.

---

## Setting up

```bash
# Clone WITH submodules — the DeepCW model
# (zeus-web/external/deepcw-engine) lives in a submodule and the web
# build fails without it.
git clone --recurse-submodules https://github.com/abhishekprakash22/zeus.git
# Already cloned without it? Run:
git submodule update --init --recursive

# Backend (listens on :6060). OpenhpsdrZeus is the executable host;
# Zeus.Server.Hosting next to it is a class library — don't pass it to
# dotnet run.
dotnet run --project OpenhpsdrZeus

# Frontend (Vite dev server on :5173, proxies /api and /ws to :6060;
# `vite build` writes the production bundle into Zeus.Server.Hosting/wwwroot)
npm --prefix zeus-web run dev
```

The native WDSP library is rebuilt by CI on tagged releases; for local dev
the binaries in `Zeus.Dsp/runtimes/<rid>/native/` are good enough. See
`native/` if you want to build WDSP from source.

---

## The contribution flow

1. **Fork** `abhishekprakash22/zeus` to your own GitHub account.
2. **Branch** from `freedv-in-core` on your fork. Name it something
   descriptive: `your-handle/cw-apf-filter`, `your-handle/fix-zoom-overflow`.
3. **Build clean** before opening the PR:
   - `dotnet build Zeus.slnx` → 0 warnings, 0 errors; `dotnet test Zeus.slnx`.
   - `npm --prefix zeus-web run build` must succeed for any frontend change.
   - A small set of pre-existing frontend test failures in the `setZoom`
     family is known; anything *else* failing is on your change.
4. **Open the PR against `freedv-in-core`** on `abhishekprakash22/zeus`.
5. **Wait for review.** Apache Labs reviews all code; reviews are usually
   within a day or two.
6. **Don't merge your own PR.** Reviewer merges after approval.

---

## The branch model

| Branch | What it is | When you push to it |
|---|---|---|
| `freedv-in-core` | Default branch **and** release branch. Everything lands here via PR. Stays green and shippable. | Always, via PR |
| `wdsp-2.1.0*` | Historical work branches for the WDSP 2.10 port. | Never |
| `main` | Historical; not part of the release flow. | Never |
| `v1.NN` tags | What end-users actually run, built by CI. | Never |

**Always target `freedv-in-core` with your PR.**

---

## Hot paths

Regions of the codebase where shipped behaviour took significant on-air
investigation to land correctly. **Do not modify these without explicit
approval in an issue:**

- **PureSignal pipeline** — `Zeus.Dsp/Wdsp/WdspDspEngine.cs` (PS-related
  methods), `Zeus.Server.Hosting/PsAutoAttenuateService.cs`, and the PS
  paths in `Zeus.Server.Hosting/DspPipelineService.cs`. PureSignal
  calibration semantics are frozen; changes need a documented plan and
  maintainer sign-off.
- **The updater / release contract** — release tags are two-part `v1.NN`
  and compare numerically; the shipped AppImage filename pattern
  (`OpenhpsdrZeus-*`) is a self-updater path contract. Don't rename
  artifacts or restructure `RepoUpdateService` version handling without
  an issue.
- **Remote access authentication** — the SPAKE2+ password model in
  `Remote/` is deny-by-default by design. Security-relevant; see
  `SECURITY.md` for reporting rather than public issues.

Load-bearing invariants also live in `docs/lessons/` — read the relevant
lesson before touching DSP, protocol, or layout code it covers.

---

## Code expectations

- **One feature per PR.** Smaller PRs land faster and revert cleaner.
- **Match existing patterns.** Grep for a recent similar feature and
  follow its wiring shape.
- **Don't add features, refactoring, or abstractions beyond what the task
  requires.** A bug fix doesn't need surrounding cleanup.
- **Validate at system boundaries** (user input, external APIs); trust
  internal code.
- **Default to no comments.** Add one only when the *why* is non-obvious.
- **No backwards-compatibility shims** for things that aren't a problem
  yet. If something's unused, delete it.

---

## Commit messages

- Subject line under 72 chars; body wrapped at ~80.
- Tell the *story* of the change, not the tools you used to make it.
  **Never mention Anthropic, Claude, or any AI assistant** in commit
  messages (see CLAUDE.md — hard rule).
- Reference the issue if there is one: `fix(...): description (#NNN)`.
- No `--no-verify`, no skipping hooks.

---

## On-air testing

Some bugs only show up on real hardware. If your contribution is in the
TX/RX/DSP/PA path, it's *strongly* preferred that you (or the maintainer)
can verify the change on a real radio before merge — the reference rig is
an ANAN G2 Ultra. Mention your test rig in the PR description; "couldn't
bench test, please verify on the G2 before merge" saves a review round
trip. Pure backend wiring, build fixes, and docs don't need this.

---

## Releases

Releases are maintainer-run: a two-part `v1.NN` tag on `freedv-in-core`
triggers CI, which assembles a draft GitHub Release (arm64 AppImages +
updater manifest); the draft is reviewed and then published via the
`release-publish` workflow. A tag is spent the moment CI has built it — a
corrected commit always gets a new number.

---

## Code of conduct

Be kind, be specific, assume good faith. We expect technical
disagreement; we don't expect personal sharpness. The maintainer reserves
the right to ask for changes or close PRs that don't fit the project's
direction; that's stewardship, not personal.

---

## Heritage

ANAN Core is Apache Labs' independently maintained derivative of an
earlier GPL-licensed version of OpenHPSDR Zeus, created by Brian Keating
(EI6LF), Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and Ramón
Martínez (EA5IUE). See `ATTRIBUTIONS.md` for the full provenance
statement. ANAN Core is separate from the current ZeusSDR project.

---

## License

By contributing you agree your contributions are licensed under **GNU GPL
v2 or later**, the same as the rest of the tree. See [LICENSE](LICENSE)
for the full text.

---

73 and welcome aboard.

— Abhi Prakash, Apache Labs
