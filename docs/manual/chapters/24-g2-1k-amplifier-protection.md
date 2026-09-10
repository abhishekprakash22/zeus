## G2-1K amplifier protection (Ganymede)

On a G2-1K, the amplifier's protection controller trips **in hardware** — bias and PTT drop within microseconds of excessive reverse power, drain current, PSU voltage, heatsink temperature, or forward power — and reports the event over its CAT link. ANAN Core removes drive immediately (same path as the SWR trip) and shows an amber banner explaining the fault in plain language — what happened and what to do (e.g. *"High reflected power — check the antenna and feedline"*) — on the radio and every connected client, including remote. Raw trip flags stay in the log and `/api/amp/status`.

When the fault is cleared, press **RESET AMP** on the banner (or the controller's own reset). If the fault is still present the controller trips again immediately — that is the protection working. The controller is found automatically on the radio's USB serial ports; to pin a port, set `ZEUS_GANYMEDE_PORT=/dev/ttyACM1` in the service environment.
