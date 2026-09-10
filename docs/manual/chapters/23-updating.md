## Updating: ANAN Core, p2app, and FPGA gateware

### ANAN Core itself

Settings → UPDATES shows installed and latest production versions. On the radio, **INSTALL & RESTART** downloads, verifies, swaps atomically, and restarts — with automatic rollback if the new version fails to come up. p2app is stopped and restarted around the swap automatically. A startup toast offers new versions when they appear. If the screen looks odd right after an update, reload once (Ctrl+Shift+R) — the browser can briefly hold the old interface.

### p2app

**UPDATE P2APP** in the same tab — §4. Knows "already up to date," rebuilds from the Saturn repository when there's something new, restores the previous binary on any build failure.

### FPGA firmware (gateware) — radio only

Settings → UPDATES → **FPGA FIRMWARE**:

1. **LOAD AVAILABLE IMAGES** lists official gateware from the Saturn repository.
2. **CHECK AGAINST INSTALLED** compares the selected image byte-for-byte with the radio's primary flash slot — know before you flash.
3. Type **FLASH** into the confirm field to enable the red button. Keep power on until it reports done, then power-cycle to run the new gateware.

**Safety by design:** only the *primary* slot is ever written. The factory *golden* image is never touched — if a flash goes wrong, the radio falls back to golden and boots anyway. The flasher refuses to run while transmitting or while the data plane is busy. Never flash while p2app is streaming.
