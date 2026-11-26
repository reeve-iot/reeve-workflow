import { describe, expect, it } from 'vitest'
import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { WorkflowNode } from '../model/workflow.js'
import { documentNodeLabels } from './nodeLabels.js'

const at = (id: string, blockTypeId: string, extra: Partial<WorkflowNode> = {}): WorkflowNode =>
  ({ id, blockTypeId, position: { x: 0, y: 0 }, configuration: {}, ...extra })
const compare: BlockTypeDescriptor = { blockTypeId: 'compare', displayName: 'Compare', inputs: [], outputs: [] }

describe('documentNodeLabels', () => {
  it('adds the id where another node anywhere in the document has the same name, never to an entry or exit node', () => {
    const inner = at('inner', 'compare')
    const innerEntry = at('__workflow_input__', 'WorkflowInput')
    const outer = at('outer', 'compare')
    const lone = at('lone', 'notify')
    const entry = at('__workflow_input__', 'WorkflowInput')
    const box = at('box', 'box', { subCanvases: { Body: { nodes: [innerEntry, inner], edges: [] } } })
    const label = documentNodeLabels({ nodes: [entry, outer, box, lone] }, [compare])

    expect([outer, inner, lone, entry, innerEntry].map(label)).toEqual(['Compare (outer)', 'Compare (inner)', 'notify', 'WorkflowInput', 'WorkflowInput'])
  })

  it('adds the canvas where nodes called alike share an id on different canvases, leaving the root one plain', () => {
    const body = () => ({ Body: { nodes: [at('c', 'compare')], edges: [] } })
    const root = at('c', 'compare')
    const first = at('box1', 'box', { subCanvases: body() })
    const second = at('box2', 'box', { subCanvases: { Then: { nodes: [at('x', 'box', { subCanvases: body() })], edges: [] } } })
    const label = documentNodeLabels({ nodes: [root, first, second] }, [compare])
    const inner = (box: WorkflowNode, branch: string) => box.subCanvases?.[branch]?.nodes[0] as WorkflowNode

    expect([root, inner(first, 'Body'), inner(inner(second, 'Then'), 'Body')].map(label))
      .toEqual(['Compare (c)', 'Compare (c in box1→Body)', 'Compare (c in box2→Then/x→Body)'])
  })
})
