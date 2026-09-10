## Connecting to your radio

### The Discover tab

Discovery broadcasts every 10 seconds and lists every OpenHPSDR radio it hears: board type, address, firmware, and a **Connect** button. On the radio itself only one of the LAN / loopback Saturn rows appears at a time — with a cable the LAN row wins; unplugged, the loopback row takes over.

### Manual connect

When broadcast discovery can't reach a radio (VPNs, routed subnets), use the **Manual** tab: enter `ip:port` (port **1024** for Protocol 2) and connect. Endpoints you use are saved for next time.

### Protocol 1 versus Protocol 2

ANAN Core speaks both. **Protocol 2 is the primary path** — full RX + TX, CW, and PureSignal — and is what a G2-family radio uses. Protocol 1 remains for older boards (Hermes, Hermes-Lite 2, and friends).

### Choosing the board type

Discovery identifies the board automatically. The **RADIO** settings tab shows *detected vs selected*, and an **Override Detection** control exists for boards that share a discovery ID. The G2 family shares the OrionMkII (0x0A) discovery family: a **variant dropdown** in the radio selector picks between the plain G2, **ANAN-G2-1K** (which switches the power model to the 1 kW coupler transfer function — required for correct power/SWR metering on a 1K), and related boards. On a G2 Ultra whose panel isn't auto-identified, the **Assume G2-Ultra** option (RADIO tab) forces the Andromeda front-panel personality.

### Connecting through a KiwiSDR

The **KIWI SDR** settings tab lets ANAN Core use a public KiwiSDR node as a receive source — pick a node from the map, connect, and the panadapter runs on the remote receiver. Receive-only, useful for propagation checks and remote listening.

### Disconnecting

The **DISC** control (left rail in the G2 layout; header control on the desktop) arms a red **SURE?** for 3 seconds — tap again to disconnect. If you have unsaved TX-audio profile edits, the armed button warns you first.
