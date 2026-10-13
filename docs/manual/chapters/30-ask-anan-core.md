# Ask ANAN Core — the built-in assistant

ANAN Core can answer questions about the radio and help you work out what is wrong. It reads the operator manual and the radio's live state; it **cannot change a setting or transmit**. When something needs changing, it tells you exactly what to tap.

## Turning it on

The assistant is off until you turn it on, at the radio, in **Settings → Assistant**:

- **Model provider.** Choose *Anthropic* and paste your own API key, or choose *OpenAI-compatible server* and give the address of a model server on your network (for example a shack PC running a local model). The key is stored on the radio only and is never sent to a browser; the field shows only its last four characters.
- **Enable the assistant**, then **Save**.

Assistant settings can be changed only at the radio, not over a remote session. Asking works anywhere the radio's screen does, including remote browsers and phones.

## Asking

Open it with the **ASK** key (add it to a deck with ✎ on the G2) or **Settings → Assistant → Open assistant**. Type a question, or tap one of the starters. Good questions:

- *I can't hear anything.* — it reads the receiver state and runs the receive diagnostic, then tells you what it found (for example, that RX1 is muted) and what to tap.
- *PureSignal won't converge.* — it reads the PureSignal state and diagnostic, and quotes the values it saw.
- *What does AGC-T do?* — it answers from the manual and names the section.
- *Draft a problem report.* — it gathers the version, state and redacted log lines into a draft you can copy into **Report a problem**. Nothing is sent until you send it there.

Every answer shows the lookups the assistant made (for example `get_radio_state()` or `run_diagnostic(rx-no-audio)`) and the manual sections it used, so you can check where an answer came from.

## What is sent, and what is not

Sent to the model you configured: your question, a snapshot of the radio's state, and redacted log lines when it diagnoses. Never sent: audio, IQ, passwords, or your remote-access key. The assistant cannot change settings, cannot transmit, and will not tell you to bypass a protection such as SWR protection or an amplifier trip.
