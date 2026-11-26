import { describe, expect, it } from 'vitest'
import { checkGenerated, checkSourceRuleIds } from './check.js'
import { EDITOR_RULE, EDITOR_STORE, readContract, readText, sourceRuleIds } from './contract.js'
import { generatedByKindTable } from './kindTable.js'

describe('regeneration: the kind table against the contract', () => {
  it('holds a row for every path, object kind, value class and rule they produce', () => {
    expect(checkGenerated(readContract(), generatedByKindTable())).toEqual([])
  })

  it('holds a rule row for every id a source file names on a ruleId line', () => {
    expect(checkSourceRuleIds(readContract(), sourceRuleIds())).toEqual([])
  })

  it('keeps the kind table out of the package the index re-exports', () => {
    expect(readText('packages', 'core', 'src', 'index.ts')).not.toContain('storedDocumentGrammar')
  })

  it('names the editor rule the editor still grades', () => {
    expect(readText(...EDITOR_STORE)).toContain(`'${EDITOR_RULE}'`)
  })
})
