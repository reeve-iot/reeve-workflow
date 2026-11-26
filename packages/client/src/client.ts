import { WORKFLOW_ROUTES } from './generated/types.js'
import type { WorkflowOperations } from './generated/types.js'
import { WORKFLOW_EDITOR_ROUTES } from './generated/editor.js'
import type { WorkflowEditorOperations } from './generated/editor.js'

/** The operations the Executor's surface declares, by name. */
export type WorkflowOperationName = keyof WorkflowOperations

/** The operations the Editor's surface declares, by name. */
export type WorkflowEditorOperationName = keyof WorkflowEditorOperations

type Operation = { readonly args: unknown; readonly result: unknown }

type OperationSet<Ops> = { readonly [K in keyof Ops]: Operation }

type ArgsOf<Ops extends OperationSet<Ops>, K extends keyof Ops> = Ops[K]['args']

type Method<Ops extends OperationSet<Ops>, K extends keyof Ops> = Record<never, never> extends ArgsOf<Ops, K>
  ? (args?: ArgsOf<Ops, K>, init?: WorkflowCallOptions) => Promise<Ops[K]['result']>
  : (args: ArgsOf<Ops, K>, init?: WorkflowCallOptions) => Promise<Ops[K]['result']>

/** What a single call may carry beside its arguments. */
export interface WorkflowCallOptions {
  /** Aborting it abandons the request: the call rejects with the fetch's own abort error. */
  readonly signal?: AbortSignal
}

/** Every operation of one surface, as a method. */
export type ClientOf<Ops extends OperationSet<Ops>> = { readonly [K in keyof Ops]: Method<Ops, K> }

/**
 * Every operation the Executor publishes, as a method. The type is derived from the generated surface,
 * so an endpoint that changes shape fails to compile here rather than failing in a caller at run time.
 */
export type WorkflowClient = ClientOf<WorkflowOperations>

/**
 * Every operation the Editor publishes. A separate client, because it is a separate product.
 *
 * `createWorkflow` and `updateWorkflow` resolve to the workflow alone. A save that does not compile is kept as
 * the draft, and nothing in that answer says why: when `draftVersionId` is set where you asked to publish, call
 * `getVersionCompilation({ versionId: draftVersionId })` for its diagnostics.
 */
export type WorkflowEditorClient = ClientOf<WorkflowEditorOperations>

/**
 * Thrown for any response the server did not answer 2xx with. `body` is the problem document as sent, and
 * `headers` the response's own, so a 503's `Retry-After` can be read.
 */
export class WorkflowApiError extends Error {
  constructor(
    readonly status: number,
    readonly operation: string,
    readonly body: unknown,
    readonly headers: Headers = new Headers(),
  ) {
    super(`${operation} answered ${String(status)}`)
    this.name = 'WorkflowApiError'
  }
}

export interface WorkflowClientOptions {
  /** Where the surface is mounted, e.g. `https://host` or `https://host/reeve`. */
  readonly baseUrl: string
  /** Supplied so a host can add auth, retries or its own instrumentation. Defaults to global fetch. */
  readonly fetch?: typeof globalThis.fetch
  /** Merged into every request. A function is called per request, for a token that expires. */
  readonly headers?: Readonly<Record<string, string>> | (() => Readonly<Record<string, string>>)
}

type UnknownArgs = Readonly<Record<string, unknown>>

interface Route {
  readonly method: string
  readonly path: string
  readonly query: readonly string[]
  readonly empty: boolean
}

type RouteTable = Readonly<Record<string, Route>>

function url(baseUrl: string, operation: string, route: Route, args: UnknownArgs): string {
  const path = route.path.replace(/\{(\w+)\}/g, (_, name: string) => {
    const value = args[name]
    if (value === undefined || value === null)
      throw new TypeError(`${operation} needs a value for the path parameter ${name}`)
    return encodeURIComponent(String(value))
  })

  const query = new URLSearchParams()
  for (const name of route.query) {
    const value = args[name]
    if (value !== undefined && value !== null) query.append(name, String(value))
  }
  const search = query.toString()
  return `${baseUrl.replace(/\/+$/, '')}${path}${search === '' ? '' : `?${search}`}`
}

function build<Ops extends OperationSet<Ops>>(
  routes: RouteTable,
  options: WorkflowClientOptions,
): ClientOf<Ops> {
  const send: typeof globalThis.fetch =
    options.fetch ?? ((input, init) => globalThis.fetch(input, init))

  async function call(operation: string, route: Route, args: UnknownArgs, init?: WorkflowCallOptions): Promise<unknown> {
    const extra = typeof options.headers === 'function' ? options.headers() : (options.headers ?? {})
    const hasBody = 'body' in args && args['body'] !== undefined

    const response = await send(url(options.baseUrl, operation, route, args), {
      method: route.method,
      headers: { Accept: 'application/json', ...(hasBody ? { 'Content-Type': 'application/json' } : {}), ...extra },
      ...(hasBody ? { body: JSON.stringify(args['body']) } : {}),
      ...(init?.signal === undefined ? {} : { signal: init.signal }),
    })

    const text = await response.text()
    let parsed: unknown
    try {
      parsed = text === '' ? undefined : JSON.parse(text)
    } catch (cause) {
      if (!response.ok) throw new WorkflowApiError(response.status, operation, text, response.headers)
      throw cause
    }
    if (!response.ok) throw new WorkflowApiError(response.status, operation, parsed, response.headers)
    if (route.empty) return undefined
    if (parsed === undefined)
      throw new TypeError(`${operation} answered ${String(response.status)} with no body`)
    return parsed
  }

  const client: Record<string, (args?: UnknownArgs, init?: WorkflowCallOptions) => Promise<unknown>> = {}
  for (const [operation, route] of Object.entries(routes)) {
    client[operation] = (args = {}, init) => call(operation, route, args, init)
  }

  return client as ClientOf<Ops>
}

/**
 * Builds a client over the Executor's published surface. It carries no auth, no caching and no retry
 * policy: those are the host's, and a client that guessed at them would be one more thing to work around.
 */
export function createWorkflowClient(options: WorkflowClientOptions): WorkflowClient {
  return build<WorkflowOperations>(WORKFLOW_ROUTES, options)
}

/**
 * Builds a client over the Editor's published surface. Separate from the one above because the two are
 * separate products: a host that mounted only the Executor has nothing for these methods to reach.
 */
export function createWorkflowEditorClient(options: WorkflowClientOptions): WorkflowEditorClient {
  return build<WorkflowEditorOperations>(WORKFLOW_EDITOR_ROUTES, options)
}
