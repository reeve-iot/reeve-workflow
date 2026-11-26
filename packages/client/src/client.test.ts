import { describe, expect, it, vi } from 'vitest'
import { createWorkflowClient, createWorkflowEditorClient, WorkflowApiError } from './client.js'
import { WORKFLOW_ROUTES } from './generated/types.js'
import { WORKFLOW_EDITOR_ROUTES } from './generated/editor.js'

function respondWith(body: unknown, status = 200): typeof globalThis.fetch {
  return vi.fn(async () =>
    Promise.resolve(
      new Response(body === undefined ? '' : JSON.stringify(body), {
        status,
        headers: { 'Content-Type': 'application/json' },
      }),
    ),
  ) as unknown as typeof globalThis.fetch
}

function clientOver(fetch: typeof globalThis.fetch, baseUrl = 'https://host'): ReturnType<typeof createWorkflowClient> {
  return createWorkflowClient({ baseUrl, fetch })
}

function requestFor(fetch: typeof globalThis.fetch): { url: string; init: RequestInit } {
  const call = vi.mocked(fetch).mock.calls[0]
  return { url: String(call?.[0]), init: (call?.[1] ?? {}) as RequestInit }
}

describe('url building', () => {
  it('substitutes a path parameter and escapes what it substitutes', async () => {
    const fetch = respondWith({})
    await clientOver(fetch).getWorkflow({ id: 'a/b' })
    expect(requestFor(fetch).url).toBe('https://host/api/workflows/a%2Fb')
  })

  it('omits a query parameter that was not supplied, rather than sending it empty', async () => {
    const fetch = respondWith({ items: [], skip: 0, take: 0, totalCount: 0 })
    await clientOver(fetch).getExecutions({ id: 'w1', take: 25 })
    expect(requestFor(fetch).url).toBe('https://host/api/workflows/w1/executions?take=25')
  })

  it('sends a query parameter set to false, which is a filter and not an absence', async () => {
    const fetch = respondWith({ items: [], skip: 0, take: 0, totalCount: 0 })
    await clientOver(fetch).getExecutions({ id: 'w1', hasErrors: false })
    expect(requestFor(fetch).url).toContain('hasErrors=false')
  })

  it('does not double the separator when the base url ends in one', async () => {
    const fetch = respondWith([])
    await clientOver(fetch, 'https://host/reeve/').getWorkflows()
    expect(requestFor(fetch).url).toBe('https://host/reeve/api/workflows')
  })

  it('refuses a missing path parameter rather than requesting a url with a brace in it', async () => {
    const fetch = respondWith({})
    await expect(
      clientOver(fetch).getWorkflow({ id: undefined as unknown as string }),
    ).rejects.toThrow(/path parameter id/)
  })
})

describe('bodies', () => {
  it('sends no body and no content type when the optional body is omitted', async () => {
    const fetch = respondWith({})
    await clientOver(fetch).executeWorkflow({ id: 'w1' })
    const { init } = requestFor(fetch)
    expect(init.body).toBeUndefined()
    expect(new Headers(init.headers).get('Content-Type')).toBeNull()
  })

  it('sends the body it was given, and says so in the headers', async () => {
    const fetch = respondWith({})
    await clientOver(fetch).executeWorkflow({ id: 'w1', body: { debugMode: true, inputParameters: null } })
    const { init } = requestFor(fetch)
    expect(init.body).toBe('{"debugMode":true,"inputParameters":null}')
    expect(new Headers(init.headers).get('Content-Type')).toBe('application/json')
  })

  it('refuses an empty success body, which its declared result says cannot happen', async () => {
    const fetch = respondWith(undefined)
    await expect(clientOver(fetch).executeWorkflow({ id: 'w1' })).rejects.toThrow(/with no body/)
  })
})

describe('failures', () => {
  it('carries the problem details through, because that is where the reason is', async () => {
    const fetch = respondWith({ status: 404, detail: 'no such workflow' }, 404)
    const error = await clientOver(fetch)
      .getWorkflow({ id: 'w1' })
      .catch((e: unknown) => e)

    expect(error).toBeInstanceOf(WorkflowApiError)
    expect((error as WorkflowApiError).status).toBe(404)
    expect((error as WorkflowApiError).operation).toBe('getWorkflow')
    expect((error as WorkflowApiError).body).toEqual({ status: 404, detail: 'no such workflow' })
  })

  it('keeps the response headers, so a 503 says when to ask again', async () => {
    const problem = { status: 503, detail: 'could not be answered' }
    const fetch = vi.fn(async () =>
      Promise.resolve(new Response(JSON.stringify(problem), { status: 503, headers: { 'Retry-After': '5' } })),
    ) as unknown as typeof globalThis.fetch
    const error = (await createWorkflowEditorClient({ baseUrl: 'https://host', fetch })
      .publishWorkflow({ id: 'w1' })
      .catch((e: unknown) => e)) as WorkflowApiError

    expect(error.status).toBe(503)
    expect(error.headers.get('retry-after')).toBe('5')
    expect(error.body).toEqual(problem)
  })

  it('keeps the headers of a failure whose body is not json', async () => {
    const fetch = vi.fn(async () =>
      Promise.resolve(new Response('<html>503</html>', { status: 503, headers: { 'Retry-After': '30' } })),
    ) as unknown as typeof globalThis.fetch
    const error = (await clientOver(fetch).getWorkflows().catch((e: unknown) => e)) as WorkflowApiError

    expect(error.headers.get('Retry-After')).toBe('30')
  })

  it('keeps the status when a gateway answers html, rather than failing to parse it', async () => {
    const fetch = vi.fn(async () =>
      Promise.resolve(new Response('<html>502</html>', { status: 502 })),
    ) as unknown as typeof globalThis.fetch
    const error = (await clientOver(fetch)
      .getWorkflows()
      .catch((e: unknown) => e)) as WorkflowApiError

    expect(error).toBeInstanceOf(WorkflowApiError)
    expect(error.status).toBe(502)
    expect(error.body).toBe('<html>502</html>')
  })

  it('reports the conflict body a concurrent execution answers with, not a generic message', async () => {
    const conflict = { workflowId: 'w1', mode: 'Skip', outcome: 'Skipped', message: 'already running' }
    const fetch = respondWith(conflict, 409)
    const error = (await clientOver(fetch)
      .executeWorkflow({ id: 'w1' })
      .catch((e: unknown) => e)) as WorkflowApiError

    expect(error.status).toBe(409)
    expect(error.body).toEqual(conflict)
  })
})

describe('headers', () => {
  it('asks the supplier once per request, so a token that expires is re-read', async () => {
    const headers = vi.fn(() => ({ Authorization: 'Bearer t' }))
    const fetch = respondWith([])
    const client = createWorkflowClient({ baseUrl: 'https://host', fetch, headers })

    await client.getWorkflows()
    await client.getWorkflows()

    expect(headers).toHaveBeenCalledTimes(2)
    expect(new Headers(requestFor(fetch).init.headers).get('Authorization')).toBe('Bearer t')
  })
})

describe('coverage of the surface', () => {
  it('exposes a method for every route, so a new endpoint cannot be silently unreachable', () => {
    const client = createWorkflowClient({ baseUrl: 'https://host', fetch: respondWith({}) })
    for (const operation of Object.keys(WORKFLOW_ROUTES))
      expect(typeof client[operation as keyof typeof client]).toBe('function')
  })

  it('uses the method the route declares', async () => {
    const fetch = respondWith({})
    await clientOver(fetch).validateImport({ body: {} as never })
    expect(requestFor(fetch).init.method).toBe('POST')
  })
})

describe('the editor client', () => {
  it('exposes a method for every write route, so a new endpoint cannot be silently unreachable', () => {
    const client = createWorkflowEditorClient({ baseUrl: 'https://host', fetch: respondWith({}) })
    for (const operation of Object.keys(WORKFLOW_EDITOR_ROUTES))
      expect(typeof client[operation as keyof typeof client]).toBe('function')
  })

  it('resolves a 204 to nothing instead of refusing the empty body', async () => {
    const fetch = vi.fn(async () =>
      Promise.resolve(new Response(null, { status: 204 })),
    ) as unknown as typeof globalThis.fetch
    await expect(
      createWorkflowEditorClient({ baseUrl: 'https://host', fetch }).deleteWorkflow({ id: 'w1' }),
    ).resolves.toBeUndefined()
    expect(requestFor(fetch).init.method).toBe('DELETE')
  })

  it('still refuses an empty body from an operation that declares a shape', async () => {
    const fetch = respondWith(undefined, 200)
    await expect(
      createWorkflowEditorClient({ baseUrl: 'https://host', fetch }).publishWorkflow({ id: 'w1' }),
    ).rejects.toThrow(/no body/)
  })

  it('sends the create body and reads back the workflow', async () => {
    const created = { id: 'w1', name: 'n' }
    const fetch = respondWith(created, 201)
    const result = await createWorkflowEditorClient({ baseUrl: 'https://host', fetch }).createWorkflow({
      body: { name: 'n', definitionJson: '{}' } as never,
    })

    const request = requestFor(fetch)
    expect(request.url).toBe('https://host/api/workflows')
    expect(request.init.method).toBe('POST')
    expect(JSON.parse(String(request.init.body))).toEqual({ name: 'n', definitionJson: '{}' })
    expect(result).toEqual(created)
  })

  it('carries the version number into the rollback path', async () => {
    const fetch = respondWith({})
    await createWorkflowEditorClient({ baseUrl: 'https://host', fetch }).rollbackWorkflow({
      id: 'w1',
      versionNumber: 3,
    })
    expect(requestFor(fetch).url).toBe('https://host/api/workflows/w1/versions/3/rollback')
  })
})

describe('a call that carries a signal', () => {
  it('hands the signal to fetch, and sends none when the call carries none', async () => {
    const fetch = respondWith({})
    const controller = new AbortController()
    await clientOver(fetch).getWorkflow({ id: 'w1' }, { signal: controller.signal })
    await clientOver(fetch).getWorkflow({ id: 'w1' })
    const calls = vi.mocked(fetch).mock.calls
    expect(calls[0]?.[1]?.signal).toBe(controller.signal)
    expect(calls[1]?.[1]).not.toHaveProperty('signal')
  })
})
