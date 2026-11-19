// SPDX-License-Identifier: GPL-2.0-or-later
//
// Features that ship in the tree but are not offered to the operator.
//
// ANAN Core no longer uses operator chat or the plugin manager. Both are
// withheld here rather than deleted: flip a flag to bring the surface back.
// Typed `boolean` (not the literal `false`) so the guarded code still
// type-checks and lints as reachable.

/** Operator chat: the Chat panel, the chat-operator markers on the
 *  panadapter, and the "Show Chat Operators" display switch. */
export const CHAT_AVAILABLE: boolean = false;

/** The PLUGINS settings tab, and every button or prompt that leads to it. */
export const PLUGINS_TAB_AVAILABLE: boolean = false;

/** Workspace panel ids that are withheld: not offered in Add Panel, and
 *  dropped from saved layouts so no dead tile is left behind. */
export function isPanelWithheld(
  panelId: string,
  chatAvailable: boolean = CHAT_AVAILABLE,
): boolean {
  return panelId === 'chat' && !chatAvailable;
}
