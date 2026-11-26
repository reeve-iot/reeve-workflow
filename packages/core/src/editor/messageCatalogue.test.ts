import { readdirSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { ValidationFinding, WorkflowEdge, WorkflowNode } from '../model/workflow.js'
import { BUILTIN_PORT_TYPES } from '../portTypes/builtins.js'
import { createPortTypeRegistry } from '../portTypes/registry.js'
import {
  ENGLISH_VALIDATION_MESSAGES, LIBRARY_VALIDATION_CODES, MESSAGE_ONLY_VALIDATION_CODES, validationMessage,
} from './messageCatalogue.js'
import { createWorkflowEditorStore } from './store.js'
import { runNodeRules } from './validationRules.js'
import { validateBranchOutputs } from './validateBranchOutputs.js'

const REPO = resolve(__dirname, '../../../..')
const matches = (text: string, pattern: RegExp): string[] => [...text.matchAll(pattern)].map((match) => match[1] as string)

function editorSources(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = resolve(directory, entry.name)
    return entry.isFile() && path.endsWith('.ts') && !path.endsWith('.test.ts') ? [readFileSync(path, 'utf8')] : []
  })
}

function probe(code: string): { reads: Set<string>; text: string } {
  const reads = new Set<string>()
  const params = new Proxy({}, { get: (_target, key) => { reads.add(String(key)); return `«${String(key)}»` } })
  const entry = ENGLISH_VALIDATION_MESSAGES[code]
  return { reads, text: entry ? entry.render(params) : '' }
}

interface DocRow { required: string[]; english: string }
function documentedCodes(): Map<string, DocRow> {
  const catalogue = JSON.parse(readFileSync(resolve(REPO, 'contracts/validation-message-catalogue.json'), 'utf8')) as {
    rows: { code: string; requiredParams: string[]; english: string }[]
  }
  return new Map(catalogue.rows.map((row) => [row.code, { required: row.requiredParams, english: row.english }]))
}

describe('the English validation catalogue', () => {
  it('builds English from params and ignores the raw message', () => {
    expect(validationMessage({
      nodeId: 'n1',
      code: 'required-input-unfed',
      params: { nodeLabel: 'Notify', portName: 'Message', portLabel: 'Body' },
      message: 'stale or translated raw text',
    })).toBe("Node 'Notify' is missing required mapping for 'Body'")
  })

  it('has an entry for every code the editor writes and every compiler rule id', () => {
    const editorCodes = new Set(editorSources(__dirname).flatMap((source) => matches(source, /\bcode:\s*'([^']+)'/g)))
    const parity = JSON.parse(readFileSync(resolve(REPO, 'contracts/rule-parity.json'), 'utf8')) as { rules: { id: string }[] }
    const compilerCodes = new Set([
      ...parity.rules.map((rule) => rule.id),
      ...matches(readFileSync(resolve(REPO, 'dotnet/src/ReeveWorkflow.Core/Compilation/CompilationRules.cs'), 'utf8'), /public const string \w+ = "([^"]+)"/g),
    ])
    const known = new Set(LIBRARY_VALIDATION_CODES)
    expect([...editorCodes].filter((code) => !known.has(code))).toEqual([])
    expect([...compilerCodes].filter((code) => !known.has(code))).toEqual([])
    const refusals = JSON.parse(readFileSync(resolve(REPO, 'contracts/refusal-codes.json'), 'utf8')) as { rows: { code: string; kind: string }[] }
    const saveRefusals = refusals.rows.filter((row) => row.kind === 'save refusal').map((row) => row.code)
    const runtimeCodesWithARow = ['secret-unresolved'].filter((code) => refusals.rows.some((row) => row.kind === '_error.code' && row.code === code))
    expect(runtimeCodesWithARow).toEqual(['secret-unresolved'])
    const produced = new Set([...editorCodes, ...compilerCodes, ...saveRefusals, ...runtimeCodesWithARow])
    expect(LIBRARY_VALIDATION_CODES.filter((code) => !produced.has(code))).toEqual([])
    expect(MESSAGE_ONLY_VALIDATION_CODES.filter((code) => code in ENGLISH_VALIDATION_MESSAGES)).toEqual([])
  })

  it('has a catalogue entry for every save refusal the contract lists', () => {
    const refusals = JSON.parse(readFileSync(resolve(REPO, 'contracts/refusal-codes.json'), 'utf8')) as { rows: { code: string; kind: string }[] }
    const saveRefusals = refusals.rows.filter((row) => row.kind === 'save refusal').map((row) => row.code)
    expect(saveRefusals.length).toBeGreaterThan(0)
    expect(saveRefusals.filter((code) => !(code in ENGLISH_VALIDATION_MESSAGES))).toEqual([])
  })

  it.each(Object.keys(ENGLISH_VALIDATION_MESSAGES))('%s: its template reads exactly the params it requires', (code) => {
    const entry = ENGLISH_VALIDATION_MESSAGES[code]
    const { reads, text } = probe(code)
    expect(entry?.code).toBe(code)
    expect([...reads].sort()).toEqual([...(entry?.requiredParams ?? [])].sort())
    expect(text).not.toBe('')
    for (const name of reads) expect(text).toContain(`«${name}»`)
  })

  it('is listed one row per code in the contract, with the same required params and the same English', () => {
    const documented = documentedCodes()
    expect([...documented.keys()].sort()).toEqual([...LIBRARY_VALIDATION_CODES].sort())
    for (const [code, entry] of Object.entries(ENGLISH_VALIDATION_MESSAGES)) {
      const row = documented.get(code)
      expect(row?.required, `${code} required params`).toEqual([...entry.requiredParams])
      const rendered = (row?.english ?? '').replace(/\{(\w+)\}/g, '«$1»')
      expect(rendered, `${code} English`).toBe(probe(code).text)
    }
  })
})

describe('validationMessage', () => {
  const unfed: ValidationFinding = {
    nodeId: 'n1', code: 'required-input-unfed', message: 'server wording',
    params: { nodeLabel: 'Notify', portLabel: 'Body' },
  }

  it('shows the finding\'s own message when a required param is missing, as a publish diagnostic is', () => {
    const diagnostic: ValidationFinding = { nodeId: unfed.nodeId, code: unfed.code, message: unfed.message }
    expect(validationMessage(diagnostic)).toBe('server wording')
    expect(validationMessage({ ...unfed, params: { nodeLabel: 'Notify' } })).toBe('server wording')
    expect(validationMessage(diagnostic, { 'required-input-unfed': () => 'host wording' })).toBe('server wording')
  })

  it('shows the host\'s override ahead of the template once the finding carries every required param', () => {
    const calls: unknown[] = []
    const catalogue = { 'required-input-unfed': (params: Readonly<Record<string, unknown>>, finding: ValidationFinding) => { calls.push(finding); return `host ${String(params.portLabel)}` } }
    expect(validationMessage(unfed, catalogue)).toBe('host Body')
    expect(calls).toEqual([unfed])
  })

  it('uses a host code\'s override whenever there is one, and its message otherwise', () => {
    const hostFinding: ValidationFinding = { nodeId: null, code: 'dispatch-bonded-depot', message: 'depot wording' }
    expect(validationMessage(hostFinding)).toBe('depot wording')
    expect(validationMessage(hostFinding, { 'dispatch-bonded-depot': () => 'host depot' })).toBe('host depot')
  })

  it('shows the unknown-namespace compiler message and lets the host localize it without params', () => {
    const diagnostic: ValidationFinding = {
      nodeId: 'n1', code: 'expression-unknown-namespace',
      message: "Expression on node 'n1' references 'device.gone.x', which does not exist.",
    }
    expect(validationMessage(diagnostic)).toBe(diagnostic.message)
    expect(validationMessage({ ...diagnostic, params: { path: 'device.gone.x' } })).toBe(diagnostic.message)
    expect(validationMessage(diagnostic, { 'expression-unknown-namespace': () => 'host namespace wording' }))
      .toBe('host namespace wording')
  })

  it('shows the message for a compiler rule id the library holds no template for', () => {
    expect(validationMessage({ nodeId: null, code: 'expression-syntax', message: 'server expression text' })).toBe('server expression text')
  })

  it('lets a host translate a dotted-name compiler diagnostic without invented params', () => {
    const diagnostic: ValidationFinding = { nodeId: 'n', code: 'dotted-name', message: "Name 'result.value' contains a dot" }
    expect(validationMessage(diagnostic)).toBe(diagnostic.message)
    expect(validationMessage(diagnostic, { 'dotted-name': (_params, finding) => `host: ${finding.message}` }))
      .toBe(`host: ${diagnostic.message}`)
  })

  it('does not read a code off Object.prototype', () => {
    expect(validationMessage({ nodeId: null, code: 'constructor', message: 'plain' })).toBe('plain')
    expect(validationMessage({ nodeId: null, code: 'toString', message: 'plain' }, {})).toBe('plain')
  })
})

describe('every library finding renders as the message it carries', () => {
  const portTypes = createPortTypeRegistry(BUILTIN_PORT_TYPES)
  const block = (overrides: Partial<BlockTypeDescriptor>): BlockTypeDescriptor => ({
    blockTypeId: 'noop', displayName: 'Noop', inputs: [], outputs: [], subCanvasNames: [], subCanvasInputs: {}, subCanvasOutputs: {}, ...overrides,
  })
  const node = (id: string, blockTypeId: string, extra: Partial<WorkflowNode> = {}): WorkflowNode =>
    ({ id, blockTypeId, position: { x: 0, y: 0 }, configuration: {}, ...extra })
  const edge = (id: string, sourceNodeId: string, targetNodeId: string, mappings: WorkflowEdge['mappings'] = {}): WorkflowEdge =>
    ({ id, sourceNodeId, targetNodeId, mappings })
  const START = node('__workflow_input__', 'WorkflowInput')
  const agree = (findings: readonly ValidationFinding[], codes: readonly string[]) => {
    expect(findings.map((f) => f.code)).toEqual(codes)
    for (const finding of findings) expect(validationMessage(finding), finding.code).toBe(finding.message)
  }

  it('for the branch-output warnings', () => {
    const NOTIFY = block({ blockTypeId: 'notify', displayName: 'Notify', outputs: [{ name: 'Text', displayName: 'Text', type: 'String', isRequired: false }] })
    const LOOP = block({ blockTypeId: 'loop.for', displayName: 'For Loop', subCanvasNames: ['Body'], subCanvasInputs: { Body: [] } })
    const MERGED = block({ blockTypeId: 'merged', displayName: 'Merged', subCanvasNames: ['Body'], subCanvasInputs: { Body: [] }, outputSatisfyingBranchSets: [['Body']] })
    const run = (blockTypeId: string, port: { type: string; sources: Record<string, string> }, fed: boolean) => validateBranchOutputs({
      definition: {
        nodes: [START, node('loop', blockTypeId, {
          outputPorts: [{ name: 'out', displayName: 'Out', ...port }],
          subCanvases: {
            Body: {
              nodes: [node('__workflow_input__', 'WorkflowInput'), node('src', 'notify'), node('__workflow_output__', 'WorkflowOutput')],
              edges: fed ? [edge('be1', 'src', '__workflow_output__', { value: { type: 'direct', sourceOutputName: 'Text' } })] : [],
            },
          },
        })],
        edges: [edge('e1', '__workflow_input__', 'loop')],
      },
      blockTypes: [NOTIFY, LOOP, MERGED],
      workflowInputParams: [],
      portTypes,
    })
    agree(run('loop.for', { type: 'String', sources: { Body: 'value' } }, false), ['branch-output-unset'])
    agree(run('loop.for', { type: 'Boolean', sources: { Body: 'value' } }, true), [])
    agree(run('merged', { type: 'String', sources: {} }, false), ['branch-output-maybe-unset'])
  })

  it('for a crashed validation rule', () => {
    const rule = () => { throw new Error('boom') }
    agree(runNodeRules([rule], { node: node('n', 'noop'), edges: [], blockType: undefined, label: 'Noop', resolveOutputType: () => undefined, portTypes }), ['validation-rule-failed'])
  })

  it('for a document that cannot be read', () => {
    const store = createWorkflowEditorStore({ portTypes })
    store.getState().loadWorkflow({ Nodes: [], edges: [] } as unknown as Parameters<ReturnType<typeof store.getState>['loadWorkflow']>[0])
    agree(store.getState().validateWorkflowFindings(), ['definition-unreadable'])
    expect(validationMessage(store.getState().validateWorkflowFindings()[0] as ValidationFinding)).toMatch(/^The stored workflow cannot be read: /)
  })
})
