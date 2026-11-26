import { describe, expect, it } from 'vitest'
import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { WorkflowEdge, WorkflowNode } from '../model/workflow.js'
import { tracedPassthrough } from './passthroughTrace.js'

const notify: BlockTypeDescriptor = {
  blockTypeId: 'notify', displayName: 'Notify',
  inputs: [{ name: 'Message', displayName: 'Message', type: 'String', isRequired: false }],
  outputs: [{ name: 'Text', displayName: 'Text', type: 'String', isRequired: false }],
}
const node = (id: string, blockTypeId: string, extra: Partial<WorkflowNode> = {}): WorkflowNode =>
  ({ id, blockTypeId, position: { x: 0, y: 0 }, configuration: {}, ...extra })
const carried = (sourceOutputName: string): WorkflowEdge =>
  ({ id: 'e', sourceNodeId: 's', targetNodeId: 'n', mappings: { Carry: { type: 'direct', sourceOutputName } } })

describe('tracedPassthrough, where the editor cannot know', () => {
  const n = node('n', 'notify', { passthrough: ['Carry'] })

  it('counts a name fed from a block the catalogue lacks as traced, since the server may hold that block', () => {
    const s = node('s', 'plugin.late')
    const bound = new Map([['n', notify]])
    expect([...tracedPassthrough({ nodes: [s, n], edges: [carried('Out')], bound, entryPorts: [] }, n)]).toEqual(['Carry'])
  })

  it('counts a name fed from a known block lacking that output as untraced', () => {
    const s = node('s', 'notify')
    const bound = new Map([['n', notify], ['s', notify]])
    expect([...tracedPassthrough({ nodes: [s, n], edges: [carried('Out')], bound, entryPorts: [] }, n)]).toEqual([])
  })
})
