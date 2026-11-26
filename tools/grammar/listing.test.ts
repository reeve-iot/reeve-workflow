import { describe, expect, it } from 'vitest'
import { checkCoverage } from './check.js'
import { readContract } from './contract.js'
import { listedTests } from './suite.js'

describe('the listing check: the suite names every row it drives', () => {
  const tests = listedTests()

  it('lists the suite without a browser', () => {
    expect(tests.length).toBeGreaterThan(0)
  })

  it('holds every clicked, prevented, opened and hostSlot row to a test', () => {
    expect(checkCoverage(readContract(), { tests, release: false })).toEqual([])
  })
})
