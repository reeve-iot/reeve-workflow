import { describe, expect, it } from 'vitest'
import type { ValidationFinding } from '../index.js'
import { buildValidationSummary } from './validationSummary.js'

const echoLabel = (nodeId: string | null) => nodeId ?? 'General'

describe('buildValidationSummary', () => {
  it('keeps two nodes of the same type apart, however alike their messages read', () => {
    const errors: ValidationFinding[] = [
      { nodeId: 'a', code: 'required-mapping', message: "Node 'Set Device State' is missing required mapping for 'Device'" },
      { nodeId: 'b', code: 'required-mapping', message: "Node 'Set Device State' is missing required mapping for 'Device'" },
    ]
    const summary = buildValidationSummary(errors, [], echoLabel)
    expect(summary.groupedByNode.map((g) => g.nodeId)).toEqual(['a', 'b'])
  })

  it('deduplicates a repeated message within one node, and still counts the raw total', () => {
    const errors: ValidationFinding[] = [
      { nodeId: 'a', code: 'required-mapping', message: "Node 'X' is missing required mapping for 'P'" },
      { nodeId: 'a', code: 'required-mapping', message: "Node 'X' is missing required mapping for 'P'" },
      { nodeId: 'a', code: 'required-configuration', message: "Node 'X' is missing required configuration field 'Q'" },
    ]
    const summary = buildValidationSummary(errors, [], echoLabel)
    expect(summary.groupedByNode[0]?.findings).toHaveLength(2)
    expect([summary.totalErrorCount, summary.uniqueErrorCount]).toEqual([3, 2])
  })

  it('uses code and params as finding identity even when English wording differs', () => {
    const summary = buildValidationSummary([
      { nodeId: 'a', code: 'required-input-unfed', params: { portName: 'A' }, message: 'wording one' },
      { nodeId: 'a', code: 'required-input-unfed', params: { portName: 'A' }, message: 'wording two' },
      { nodeId: 'a', code: 'required-input-unfed', params: { portName: 'B' }, message: 'wording three' },
    ], [], echoLabel)
    expect(summary.groupedByNode[0]?.findings.map((f) => f.message)).toEqual(['wording one', 'wording three'])
    expect(summary.uniqueErrorCount).toBe(2)
  })

  it('tells param-less findings of one code apart by their message', () => {
    const summary = buildValidationSummary([
      { nodeId: 'a', code: 'host-rule', message: 'first problem' },
      { nodeId: 'a', code: 'host-rule', message: 'second problem' },
      { nodeId: 'a', code: 'host-rule', message: 'second problem' },
    ], [], echoLabel)
    expect(summary.groupedByNode[0]?.findings.map((f) => f.message)).toEqual(['first problem', 'second problem'])
  })

  it('labels a group through the resolver it was given', () => {
    const summary = buildValidationSummary([{ nodeId: 'n1', code: 'test', message: 'boom' }], [], () => 'Set Device State (2 of 3)')
    expect(summary.groupedByNode[0]?.nodeLabel).toBe('Set Device State (2 of 3)')
  })

  it('gives graph-wide findings a group of their own', () => {
    const summary = buildValidationSummary([{ nodeId: null, code: 'cycle', message: 'Workflow contains a cycle' }], [], echoLabel)
    expect(summary.groupedByNode[0]).toMatchObject({ nodeId: null, nodeLabel: 'General' })
  })

  it('counts warnings apart from errors, so a soft finding never reads as a blocker', () => {
    const warning = { nodeId: 'a', code: 'branch-output', message: "Branch 'Else' does not set output 'setpoint'" }
    const summary = buildValidationSummary([{ nodeId: 'a', code: 'test', message: 'boom' }], [warning], echoLabel)
    expect([summary.totalErrorCount, summary.uniqueErrorCount]).toEqual([1, 1])
    expect(summary.groupedWarningsByNode[0]?.findings).toEqual([warning])
    expect([summary.totalWarningCount, summary.uniqueWarningCount]).toEqual([1, 1])
  })

  it('keeps a node in both lists when it has an error and a warning', () => {
    const summary = buildValidationSummary(
      [{ nodeId: 'a', code: 'error', message: 'boom' }], [{ nodeId: 'a', code: 'warning', message: 'careful' }], echoLabel,
    )
    expect([summary.groupedByNode.length, summary.groupedWarningsByNode.length]).toEqual([1, 1])
  })
})
