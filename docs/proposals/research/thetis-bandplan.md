# Thetis band plan & region model

Research reference for the Zeus "Regional band planning" PRD. Source: `ramdor/Thetis` @ `master`
(public mirror of the main Apache Labs working fork). Paths below are relative to
`Project Files/Source/Console/` in that repo.

---

## 1. Region model

Thetis has **one region enum** (`FRSRegion`) and a global, singleton selection. All band-plan
behavior is derived from that single value — there is no country-of-operator override that layers
on top of a "base" IARU region.

**`FRSRegion`** — `enums.cs:205`

```csharp
public enum FRSRegion {
    FIRST = -1,
    US = 0, Spain = 1, Europe = 2, UK = 3, Italy_Plus = 4, Japan = 5, Australia = 6,
    Norway = 7, Denmark = 8, Latvia = 9, Slovakia = 10, Bulgaria = 11, Greece = 12,
    Hungary = 13, Netherlands = 14, France = 15, Russia = 16, Israel = 17,
    Extended = 18, India = 19, Sweden = 20,
    Region1 = 21, Region2 = 22, Region3 = 23, Germany = 24,
    LAST,
}
```

Notes:
- `Extended` is a **pseudo-region** that unlocks TX across the full tunable range (SWL + out-of-band).
  When `Extended` is set, `BandStackManager.Extended = true` is also passed in to every lookup and
  overrides the user's regional selection (`clsBandStackManager.cs:1276`).
- `Region1` / `Region2` / `Region3` are "pure IARU" fallbacks. Most European countries (Denmark,
  France, Netherlands, …) resolve to `Region1` for their default band stack but get slightly
  different `BandFrequencyData` edges (e.g. Italy's 160M is 1.83–1.85 vs Europe's 1.81–2.0) —
  see `clsBandStackManager.cs:1274` switch.
- Holder: `console.cs:15369` `private FRSRegion current_region`. Set via the `CurrentRegion`
  property (`console.cs:15370`) which forwards to `BandStackManager.Region`.

---

## 2. BandSegment model

Thetis splits "band plan data" across **two independent tables** — a distinction the Zeus
contract needs to preserve.

### 2a. `BandFrequencyData` — coarse band range (TX-guard + band-button routing)

`clsBandStackManager.cs:80`

```csharp
public struct BandFrequencyData {
    public double   low;       // MHz
    public double   high;      // MHz (0 when lowOnly=true, i.e. point-frequency entry like WWV)
    public Band     band;      // enum Band: B160M, B80M, …, B2M, WWV, BLMF, GEN, VHF0..13
    public bool     lowOnly;
    public FRSRegion region;
    public BandType bandType;  // GEN, HF, VHF, UHF, SHF — drives IsOKToTX()
}
```

No power cap, no mode/submode restriction, no explicit TX-allowed flag. TX permission is
implicit: `bandType == HF` AND `band not in {WWV, BLMF}` ⇒ TX allowed
(`clsBandStackManager.cs:1063 IsOKToTX`). SWL entries are `BandType.GEN` so they read-only by
construction.

### 2b. `BandText` — fine-grained sub-band label + TX flag (display + regulatory carve-outs)

`database.cs:747` (DataSet table "BandText")

| Column | Type   | Purpose |
|--------|--------|---------|
| Low    | double | MHz, inclusive |
| High   | double | MHz, inclusive |
| Name   | string | Human label shown on panadapter / VFO (e.g. `"20M Extra CW"`, `"60M Channel 1"`) |
| TX     | bool   | Sub-band TX-allowed flag — **this** is where CW-vs-phone, 60m channelization, "Out of Band" carve-outs live |

`DB.BandText(freq, out name)` (`database.cs:9566`) returns `(Name, TX)` for the sub-segment
containing `freq`; if nothing matches, returns `"Out of Band"` with TX=false.

So: **TX-inhibit actually blends both tables.** `BandStackManager.IsOKToTX` does the
coarse "is this in an HF ham band for the current region" check; `DB.BandText(...).TX` does the
sub-band "is this the 60m general segment vs a 60m channel" check.

---

## 3. Default data shape — sample rows

### `BandFrequencyData` defaults for Region 1 (Europe-style) — `clsBandStackManager.cs:1405`

```csharp
new BandFrequencyData(1.81,   2.0,    Band.B160M, BandType.HF, false, region);
new BandFrequencyData(3.5,    3.8,    Band.B80M,  BandType.HF, false, region);
new BandFrequencyData(5.1,    5.5,    Band.B60M,  BandType.HF, false, region);  // covers UK 5MHz spread
new BandFrequencyData(7.0,    7.2,    Band.B40M,  BandType.HF, false, region);
new BandFrequencyData(14.0,  14.35,   Band.B20M,  BandType.HF, false, region);
new BandFrequencyData(50.0,  52.0,    Band.B6M,   BandType.HF, false, region);  // note: bandType HF even on 6m
new BandFrequencyData(144.0, 148.0,   Band.B2M,   BandType.VHF, false, region);
```

### `BandFrequencyData` defaults for US — `clsBandStackManager.cs:1334`

```csharp
new BandFrequencyData(7.0,  7.3,  Band.B40M, BandType.HF, false, region);  // wider vs EU 7.0-7.2
new BandFrequencyData(3.5,  4.0,  Band.B80M, BandType.HF, false, region);  // wider
new BandFrequencyData(5.1,  5.5,  Band.B60M, BandType.HF, false, region);  // 60m actual allowed subset is in BandText
```

### `BandText` US sample — `database.cs:792` (40m block)

```
7.000000, 7.024999, "40M Extra CW",          TX=true
7.025000, 7.039999, "40M CW",                TX=true
7.040000, 7.040000, "40M RTTY DX",           TX=true
7.100000, 7.124999, "40M CW",                TX=true
7.125000, 7.170999, "40M Ext/Adv SSB",       TX=true
7.290000, 7.290000, "40M AM Calling Frequency", TX=true
```

### `BandText` US 60m — channelized, only 5 narrow TX-allowed spots:

```
5.100000, 5.331999, "60M General",  TX=false
5.332000, 5.332000, "60M Channel 1", TX=true   // single-Hz-wide TX window
5.332001, 5.347999, "60M General",  TX=false
5.348000, 5.348000, "60M Channel 2", TX=true
…
```

---

## 4. User edit surface

- **Region selection**: Setup form → "General" page → "Region" group box (`grpFRSRegion`,
  combo `comboFRSRegion`). `setup.cs:14193` handles the selected-index-changed event, maps the
  display string to an `FRSRegion` value, sets `console.CurrentRegion`, then calls
  `BandStackManager.RegionReset()` which wipes + reseeds the default band-stack entries.
- **Band-stack entries** (frequency/mode/filter memory per band): editable via the
  `frmBandStack2` dialog. Users can add/delete/rename entries. These are stored in the
  `BandStack2Entries` table.
- **`BandFrequencyData` ranges (the actual per-region band edges)**: **not user-editable in the
  GUI** — they are hard-coded in the `frequencyData()` switch. Changing an operator's local edges
  requires a code change + rebuild.
- **`BandText` sub-band labels + TX flag**: **not user-editable in the GUI** — also hard-coded,
  seeded by `DB.AddRegion2BandText()` etc. (`database.cs:1070`).
- Region change fires `DB.UpdateRegion(...)` (`database.cs:11327`), which calls
  `ClearBandText()` then re-seeds the BandText for the selected region family. Note: only
  `US/Australia` and `Japan` have distinct BandText seeders; all other regions (UK, EU, …) inherit
  whatever BandText was last loaded — **known Thetis gotcha**, worth flagging in the Zeus PRD.

Persistence: all of the above lives in a single `DataSet` serialized to XML via
`DB.WriteDB()` → `ds.WriteXml(_file_name, WriteSchema)` (`database.cs:9530`, `9546`). The file
lives under `%AppData%\OpenHPSDR\Thetis\database.xml` (path built by caller).
The region choice itself is persisted as an option row via
`DB.SaveVarsDictionary("Options", …)` (`setup.cs:1627`).

---

## 5. Downstream consumers

1. **TX inhibit** (`console.cs:6778 CheckValidTXFreq`): called from MOX engage, tune, CAT-set-freq,
   and TX mode changes. For SSB/AM/FM/DIGU/DIGL/SPEC it re-checks with the *filter edges* added
   (`f + filterLow`, `f + filterHigh`) — so a wide filter hanging over a band edge inhibits TX
   even if the carrier frequency is in-band. For CW it checks the carrier only. For DRM it offsets
   by −12 kHz. `extended` mode short-circuits to `true`.
2. **`BandByFreq`** (`console.cs:6451`): frequency → `Band` enum, used for band-button highlight
   and BandStack filter selection.
3. **BandStack memory**: each `Band` has its own filtered list of `BandStackEntry` (freq + mode
   + filter + zoom + CTUN state + description + GUID), navigated via `GetFilter(Band)`.
4. **Display label**: `DB.BandText(freq, ...)` → shown in `txtVFOABand` / `txtVFOBBand`. Returns
   `"Out of Band"` string for unmatched frequencies and drives the amber/green
   `band_text_light_color` / `band_text_dark_color` indicator.
5. **Mode coercion**: no automatic mode flip on band change — mode is recalled per
   `BandStackEntry`. Region change does NOT rewrite existing BandStack entries, so an EU operator
   loading a US-seeded DB can have CWL memories in a region where only CWU is conventional.

---

## 6. Gotchas / edge cases

- **Boundary inclusivity**: `BandText` lookup uses SQL-style `freq >= Low AND freq <= High`
  (`database.cs:9575`). Seed rows are authored as `x.xxx000 … x.xxx999` deliberately to avoid
  double-matches at adjacent-row boundaries, but a frequency sitting on an exact row edge
  (e.g. 7.025000) matches only the row that claims it as `Low` — author-dependent, easy to get
  wrong when adding a new segment.
- **Filter-aware TX check can deny TX at an otherwise in-band carrier** (see §5.1). Thetis exposes
  a "tune" bypass (`chkTUN.Checked` passed in as `bIgnoreFilter`).
- **`Extended` region is sticky**: setting `Extended = true` rewrites `m_oldRegion` and
  subsequent reads short-circuit; must also reset Extended when switching away.
- **`FRSRegion.FIRST = -1` is used as "uninitialized"** — don't treat it as a valid selection.
- **Region switch rebuilds BandStack defaults** only when the entries list is empty. If the user
  has any entries at all (from previous session), `addStandardFrequencies()` is skipped
  (`clsBandStackManager.cs:805`) — so changing region mid-session does NOT replace band-stack
  memories; only `RegionReset()` (triggered explicitly by the region combo) wipes and reseeds.
- **BandText is not fully region-partitioned** — UK/EU default to US-seeded sub-band labels
  unless the user has manually set region=Japan. Likely-wrong labels on panadapter for EU
  operators is a long-standing Thetis issue; Zeus should fix this in the contract.
- **No power cap field anywhere**. TX power is a separate per-mode / per-TXProfile setting and is
  NOT coupled to the region or the band segment. If Zeus wants region-aware TX power caps (e.g.
  UK 60m 15 W), that is a **new** concept not in Thetis.
