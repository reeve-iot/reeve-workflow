import { describe, expect, it } from 'vitest'
import type { DraftAnalysisIndex, DraftNodeAnalysis, DraftPortAnalysis, WorkflowNode } from '@reeve-iot/workflow-core'
import { inputWriteTerm } from './writeTerm.js'

const node = (typeArguments?: Record<string, string>): WorkflowNode =>
  ({ id: 'n', blockTypeId: 'pair', position: { x: 0, y: 0 }, configuration: {}, ...(typeArguments ? { typeArguments } : {}) })

const analysis = (typeArguments: Record<string, string>, effective: string): DraftAnalysisIndex => ({
  node: (): DraftNodeAnalysis => ({ canvasPath: [], nodeId: 'n', typeArguments, inputs: [], outputs: [] }),
  port: (): DraftPortAnalysis => ({ name: 'Value', declared: 'Map<$K,$V>', effective, state: 'bound', unbound: [] }),
  findings: () => [],
})

describe('inputWriteTerm', () => {
  it('lets the node set one parameter and the analysis fill the other, over a stale bound port', () => {
    const stale = analysis({ K: 'String', V: 'Int' }, 'Map<String,Int>')
    expect(inputWriteTerm('Map<$K,$V>', node({ V: 'Boolean' }), 'Value', stale, [])).toBe('Map<String,Boolean>')
  })

  it('takes the analysis port when neither the node nor the analysis arguments close the term', () => {
    const answered = analysis({}, 'Map<String,Int>')
    expect(inputWriteTerm('Map<$K,$V>', node(), 'Value', answered, [])).toBe('Map<String,Int>')
  })
})
