import { renderHook, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { WorkflowClient } from '@reeve-iot/workflow-client'
import { createWorkflowOptionsSource } from './workflowOptions.js'

interface Listed {
  id: string
  name: string
}

function clientListing(workflows: readonly Listed[], calls?: { count: number }): WorkflowClient {
  return {
    getWorkflows: async () => {
      if (calls) calls.count += 1
      return workflows
    },
  } as unknown as WorkflowClient
}

const one: Listed = { id: 'w1', name: 'Morning' }
const two: Listed = { id: 'w2', name: 'Evening' }

describe('createWorkflowOptionsSource', () => {
  it('is empty on the first render and holds the list once it arrives', async () => {
    const source = createWorkflowOptionsSource(clientListing([one, two]))

    const { result } = renderHook(() => source.useWorkflowOptions())
    expect(result.current).toEqual([])

    await waitFor(() => { expect(result.current).toHaveLength(2) })
    expect(result.current.map((o) => o.id)).toEqual(['w1', 'w2'])
  })

  it('offers an id and a name and no more', async () => {
    const source = createWorkflowOptionsSource(clientListing([one, two]))

    const { result } = renderHook(() => source.useWorkflowOptions())
    await waitFor(() => { expect(result.current).toHaveLength(2) })

    expect(result.current).toEqual([{ id: 'w1', name: 'Morning' }, { id: 'w2', name: 'Evening' }])
  })

  it('fetches once for every picker on the canvas', async () => {
    const calls = { count: 0 }
    const source = createWorkflowOptionsSource(clientListing([one], calls))

    const first = renderHook(() => source.useWorkflowOptions())
    const second = renderHook(() => source.useWorkflowOptions())
    await waitFor(() => { expect(first.result.current).toHaveLength(1) })
    await waitFor(() => { expect(second.result.current).toHaveLength(1) })

    expect(calls.count).toBe(1)
  })

  it('loads again after refresh, which is how a newly created workflow becomes offerable', async () => {
    const calls = { count: 0 }
    const source = createWorkflowOptionsSource(clientListing([one], calls))

    const { result } = renderHook(() => source.useWorkflowOptions())
    await waitFor(() => { expect(result.current).toHaveLength(1) })
    source.refresh()
    await waitFor(() => { expect(calls.count).toBe(2) })

    expect(result.current).toHaveLength(1)
  })

  it('reports a failure and offers nothing rather than throwing into render', async () => {
    const onError = vi.fn()
    const client = { getWorkflows: async () => { throw new Error('502') } } as unknown as WorkflowClient
    const source = createWorkflowOptionsSource(client, { onError })

    const { result } = renderHook(() => source.useWorkflowOptions())
    await waitFor(() => { expect(onError).toHaveBeenCalledWith(expect.any(Error)) })

    expect(result.current).toEqual([])
  })
})
