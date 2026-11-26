import { existsSync, readFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import type { BlockPort, BlockTypeDescriptor } from '../model/blockType.js'
import type { ValidationFinding, WorkflowDefinition, WorkflowParameter } from '../model/workflow.js'
import { BUILTIN_PORT_TYPES } from '../portTypes/builtins.js'
import { createPortTypeRegistry } from '../portTypes/registry.js'
import { createWorkflowEditorStore } from './store.js'
import { validateStructureFindings } from './validateStructure.js'

const corpusPath = resolve(__dirname, '../../../../contracts/workflow-validation-corpus.json')

interface PortRow { name: string; type: string; isRequired?: boolean }
interface BlockRow {
  blockTypeId: string
  inputs: PortRow[]
  outputs: PortRow[]
  subCanvases: string[]
  subCanvasInputs: Record<string, PortRow[]>
  hasDynamicPorts: boolean
  supportsDeclaredOutputs?: boolean
  typeParameters?: { name: string; constraints: string[] }[]
}
interface CorpusCase {
  name: string
  why: string
  gradedBy?: string[]
  signature?: { inputs: unknown[]; outputs: unknown[] }
  definition: WorkflowDefinition
  expect: { verdict: 'valid' | 'invalid'; rules?: string[] }
}

const corpus = JSON.parse(readFileSync(corpusPath, 'utf8')) as { $note: string[]; blocks: BlockRow[]; cases: CorpusCase[] }
const fileCases = (JSON.parse(readFileSync(corpusPath, 'utf8')) as { cases: Pick<CorpusCase, 'name' | 'gradedBy'>[] }).cases

const blockPort = (p: PortRow): BlockPort => ({ name: p.name, displayName: p.name, type: p.type, isRequired: p.isRequired ?? false })
const blocks: BlockTypeDescriptor[] = corpus.blocks.map((b) => ({
  blockTypeId: b.blockTypeId,
  displayName: b.blockTypeId,
  inputs: b.inputs.map(blockPort),
  outputs: b.outputs.map(blockPort),
  hasDynamicPorts: b.hasDynamicPorts,
  supportsDeclaredOutputs: b.supportsDeclaredOutputs ?? false,
  typeParameters: b.typeParameters ?? [],
  subCanvasNames: b.subCanvases,
  subCanvasInputs: Object.fromEntries(b.subCanvases.map((s) => [s, (b.subCanvasInputs[s] ?? []).map(blockPort)])),
}))

function measure(testCase: CorpusCase): ValidationFinding[] {
  const store = createWorkflowEditorStore({ portTypes: createPortTypeRegistry(BUILTIN_PORT_TYPES) })
  store.getState().setBlockTypes(blocks)
  store.getState().loadWorkflow(structuredClone(testCase.definition))
  if (testCase.signature) {
    const { inputs, outputs } = structuredClone(testCase.signature)
    store.getState().setWorkflowParams(inputs as WorkflowParameter[], outputs as WorkflowParameter[])
  }
  return [...validateStructureFindings({ ...testCase.definition, blockTypes: blocks }), ...store.getState().validateWorkflowFindings()]
}

function reaches(testCase: CorpusCase, findings: ValidationFinding[]): boolean {
  const verdict = findings.length === 0 ? 'valid' : 'invalid'
  if (verdict !== testCase.expect.verdict) return false
  return verdict === 'valid' || findings.some((f) => f.ruleId !== undefined && (testCase.expect.rules ?? []).includes(f.ruleId))
}

const listsEditor = (testCase: Pick<CorpusCase, 'gradedBy'>) => testCase.gradedBy?.includes('editor') === true

describe('workflow validation corpus — editor side', () => {
  it('the corpus is not empty', () => {
    expect(corpus.cases.length).toBeGreaterThan(0)
  })

  for (const testCase of corpus.cases) {
    it(`${testCase.name}: ${testCase.why}`, () => {
      const findings = measure(testCase)
      const found = `found ${JSON.stringify(findings.map((f) => f.ruleId ?? f.message))}; gradedBy ${JSON.stringify(testCase.gradedBy)}`
      if (listsEditor(testCase)) {
        expect(reaches(testCase, findings), `expects ${testCase.expect.verdict} ${JSON.stringify(testCase.expect.rules ?? [])}, ${found}`).toBe(true)
      } else {
        expect(findings, `left out, so the editor must call it valid; ${found}`).toEqual([])
      }
    })
  }

  it('a refusal under a rule the case does not expect does not reach it', () => {
    const unfed = corpus.cases.find((c) => c.name === 'required-input-unfed') as CorpusCase
    expect(reaches(unfed, [{ ruleId: 'cycle', code: 'cycle', nodeId: null, message: 'Workflow contains a cycle' }])).toBe(false)
  })

  it('reaches exactly the cases whose marker lists the editor', () => {
    expect(corpus.cases.map((c) => c.name)).toEqual(fileCases.map((c) => c.name))
    expect(corpus.cases.filter((c) => reaches(c, measure(c))).map((c) => c.name))
      .toEqual(fileCases.filter(listsEditor).map((c) => c.name))
  })

  it.each([
    ['rule-agreement.json', 'ruleAgreement.test.ts'],
    ['dangling-edge-agreement.json', 'danglingEdgeAgreement.test.ts'],
  ])('the note names %s and its grader %s, which reads it', (matrix, grader) => {
    expect(corpus.$note.join(' ')).toContain(`contracts/${matrix}`)
    expect(corpus.$note.join(' ')).toContain(grader)
    expect(existsSync(join(dirname(corpusPath), matrix))).toBe(true)
    expect(readFileSync(join(__dirname, grader), 'utf8')).toContain(`'${matrix}'`)
  })
})
