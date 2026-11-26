import type { Surface, SurfaceEndpoint, SurfaceParameter } from './surface.js'

function header(names: GeneratedNames): string {
  return [
    `// Generated from ${names.surface} by generateTypes(). Do not edit by hand.`,
    `// generated.test.ts writes ${names.module}.actual beside this file when the two disagree.`,
  ].join('\n')
}

const SCALARS: Readonly<Record<string, string>> = {
  string: 'string',
  uuid: 'string',
  'date-time': 'string',
  integer: 'number',
  number: 'number',
  boolean: 'boolean',
  unknown: 'unknown',
}

/**
 * Translates one term of the surface's type grammar into TypeScript. Read right to left: a trailing `?`
 * binds loosest, so `X[]?` is a nullable array and `X?[]` is an array of nullable.
 */
export function translate(term: string): string {
  // `unknown` already admits null, and `unknown | null` is a redundant constituent a linter rejects.
  if (term.endsWith('?')) {
    const inner = translate(term.slice(0, -1))
    return inner === 'unknown' ? inner : `${inner} | null`
  }
  if (term.endsWith('[]')) return `ReadonlyArray<${translate(term.slice(0, -2))}>`
  if (term.startsWith('map<') && term.endsWith('>'))
    return `Readonly<Record<string, ${translate(term.slice(4, -1))}>>`
  if (term.startsWith('#')) return term.slice(1)
  const scalar = SCALARS[term]
  if (scalar === undefined) throw new Error(`Unknown type term in surface: ${term}`)
  return scalar
}

/** Strips the route constraints the server's template carries: `{id:guid}` is `{id}` to a caller. */
export function templateOf(path: string): string {
  return `/${path.replace(/\{(\w+):[^}]+\}/g, '{$1}')}`
}

/** What one success resolves to, and whether the client should expect a body at all. */
export function successOf(endpoint: SurfaceEndpoint): { readonly type: string; readonly empty: boolean } {
  const ok = endpoint.responses.filter((r) => r.status >= 200 && r.status < 300)
  if (ok.length !== 1)
    throw new Error(
      `${endpoint.operation} declares ${String(ok.length)} success responses; a client method returns one thing`,
    )
  const [only] = ok
  if (only?.type !== undefined) return { type: translate(only.type), empty: false }
  // 204 is the one bodiless success with a settled meaning. Any other status with no shape is a
  // declaration nobody finished, and generating `void` for it would hide that.
  if (only?.status === 204) return { type: 'void', empty: true }
  throw new Error(
    `${endpoint.operation} declares a success with no shape; the client would resolve to undefined ` +
      'while its type promised a value, so decide what a bodiless success means before generating one',
  )
}

function argsOf(endpoint: SurfaceEndpoint): string {
  const fields: string[] = []
  const named = new Set<string>()
  const add = (p: SurfaceParameter): void => {
    if (named.has(p.name)) throw new Error(`${endpoint.operation} names ${p.name} twice`)
    named.add(p.name)
    fields.push(`      readonly ${p.name}${p.required ? '' : '?'}: ${translate(p.type)}`)
  }
  endpoint.pathParameters.forEach(add)
  endpoint.queryParameters.forEach(add)
  if (endpoint.requestBody !== undefined) {
    if (named.has('body')) throw new Error(`${endpoint.operation} has a parameter named body`)
    fields.push(
      `      readonly body${endpoint.requestBody.required ? '' : '?'}: ${translate(endpoint.requestBody.type)}`,
    )
  }
  return fields.length === 0 ? 'Record<never, never>' : `{\n${fields.join('\n')}\n    }`
}

/** What the generated module calls the two things it exports, and which pair of files it belongs to. */
export interface GeneratedNames {
  readonly operations: string
  readonly routes: string
  /** The surface document this module is generated from, named in its header. */
  readonly surface: string
  /** This module's own file name, so the header names the right file to compare against. */
  readonly module: string
}

/** Shapes another generated module already declares, so a second one imports them instead of redeclaring. */
export interface SharedShapes {
  /** The module specifier to import them from, e.g. `./types.js`. */
  readonly module: string
  /** The surface those shapes were generated from. */
  readonly surface: Surface
}

const EXECUTOR_NAMES: GeneratedNames = {
  operations: 'WorkflowOperations',
  routes: 'WORKFLOW_ROUTES',
  surface: 'surface.json',
  module: 'types.ts',
}

function shapeOf(type: Surface['types'][number]): string {
  const properties = type.properties.map((p) => `  readonly ${p.name}: ${translate(p.type)}`)
  return `interface ${type.name} {\n${properties.join('\n')}\n}`
}

/**
 * The whole generated module: one interface per shape, one entry per operation, and a route table the
 * client builds URLs from. Deterministic — the same surface always produces the same text.
 */
export function generateTypes(
  surface: Surface,
  names: GeneratedNames = EXECUTOR_NAMES,
  shared?: SharedShapes,
): string {
  const already = new Map((shared?.surface.types ?? []).map((type) => [type.name, shapeOf(type)]))
  const imported: string[] = []
  const shapes: string[] = []

  for (const type of surface.types) {
    const mine = shapeOf(type)
    const theirs = already.get(type.name)
    if (theirs === undefined) {
      shapes.push(`export ${mine}`)
      continue
    }
    // Two surfaces describing one name differently would generate a client that compiles and sends the
    // wrong body to one of the two servers. Imported, so there is one declaration or an error.
    if (theirs !== mine)
      throw new Error(
        `${type.name} is described differently by ${surface.source} and ${shared?.surface.source ?? 'the shared surface'}`,
      )
    imported.push(type.name)
  }

  const operations = surface.endpoints.map((endpoint) => {
    const query = endpoint.queryParameters.map((p) => `'${p.name}'`).join(' | ')
    return [
      `  readonly ${endpoint.operation}: {`,
      `    readonly method: '${endpoint.method}'`,
      `    readonly path: '${templateOf(endpoint.path)}'`,
      `    readonly query: ${query === '' ? 'never' : query}`,
      `    readonly args: ${argsOf(endpoint)}`,
      `    readonly result: ${successOf(endpoint).type}`,
      '  }',
    ].join('\n')
  })

  const routes = surface.endpoints.map(
    (endpoint) =>
      `  ${endpoint.operation}: { method: '${endpoint.method}', path: '${templateOf(endpoint.path)}', query: [${endpoint.queryParameters
        .map((p) => `'${p.name}'`)
        .join(', ')}], empty: ${String(successOf(endpoint).empty)} },`,
  )

  const body = [
    shapes.join('\n\n'),
    '',
    `export interface ${names.operations} {`,
    operations.join('\n'),
    '}',
    '',
    `export const ${names.routes} = {`,
    routes.join('\n'),
    '} as const satisfies Readonly<',
    `  Record<keyof ${names.operations}, ` +
      '{ method: string; path: string; query: readonly string[]; empty: boolean }>',
    '>',
    '',
  ].join('\n')

  // Only the shared shapes this module actually names: an import nobody uses is a compile error under
  // noUnusedLocals, and the closure of a surface is wider than the text generated from it.
  const used = imported.filter((name) => new RegExp(`\\b${name}\\b`).test(body))
  const importLine =
    used.length === 0 ? [] : [`import type { ${used.join(', ')} } from '${shared?.module ?? ''}'`, '']

  return [header(names), '', ...importLine, body].join('\n')
}
