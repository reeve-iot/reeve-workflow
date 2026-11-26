import { describe, expect, it } from 'vitest'
import { readWorkflowReference } from './workflowReference.js'

describe('readWorkflowReference', () => {
  it('reads the id out of the field the descriptor names', () => {
    expect(readWorkflowReference({ WorkflowId: 'child-1' }, 'WorkflowId')).toBe('child-1')
  })

  it('answers null for a block that does not run another workflow', () => {
    expect(readWorkflowReference({ WorkflowId: 'child-1' }, undefined)).toBeNull()
  })

  it('answers null for an empty choice, so a host resolver is never asked to fetch nothing', () => {
    expect(readWorkflowReference({ WorkflowId: '' }, 'WorkflowId')).toBeNull()
    expect(readWorkflowReference({}, 'WorkflowId')).toBeNull()
  })

  it('answers null for a value that is not an id at all', () => {
    expect(readWorkflowReference({ WorkflowId: 42 }, 'WorkflowId')).toBeNull()
    expect(readWorkflowReference({ WorkflowId: null }, 'WorkflowId')).toBeNull()
  })
})
