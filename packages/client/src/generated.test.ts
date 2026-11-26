import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import { generateTypes } from './generate.js'
import { parseSurface } from './surface.js'
import { WORKFLOW_ROUTES } from './generated/types.js'
import { WORKFLOW_EDITOR_ROUTES } from './generated/editor.js'

const root = (path: string): string => resolve(process.cwd(), path)

const surfaces = [
  {
    product: 'executor',
    surface: root('packages/client/surface.json'),
    published: root('dotnet/src/ReeveWorkflow.AspNetCore/workflow-api-surface.json'),
    generated: root('packages/client/src/generated/types.ts'),
    names: {
      operations: 'WorkflowOperations',
      routes: 'WORKFLOW_ROUTES',
      surface: 'surface.json',
      module: 'types.ts',
    },
    routes: WORKFLOW_ROUTES as Readonly<Record<string, unknown>>,
  },
  {
    product: 'editor',
    surface: root('packages/client/surface.editor.json'),
    published: root('dotnet/src/ReeveWorkflow.Editor/workflow-editor-api-surface.json'),
    generated: root('packages/client/src/generated/editor.ts'),
    names: {
      operations: 'WorkflowEditorOperations',
      routes: 'WORKFLOW_EDITOR_ROUTES',
      surface: 'surface.editor.json',
      module: 'editor.ts',
    },
    routes: WORKFLOW_EDITOR_ROUTES as Readonly<Record<string, unknown>>,
    sharedWith: root('packages/client/surface.json'),
  },
] as const

describe.each(surfaces)('the generated $product module', (subject) => {
  it('is generated from a surface this package can find', () => {
    expect(existsSync(subject.surface), `no surface at ${subject.surface}`).toBe(true)
  })

  it('is what the committed surface generates', () => {
    const shared =
      'sharedWith' in subject
        ? {
            module: './types.js',
            surface: parseSurface(JSON.parse(readFileSync(subject.sharedWith, 'utf8'))),
          }
        : undefined
    const expected = generateTypes(
      parseSurface(JSON.parse(readFileSync(subject.surface, 'utf8'))),
      subject.names,
      shared,
    )
    const actual = readFileSync(subject.generated, 'utf8')
    if (actual !== expected) writeFileSync(`${subject.generated}.actual`, expected)
    expect(actual, `regenerated output written to ${subject.generated}.actual`).toBe(expected)
  })

  it('routes every operation the surface declares, and no other', () => {
    const surface = parseSurface(JSON.parse(readFileSync(subject.surface, 'utf8')))
    expect(Object.keys(subject.routes).sort()).toEqual(
      surface.endpoints.map((e) => e.operation).sort(),
    )
  })

  it('copies the document the server publishes, byte for byte', () => {
    expect(existsSync(subject.published), `no published surface at ${subject.published}`).toBe(true)
    expect(readFileSync(subject.surface, 'utf8')).toBe(readFileSync(subject.published, 'utf8'))
  })
})
