import { describe, expect, it } from 'vitest'
import { checkContract } from './check.js'
import { readContract, readText } from './contract.js'
import { generatedByKindTable } from './kindTable.js'
import { listedTests } from './suite.js'

const RELEASE = process.env['GITHUB_REF_TYPE'] === 'tag'

describe('the release gate', () => {
  it.runIf(RELEASE)('refuses a contract holding a gap row', () => {
    expect(checkContract(readContract(), {
      tests: listedTests(), release: true, generated: generatedByKindTable(),
    })).toEqual([])
  })

  it('is the file the tag job runs', () => {
    expect(readText('.github/workflows/release.yml')).toContain('tools/grammar/release.test.ts')
  })
})
