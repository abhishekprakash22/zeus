## Expert: the native PCIe path (XDMA)

ANAN Core can drive the Saturn board directly over PCIe — no p2app, no network — including RX *and* TX. Working and field-proven, but deliberately an **expert feature** kept off the touch screen:

- Started via the API (`POST /api/xdma/rx/start`); the software pauses p2app itself and verifies the hardware is free before opening the register plane. A running native stream shows a live-rate badge and a STOP button on the Discover row.
- Native **TX is gated behind an arming ceremony**: `POST /api/xdma/tx/arm` with the confirm phrase `i-have-a-dummy-load` and a time-limited window. Unkey is always honored; the window disarms itself.
- **Do not put native TX on an antenna through an amplifier.** The native path does not yet drive the Alex low-pass filters — harmonic suppression is not in place. Dummy load only, until this manual says otherwise.
- Never run p2app and a native session at once. The software enforces this; don't fight it.
- PureSignal and RX2 are not implemented on the native path today — the network Protocol-2 path is the daily driver.
