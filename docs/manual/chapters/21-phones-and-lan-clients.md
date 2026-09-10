## Phones and LAN clients

ANAN Core is a web client talking to its own server (port **6060** on the radio; TLS on 6443). Any browser on your LAN can open the radio's address and get the full interface; the **SERVER** tab's URL box exists for wrapped/native phone apps that need to be told where the server lives (`http://<radio-ip>:6060`, Save & reload).

- **Microphone over the LAN needs HTTPS** (browsers only grant mic access to secure origins or localhost). The SERVER tab's *Mobile browser HTTPS* box lists the radio's secure addresses when LAN HTTPS is enabled.
- **The mobile layout** puts a RADIO tab (VFO, S-meter, band/mode), a TOOLS page with **RX Controls** (Filter / Tuning / front-end sections and an **AF volume** slider above AGC), a large **PTT** button (in a remote session it keys the radio and your phone mic rides the WebRTC path), and **Mute** that actually silences the remote audio element.
- **Install as an app (PWA):** the browser offers "Add to Home Screen" for the radio's own address. The install prompt is deliberately suppressed inside remote sessions — install the local address, use `ananremote.com/go/<CALL>` in the browser for remote.
