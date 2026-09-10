## PureSignal (adaptive predistortion)

PureSignal linearizes the PA by comparing a feedback sample of your own transmission against what was meant to be sent, and predistorting to cancel the difference. ANAN Core ships **WDSP 2.1 with PureSignal 3**.

- **Arming:** the **PS** transport key (or G2 drawer key) arms it; ANAN Core remembers whether PS was engaged and restores it next session. PS is never armed by any default mapping — only a control you press or a panel mapping you create.
- **Calibrate into a dummy load first.** Key the two-tone generator (2TON) with PS armed and watch the PURESIGNAL tab: feedback level, correction state, and fit progress.
- **The calibrator reports its own verdicts.** The PS popover (click the PS key's status) shows **accepted vs attempted fits**. If the chain is driven into severe compression, PS **refuses to calibrate** and the tab shows an **over-drive banner** — that is the calibrator protecting you from a meaningless fit, not a fault. Lower drive (or raise Feedback atten) until fits complete.
- **Auto-attenuate** steps the feedback attenuator for you when the feedback path clips while keyed, and the banner distinguishes the two causes (tap clipping vs hardware peak). A manual **Feedback atten** control exists for a fixed offset.
- **Feedback tap:** internal coupler (the ANAN's own) or external (bypass) for an amplifier's coupler.
- **Monitor PA output** must be ON for the live **IMD3 / IMD5** readout: with two-tone keyed, the analyzer finds the actual tone bins (±400 Hz search, so carrier error cancels), places the 3rd/5th-order products from the *measured* tones, and reports a 5-frame median in dBc. Products at the display floor read "—" (better than measurable — expected after convergence); a very wide TX span also reads "—" by design. The PS tab sparkline tracks IMD3 over time.
- **Amp view:** the card at the bottom of the PURESIGNAL tab draws the amplifier *as the calibrator measured it* — gain and phase against input amplitude, with the correction curves being applied. A flat gain trace is a linear amp; sag at the right edge is the compression PS is straightening.
- **Timing and tolerance controls** (MOX/TX/amp/cal delays, iterations per pass, relax-phase-tolerance, looser convergence for weak PAs) are on the tab for unusual amplifier chains; the defaults suit the internal PA.
- **Expectations:** two-tone IMD a few dB shy of the old PS2 close-in, with equal-or-better real-speech splatter, is documented PS3 behaviour.
