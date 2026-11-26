/** Pan offset that places a node's centre at the viewport centre. `viewport` must already exclude any
 * reserved area (an open configuration panel, say) or the node lands behind it. */
export function computeCenteringPan(
  nodePosition: { x: number; y: number },
  nodeSize: { width: number; height: number },
  viewport: { width: number; height: number },
  zoom: number,
): { x: number; y: number } {
  return {
    x: viewport.width / 2 - (nodePosition.x + nodeSize.width / 2) * zoom,
    y: viewport.height / 2 - (nodePosition.y + nodeSize.height / 2) * zoom,
  }
}
