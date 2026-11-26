/** Node box metrics the canvas, the nodes and the wires must all agree on. They live in one module
 * because they drifted apart in the code this was extracted from: the node rendered 220 wide while the
 * wire left from 200, so every wire started twenty pixels INSIDE its own node. */
export const NODE_WIDTH = 220
export const NODE_HEIGHT = 120
export const NODE_HEADER_HEIGHT = 40
export const NODE_ITEM_HEIGHT = 24

/** Width the node configuration panel occupies. The canvas reserves it when centring a node, or the
 * node lands underneath the panel that is about to open and cannot be dragged out. */
export const NODE_CONFIG_PANEL_WIDTH = 300

/** Below this the reserved panel space would push a centred node under the palette instead. */
export const MIN_CENTRING_WIDTH = 320

/** Vertical centre of a node's first port row, in canvas coordinates. */
export function portAnchorY(nodeY: number): number {
  return nodeY + NODE_HEADER_HEIGHT + 0.5 * NODE_ITEM_HEIGHT + 4
}
