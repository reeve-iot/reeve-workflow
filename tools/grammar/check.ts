export type RowState = 'clicked' | 'prevented' | 'opened' | 'hostSlot' | 'deferred' | 'gap' | 'exempt'

export interface Contracted {
  readonly state: RowState
  readonly test?: string
  readonly slot?: string
  readonly why?: string
}

export interface ContractRow extends Contracted {
  readonly id: string
}

export interface ValueEntry extends Contracted {
  readonly value: string
}

export interface PathEntry {
  readonly path: string
  readonly values: readonly ValueEntry[]
}

export interface RuleEntry extends Contracted {
  readonly id: string
}

export interface Contract {
  readonly paths: readonly PathEntry[]
  readonly objects: readonly PathEntry[]
  readonly workflowRow: readonly PathEntry[]
  readonly rules: readonly RuleEntry[]
}

export interface Coverage {
  readonly tests: readonly string[]
  readonly release: boolean
}

export interface Generated {
  readonly paths: readonly string[]
  readonly objects: readonly string[]
  readonly values: readonly string[]
  readonly rules: readonly string[]
}

export interface Failure {
  readonly row: string
  readonly reason: string
}

const TESTED: readonly RowState[] = ['clicked', 'prevented', 'opened', 'hostSlot']

const STATES: readonly RowState[] = ['clicked', 'prevented', 'opened', 'hostSlot', 'deferred', 'gap', 'exempt']

const EXEMPT: readonly string[] = ['structure', 'compilation-fault', 'definition-undrawable']

const listed = (ids: readonly string[]): string => {
  const quoted = ids.map((id) => `'${id}'`)
  return quoted.length < 2 ? quoted.join('') : `${quoted.slice(0, -1).join(', ')} and ${quoted.at(-1)}`
}

export const slug = (label: string): string => label.replaceAll(' ', '-')

const pathIds = (entries: readonly PathEntry[]): string[] =>
  entries.flatMap((entry) => entry.values.map((value) => `${slug(entry.path)}#${value.value}`))

export function rowIds(contract: Contract): string[] {
  return [
    ...pathIds(contract.paths),
    ...pathIds(contract.objects),
    ...pathIds(contract.workflowRow),
    ...contract.rules.map((rule) => rule.id),
  ]
}

export function rowsOf(contract: Contract): ContractRow[] {
  const rows: ContractRow[] = [
    ...contract.paths.flatMap((entry) => entry.values.map((value) => ({ ...value, id: `${slug(entry.path)}#${value.value}` }))),
    ...contract.objects.flatMap((entry) => entry.values.map((value) => ({ ...value, id: `${slug(entry.path)}#${value.value}` }))),
    ...contract.workflowRow.flatMap((entry) => entry.values.map((value) => ({ ...value, id: `${slug(entry.path)}#${value.value}` }))),
    ...contract.rules.map((rule) => ({ ...rule, id: rule.id })),
  ]
  const seen = new Set<string>()
  for (const row of rows) {
    if (seen.has(row.id)) throw new Error(`The contract holds two rows with the id '${row.id}'.`)
    seen.add(row.id)
  }
  return rows
}

const TAGS = /@[a-z-]+:\S+/g

export const testName = (title: string): string => title.replace(TAGS, ' ').replace(/\s+/g, ' ').trim()

export const rowsNamedBy = (title: string): string[] =>
  (title.match(TAGS) ?? []).filter((tag) => tag.startsWith('@row:')).map((tag) => tag.slice('@row:'.length))

export function checkCoverage(contract: Contract, coverage: Coverage): Failure[] {
  const failures: Failure[] = []
  const rows = rowsOf(contract)
  const ids = new Set(rows.map((row) => row.id))
  const named = new Map<string, readonly string[]>()
  for (const title of coverage.tests) {
    const name = testName(title)
    named.set(name, [...(named.get(name) ?? []), ...rowsNamedBy(title)])
  }

  for (const title of coverage.tests) {
    for (const id of rowsNamedBy(title)) {
      if (!ids.has(id)) failures.push({ row: id, reason: 'no row of the contract has this id' })
    }
  }

  for (const row of rows) {
    if (!STATES.includes(row.state)) {
      failures.push({ row: row.id, reason: `'${row.state}' is not a state a row may hold` })
      continue
    }
    if (TESTED.includes(row.state)) {
      if (row.test === undefined || row.test === '') {
        failures.push({ row: row.id, reason: `${row.state} needs the name of a test` })
        continue
      }
      const names = named.get(row.test)
      if (names === undefined) {
        failures.push({ row: row.id, reason: `no test in the suite is named '${row.test}'` })
        continue
      }
      if (!names.includes(row.id)) {
        failures.push({ row: row.id, reason: `the test '${row.test}' does not name @row:${row.id}` })
        continue
      }
      if (row.state === 'hostSlot' && (row.slot === undefined || row.slot === '')) {
        failures.push({ row: row.id, reason: 'hostSlot needs the slot it is authored through' })
      }
    } else if (row.state === 'deferred') {
      if (row.why === undefined || row.why === '') {
        failures.push({ row: row.id, reason: 'deferred needs a why saying what is not supported yet' })
      }
    } else if (row.state === 'exempt') {
      if (!EXEMPT.includes(row.id)) failures.push({ row: row.id, reason: `exempt is only for ${listed(EXEMPT)}` })
    } else if (coverage.release && row.state === 'gap') {
      failures.push({ row: row.id, reason: 'a gap row may not reach the release' })
    }
  }

  return failures
}

export function checkGenerated(contract: Contract, generated: Generated): Failure[] {
  const failures: Failure[] = []
  const labels = (entries: readonly PathEntry[]): Set<string> => new Set(entries.map((entry) => entry.path))
  const collect = (declared: Set<string>, produced: readonly string[], reason: string): void => {
    for (const label of produced) {
      if (!declared.has(label)) failures.push({ row: label, reason })
    }
  }

  const paths = labels(contract.paths)
  collect(paths, generated.paths, 'a field of the kind table the contract holds no row for')
  for (const label of paths) {
    if (!generated.paths.includes(label)) failures.push({ row: label, reason: 'a path the kind table does not produce' })
  }

  const objects = labels(contract.objects)
  collect(objects, generated.objects, 'an object kind of the kind table the contract holds no row for')
  for (const id of pathIds(contract.objects)) {
    if (!generated.values.includes(id)) failures.push({ row: id, reason: 'an object row the kind table does not produce' })
  }

  const ids = new Set(rowIds(contract))
  for (const id of generated.values) {
    if (!ids.has(id)) failures.push({ row: id, reason: 'a value class of the kind table the contract holds no row for' })
  }

  const rules = new Set(contract.rules.map((rule) => rule.id))
  collect(rules, generated.rules, 'a rule of a reader, the compiler or the editor the contract holds no row for')
  for (const id of rules) {
    if (!generated.rules.includes(id)) failures.push({ row: id, reason: 'a rule the readers, the compiler and the editor do not hold' })
  }

  return failures
}

export function checkSourceRuleIds(contract: Contract, named: readonly string[]): Failure[] {
  const rows = new Set(contract.rules.map((rule) => rule.id))
  return named
    .filter((id) => !rows.has(id))
    .map((id) => ({ row: id, reason: 'a rule id a source file names, the contract holds no row for' }))
}

export function checkContract(contract: Contract, known: Coverage & { readonly generated: Generated }): Failure[] {
  return [...checkGenerated(contract, known.generated), ...checkCoverage(contract, known)]
}
