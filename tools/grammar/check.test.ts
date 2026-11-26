import { describe, expect, it } from 'vitest'
import { checkContract, checkCoverage, checkGenerated, rowIds, rowsOf } from './check.js'
import type { Contract, ContractRow, RuleEntry } from './check.js'

type Row = Omit<ContractRow, 'id'>
type Evidence = Omit<Row, 'state'>

const row = (state: ContractRow['state'], extra: Evidence = {}): Row => ({ state, ...extra })

const path = (label: string, values: Record<string, Row>) => ({
  path: label,
  values: Object.entries(values).map(([value, entry]) => ({ value, ...entry })),
})

const rule = (id: string, entry: Row): RuleEntry => ({ id, ...entry })

const contract = (parts: Partial<Contract>): Contract => ({ paths: [], objects: [], workflowRow: [], rules: [], ...parts })

const empty = { tests: [] as readonly string[], release: false }

const NODES = path('canvas.nodes', { array: row('gap') })

describe('checkCoverage: the evidence each state needs', () => {
  it('passes a clicked row a test names and the test names back', () => {
    const held = contract({ paths: [path('canvas.nodes', { array: row('clicked', { test: 'a dropped block' }) })] })
    expect(checkCoverage(held, { ...empty, tests: ['a dropped block @row:canvas.nodes#array'] })).toEqual([])
  })

  it('fails a clicked row that names no test', () => {
    const held = contract({ paths: [path('canvas.nodes', { array: row('clicked') })] })
    expect(checkCoverage(held, empty)).toEqual([{ row: 'canvas.nodes#array', reason: 'clicked needs the name of a test' }])
  })

  it('fails a prevented row that names no test', () => {
    const held = contract({ paths: [path('canvas.nodes', { array: row('prevented') })] })
    expect(checkCoverage(held, empty)).toEqual([{ row: 'canvas.nodes#array', reason: 'prevented needs the name of a test' }])
  })

  it('fails an opened row that names no test', () => {
    const held = contract({ paths: [path('canvas.nodes', { array: row('opened') })] })
    expect(checkCoverage(held, empty)).toEqual([{ row: 'canvas.nodes#array', reason: 'opened needs the name of a test' }])
  })

  it('fails a hostSlot row that names no test', () => {
    const held = contract({ paths: [path('canvas.nodes', { array: row('hostSlot') })] })
    expect(checkCoverage(held, empty)).toEqual([{ row: 'canvas.nodes#array', reason: 'hostSlot needs the name of a test' }])
  })

  it('fails a hostSlot row that names no slot', () => {
    const held = contract({ paths: [path('canvas.nodes', { array: row('hostSlot', { test: 'a test' }) })] })
    expect(checkCoverage(held, { ...empty, tests: ['a test @row:canvas.nodes#array'] }))
      .toEqual([{ row: 'canvas.nodes#array', reason: 'hostSlot needs the slot it is authored through' }])
  })

  it('fails a row whose named test does not exist', () => {
    const held = contract({ paths: [path('canvas.nodes', { array: row('clicked', { test: 'a test that was deleted' }) })] })
    expect(checkCoverage(held, { ...empty, tests: ['another test @row:canvas.nodes#array'] }))
      .toEqual([{ row: 'canvas.nodes#array', reason: "no test in the suite is named 'a test that was deleted'" }])
  })

  it('fails a row whose named test does not name it back', () => {
    const held = contract({ paths: [path('canvas.nodes', { array: row('clicked', { test: 'a test' }) })] })
    expect(checkCoverage(held, { ...empty, tests: ['a test'] }))
      .toEqual([{ row: 'canvas.nodes#array', reason: "the test 'a test' does not name @row:canvas.nodes#array" }])
  })

  it('fails a test that names a row the contract does not hold', () => {
    expect(checkCoverage(contract({}), { ...empty, tests: ['a test @row:canvas.nodes#array'] }))
      .toEqual([{ row: 'canvas.nodes#array', reason: 'no row of the contract has this id' }])
  })

  it('reads a tag beside prose and several tags in one title', () => {
    const held = contract({
      paths: [path('canvas.nodes', { array: row('clicked', { test: 'a test' }), absent: row('clicked', { test: 'a test' }) })],
    })
    expect(checkCoverage(held, { ...empty, tests: ['a test @row:canvas.nodes#array @row:canvas.nodes#absent'] })).toEqual([])
  })
})

describe('checkCoverage: gap rows', () => {
  it('fails a gap row that carries a reason, at the release', () => {
    const held = contract({ paths: [path('declared output.name', { dotted: row('gap', { why: 'the readers refuse a dotted name' }) })] })
    expect(checkCoverage(held, { ...empty, release: true }))
      .toEqual([{ row: 'declared-output.name#dotted', reason: 'a gap row may not reach the release' }])
  })
})

describe('checkCoverage: states a row may hold', () => {
  it('fails a state no row may hold, rather than reading it as one nothing gates', () => {
    const held = contract({ paths: [path('canvas.nodes', { array: row('gapp' as ContractRow['state']) })] })
    expect(checkCoverage(held, empty)).toEqual([{ row: 'canvas.nodes#array', reason: "'gapp' is not a state a row may hold" }])
  })

  it('fails a misspelt state at the release too', () => {
    const held = contract({ paths: [path('canvas.nodes', { array: row('refusd' as ContractRow['state'], { why: 'a reason' }) })] })
    expect(checkCoverage(held, { ...empty, release: true }))
      .toEqual([{ row: 'canvas.nodes#array', reason: "'refusd' is not a state a row may hold" }])
  })
})

describe('checkCoverage: deferred rows', () => {
  it('passes a deferred row that says what is not supported yet', () => {
    const held = contract({ paths: [path('canvas.nodes', { array: row('deferred', { why: 'the editor cannot author an array yet' }) })] })
    expect(checkCoverage(held, empty)).toEqual([])
  })

  it('fails a deferred row with no why', () => {
    const held = contract({ paths: [path('canvas.nodes', { array: row('deferred') })] })
    expect(checkCoverage(held, empty))
      .toEqual([{ row: 'canvas.nodes#array', reason: 'deferred needs a why saying what is not supported yet' }])
  })

  it('fails a deferred row with an empty why', () => {
    const held = contract({ paths: [path('canvas.nodes', { array: row('deferred', { why: '' }) })] })
    expect(checkCoverage(held, empty))
      .toEqual([{ row: 'canvas.nodes#array', reason: 'deferred needs a why saying what is not supported yet' }])
  })
})

describe('checkCoverage: gaps and the release', () => {
  it('passes a gap row before the release', () => {
    expect(checkCoverage(contract({ paths: [NODES] }), empty)).toEqual([])
  })

  it('fails a gap row at the release', () => {
    expect(checkCoverage(contract({ paths: [NODES] }), { ...empty, release: true }))
      .toEqual([{ row: 'canvas.nodes#array', reason: 'a gap row may not reach the release' }])
  })

  it('passes an exempt row at the release', () => {
    const held = contract({ rules: [rule('structure', row('exempt'))] })
    expect(checkCoverage(held, { ...empty, release: true })).toEqual([])
  })

  it('fails exempt on any row but the three exempted by name', () => {
    expect(checkCoverage(contract({ paths: [path('canvas.nodes', { array: row('exempt') })] }), { ...empty, release: true }))
      .toEqual([{ row: 'canvas.nodes#array', reason: "exempt is only for 'structure', 'compilation-fault' and 'definition-undrawable'" }])
  })

  it('fails exempt on a rule row that is not exempted', () => {
    const held = contract({ rules: [rule('cycle', row('exempt'))] })
    expect(checkCoverage(held, { ...empty, release: true }))
      .toEqual([{ row: 'cycle', reason: "exempt is only for 'structure', 'compilation-fault' and 'definition-undrawable'" }])
  })
})

describe('checkGenerated: a new field, value class, object kind or rule without a row', () => {
  const generated = { paths: ['canvas.nodes'], objects: ['canvas'], values: ['canvas.nodes#array', 'canvas#unlisted-key'], rules: ['L1'] }
  const complete = contract({
    paths: [path('canvas.nodes', { array: row('gap') })],
    objects: [path('canvas', { 'unlisted-key': row('gap') })],
    rules: [rule('L1', row('gap'))],
  })

  it('passes a contract holding every regenerated row', () => {
    expect(checkGenerated(complete, generated)).toEqual([])
  })

  it('fails an object row the kind table no longer produces', () => {
    const stale = contract({ ...complete, objects: [path('canvas', { 'unlisted-key': row('gap'), displayName: row('gap') })] })
    expect(checkGenerated(stale, generated)).toEqual([{ row: 'canvas#displayName', reason: 'an object row the kind table does not produce' }])
  })

  it('fails a field the contract holds no row for', () => {
    expect(checkGenerated(contract({}), { ...generated, objects: [], values: [], rules: [] }))
      .toEqual([{ row: 'canvas.nodes', reason: 'a field of the kind table the contract holds no row for' }])
  })

  it('fails an object kind the contract holds no row for', () => {
    expect(checkGenerated(contract({}), { paths: [], values: [], rules: [], objects: ['canvas'] }))
      .toEqual([{ row: 'canvas', reason: 'an object kind of the kind table the contract holds no row for' }])
  })

  it('fails a value class the contract holds no row for', () => {
    expect(checkGenerated(contract({ paths: [NODES] }), { paths: ['canvas.nodes'], objects: [], rules: [], values: ['canvas.nodes#absent'] }))
      .toEqual([{ row: 'canvas.nodes#absent', reason: 'a value class of the kind table the contract holds no row for' }])
  })

  it('fails a rule the contract holds no row for', () => {
    expect(checkGenerated(contract({}), { paths: [], objects: [], values: [], rules: ['L1'] }))
      .toEqual([{ row: 'L1', reason: 'a rule of a reader, the compiler or the editor the contract holds no row for' }])
  })

  it('fails a path the kind table does not produce', () => {
    expect(checkGenerated(contract({ paths: [NODES] }), { paths: [], objects: [], values: [], rules: [] }))
      .toEqual([{ row: 'canvas.nodes', reason: 'a path the kind table does not produce' }])
  })

  it('fails a rule the compiler does not hold', () => {
    expect(checkGenerated(complete, { ...generated, rules: [] }))
      .toEqual([{ row: 'L1', reason: 'a rule the readers, the compiler and the editor do not hold' }])
  })
})

describe('the contract flattened', () => {
  it('gives every row one id, and no two rows the same one', () => {
    const held = contract({
      paths: [path('declared output.name', { string: row('gap') })],
      objects: [path('declared output', { 'unlisted-key': row('gap') })],
      workflowRow: [path('workflow row.Mode', { absent: row('gap') })],
      rules: [rule('L1', row('opened'))],
    })
    expect(rowIds(held)).toEqual(['declared-output.name#string', 'declared-output#unlisted-key', 'workflow-row.Mode#absent', 'L1'])
  })

  it('reads a contract as rows, refusing a duplicate id rather than hiding it', () => {
    const held = contract({ paths: [path('canvas.nodes', { array: row('gap'), absent: row('gap') })] })
    const twice: Contract = { ...held, rules: [rule('canvas.nodes#array', row('gap'))] }
    expect(() => rowsOf(twice)).toThrow(/canvas\.nodes#array/)
  })
})

describe('checkContract', () => {
  it('reports the regeneration failures and the coverage failures together', () => {
    const held = contract({ paths: [path('canvas.nodes', { array: row('clicked') })] })
    const failures = checkContract(held, {
      tests: [], release: false, generated: { paths: [], objects: [], values: [], rules: [] },
    })
    expect(failures.map((failure) => failure.row)).toEqual(['canvas.nodes', 'canvas.nodes#array'])
  })
})
