import { describe, expect, it } from 'vitest'
import type { SavedWorkflowDefinition } from '../model/workflow.js'
import { documentSignature } from './documentSignature.js'

const definition = (x: number, config: Record<string, string | number> = {}): SavedWorkflowDefinition => ({
  nodes: [{
    id: 'loop', blockTypeId: 'ForLoop', position: { x, y: 0 }, configuration: config,
    subCanvases: { Body: { nodes: [{ id: 'inner', blockTypeId: 'Math', position: { x, y: 5 }, configuration: {} }], edges: [] } },
  }],
  edges: [],
})

const sign = (d: SavedWorkflowDefinition) => documentSignature({ definition: d, inputParameters: [], outputParameters: [] })

describe('documentSignature', () => {
  it('ignores where a node sits, at every canvas depth', () => {
    expect(sign(definition(10))).toBe(sign(definition(900)))
  })

  it('differs when a configuration value changes, even one named position', () => {
    expect(sign(definition(10, { position: 1 }))).not.toBe(sign(definition(10, { position: 2 })))
  })

  it('differs when the parameters change', () => {
    const a = documentSignature({ definition: definition(1), inputParameters: [], outputParameters: [] })
    const b = documentSignature({
      definition: definition(1),
      inputParameters: [{ name: 'p', displayName: 'p', type: 'String', isRequired: true }],
      outputParameters: [],
    })
    expect(a).not.toBe(b)
  })
})
