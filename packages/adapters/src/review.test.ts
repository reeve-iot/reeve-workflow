import { renderHook, waitFor } from '@testing-library/react'
import { createElement } from 'react'
import type { ReactNode } from 'react'
import { describe, expect, it, vi } from 'vitest'
import type { WorkflowClient, WorkflowResponse } from '@reeve-iot/workflow-client'
import { BUILTIN_PORT_TYPES, PortTypeRegistryContext, createPortTypeRegistry } from '@reeve-iot/workflow-core'
import { createReadCache } from './cache.js'
import { createWorkflowDocumentSource } from './workflowDocument.js'

const EMPTY: readonly string[] = []
const settled = () => new Promise((resolve) => { setTimeout(resolve, 0) })

const portTypes = createPortTypeRegistry([...BUILTIN_PORT_TYPES])
const wrapper = ({ children }: { children: ReactNode }) =>
  createElement(PortTypeRegistryContext.Provider, { value: portTypes }, children)

describe('createReadCache — what a failed load looks like to a reader', () => {
  it('answers exactly what a not-yet-loaded key answers, so a reader cannot tell the two apart', async () => {
    const load = vi.fn(async () => { throw new Error('503') })
    const cache = createReadCache<readonly string[]>(load, EMPTY)

    const whileLoading = cache.read('a')
    await settled()
    const afterFailure = cache.read('a')

    expect(afterFailure).toBe(whileLoading)
    expect(load).toHaveBeenCalledTimes(1)
  })

  it('offers no per-key retry: only invalidate(), which forgets every other key too', async () => {
    const load = vi.fn(async (key: string) => {
      if (key === 'broken') throw new Error('503')
      return [key]
    })
    const cache = createReadCache<readonly string[]>(load, EMPTY)
    cache.read('fine')
    cache.read('broken')
    await settled()
    expect(cache.read('fine')).toEqual(['fine'])

    cache.invalidate()
    await settled()
    cache.read('fine')
    cache.read('broken')
    await settled()

    expect(load.mock.calls.filter(([key]) => key === 'fine')).toHaveLength(2)
  })
})

describe('createWorkflowDocumentSource — useWorkflow while loading and after a failure', () => {
  it('returns null in both states, with nothing on the value to tell them apart', async () => {
    const onError = vi.fn()
    let releaseSlow: () => void = () => undefined
    const source = createWorkflowDocumentSource(
      {
        getWorkflow: async ({ id }: { id: string }) => {
          if (id === 'slow') return new Promise<WorkflowResponse>((_, reject) => { releaseSlow = () => reject(new Error('gave up')) })
          throw new Error('404')
        },
      } as unknown as WorkflowClient,
      { onError },
    )

    const loading = renderHook(() => source.useWorkflow('slow'), { wrapper })
    const failed = renderHook(() => source.useWorkflow('missing'), { wrapper })
    await waitFor(() => { expect(onError).toHaveBeenCalledWith('missing', expect.any(Error)) })

    expect(loading.result.current).toBeNull()
    expect(failed.result.current).toBeNull()
    expect(failed.result.current).toBe(loading.result.current)
    releaseSlow()
  })
})
