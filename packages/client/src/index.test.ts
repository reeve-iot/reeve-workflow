import { describe, expect, it } from 'vitest'
import * as entry from './index.js'
import { WORKFLOW_ROUTES } from './generated/types.js'
import { WORKFLOW_EDITOR_ROUTES } from './generated/editor.js'

describe('the package entry point', () => {
  it('exports both client factories and the error every call can throw', () => {
    expect(typeof entry.createWorkflowClient).toBe('function')
    expect(typeof entry.createWorkflowEditorClient).toBe('function')
    expect(typeof entry.WorkflowApiError).toBe('function')
  })

  it('exports the generator, so a host can regenerate against its own surface', () => {
    expect(typeof entry.generateTypes).toBe('function')
    expect(typeof entry.parseSurface).toBe('function')
    expect(entry.SUPPORTED_FORMAT_VERSION).toBe(1)
  })

  it('exports the same route tables the generated modules declare, not copies of them', () => {
    expect(entry.WORKFLOW_ROUTES).toBe(WORKFLOW_ROUTES)
    expect(entry.WORKFLOW_EDITOR_ROUTES).toBe(WORKFLOW_EDITOR_ROUTES)
  })

  it('builds a working editor client through the entry point alone', async () => {
    const fetch = (async () =>
      Promise.resolve(new Response(null, { status: 204 }))) as unknown as typeof globalThis.fetch
    const client = entry.createWorkflowEditorClient({ baseUrl: 'https://host', fetch })

    await expect(client.deleteWorkflow({ id: 'w1' })).resolves.toBeUndefined()
  })
})
