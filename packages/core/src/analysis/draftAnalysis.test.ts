import { describe, expect, it } from 'vitest'
import type { DraftAnalysis, DraftPortAnalysis } from './draftAnalysis.js'
import { displayPortTerm, draftAnalysisFreshness, indexDraftAnalysis, isMapTerm, isStringMapTerm, literalWriteTerm, namesTypeParameter, openTermLiteralType } from './draftAnalysis.js'

const bound: DraftPortAnalysis = { name: 'Collection', declared: 'Collection<$T?>', effective: 'Collection<String?>', state: 'bound', unbound: [] }
const unbound: DraftPortAnalysis = { name: 'Collection', declared: 'Collection<$T?>', effective: null, state: 'unbound', unbound: ['T'] }
const refused: DraftPortAnalysis = { name: 'Count', declared: 'Int', effective: null, state: 'refused', unbound: [] }

const analysis: DraftAnalysis = {
  revision: 4,
  registryGeneration: 0,
  diagnostics: [
    { ruleId: 'wire-type-mismatch', message: 'Text cannot feed Int', nodeId: 'a', portName: 'Count', canvasPath: [] },
    { ruleId: 'required-input-unfed', message: 'Nothing feeds Items', nodeId: 'a', portName: 'Items', canvasPath: [] },
    { ruleId: 'x', message: 'inner', nodeId: 'a', portName: null, canvasPath: [{ nodeId: 'loop', branchName: 'Body' }] },
  ],
  nodes: [
    { canvasPath: [], nodeId: 'a', typeArguments: { T: null }, inputs: [unbound, refused], outputs: [] },
    { canvasPath: [{ nodeId: 'loop', branchName: 'Body' }], nodeId: 'a', typeArguments: {}, inputs: [bound], outputs: [] },
  ],
}

describe('the three states of a draft analysis', () => {
  it('is none before an answer, current for the draft revision and stale for an earlier one', () => {
    expect(draftAnalysisFreshness(null, 4)).toBe('none')
    expect(draftAnalysisFreshness(analysis, 4)).toBe('current')
    expect(draftAnalysisFreshness(analysis, 5)).toBe('stale')
  })
})

describe('displayPortTerm', () => {
  it('shows the declared term with no answer, and a parameter as unbound', () => {
    expect(displayPortTerm('Collection<$T?>', undefined, 'none')).toMatchObject({ text: 'Collection<T?> (unbound)', knowledge: 'unbound', stale: false })
    expect(displayPortTerm('$T', undefined, 'none')?.text).toBe('T (unbound)')
    expect(displayPortTerm('Int', undefined, 'none')).toMatchObject({ text: 'Int', knowledge: 'declared' })
  })

  it('shows the effective term of a current answer', () => {
    expect(displayPortTerm('Collection<$T?>', bound, 'current')).toMatchObject({ text: 'Collection<String?>', knowledge: 'bound', stale: false })
  })

  it('shows T (unbound) where the answer leaves a parameter unbound', () => {
    expect(displayPortTerm('Collection<$T?>', unbound, 'current')).toMatchObject({ text: 'Collection<T?> (unbound)', knowledge: 'unbound' })
  })

  it('marks a refused port with its findings', () => {
    expect(displayPortTerm('Int', refused, 'current', ['Text cannot feed Int'])).toEqual({
      text: 'Int (refused)', knowledge: 'refused', stale: false, findings: ['Text cannot feed Int'],
    })
  })

  it('marks the last answer stale and never shows it as current', () => {
    expect(displayPortTerm('Collection<$T?>', bound, 'stale')).toMatchObject({ text: 'Collection<String?>', knowledge: 'bound', stale: true })
  })

  it('falls back to the declared term for a port the answer does not list', () => {
    expect(displayPortTerm('String', undefined, 'current')).toMatchObject({ text: 'String', knowledge: 'declared', stale: false })
  })
})

describe('displayPortTerm for a name only a passthrough entry gives', () => {
  const nameless = (effective: string | null, state: DraftPortAnalysis['state']): DraftPortAnalysis =>
    ({ name: 'Label', declared: null, effective, state, unbound: [] })

  it('shows nothing before an answer, since no declaration states its term', () => {
    expect(displayPortTerm(undefined, undefined, 'none')).toBeUndefined()
    expect(displayPortTerm(null, undefined, 'current')).toBeUndefined()
  })

  it('shows the effective term of an answer, marked stale once the draft has moved on', () => {
    expect(displayPortTerm(undefined, nameless('String', 'bound'), 'current')).toEqual({ text: 'String', knowledge: 'bound', stale: false, findings: [] })
    expect(displayPortTerm(undefined, nameless('String', 'bound'), 'stale')).toMatchObject({ text: 'String', stale: true })
  })

  it('marks a refused name with its findings, without a declaration to repeat', () => {
    expect(displayPortTerm(undefined, nameless(null, 'refused'), 'current', ['has no type'])).toEqual({
      text: 'refused', knowledge: 'refused', stale: false, findings: ['has no type'],
    })
  })
})

describe('indexDraftAnalysis', () => {
  const index = indexDraftAnalysis(analysis)

  it('finds a node by canvas and id, so one id on two canvases stays two nodes', () => {
    expect(index.node([], 'a')?.inputs).toHaveLength(2)
    expect(index.node([{ nodeId: 'loop', branchName: 'Body' }], 'a')?.inputs).toEqual([bound])
    expect(index.node([], 'missing')).toBeUndefined()
  })

  it('finds a port on one side of a node', () => {
    expect(index.port([], 'a', 'inputs', 'Count')).toBe(refused)
    expect(index.port([], 'a', 'outputs', 'Count')).toBeUndefined()
  })

  it('lists the findings of a node, narrowed to a port when one is named', () => {
    expect(index.findings([], 'a')).toEqual(['Text cannot feed Int', 'Nothing feeds Items'])
    expect(index.findings([], 'a', 'Count')).toEqual(['Text cannot feed Int'])
    expect(index.findings([{ nodeId: 'loop', branchName: 'Body' }], 'a')).toEqual(['inner'])
  })

  it('answers nothing for no analysis', () => {
    expect(indexDraftAnalysis(null).node([], 'a')).toBeUndefined()
  })
})

describe('namesTypeParameter', () => {
  it('is true only for a declaration term holding $', () => {
    expect(namesTypeParameter('Collection<$T?>')).toBe(true)
    expect(namesTypeParameter('Collection<String>')).toBe(false)
  })
})

describe('literalWriteTerm', () => {
  it('is the declared term where it is closed, whatever the answer says', () => {
    expect(literalWriteTerm('Int', undefined)).toBe('Int')
    expect(literalWriteTerm('Int', refused)).toBe('Int')
  })

  it('is the effective term of the last answer where the declared term names a parameter', () => {
    expect(literalWriteTerm('Collection<$T?>', bound)).toBe('Collection<String?>')
  })

  it('is the declared term with the parameters the node binds, where the port itself ends with no term', () => {
    expect(literalWriteTerm('$T', refused, { T: 'Int' })).toBe('Int')
    expect(literalWriteTerm('Collection<$T?>', unbound, { T: 'Int' })).toBe('Collection<Int?>')
    expect(literalWriteTerm('$T', unbound, { T: null })).toBeUndefined()
  })

  it('is nothing where no answer binds the parameter: the literal is then text', () => {
    expect(literalWriteTerm('$T', unbound)).toBeUndefined()
    expect(literalWriteTerm('Collection<$T?>', undefined)).toBeUndefined()
  })
})

describe('openTermLiteralType', () => {
  it.each([['Collection<$T>', 'Json'], ['Collection<String>', 'Json'], ['Collection<Collection<$T>>', 'Json']])('writes %s as parsed JSON', (term, written) => {
    expect(openTermLiteralType(term)).toBe(written)
  })

  it.each(['Map<String,Int>', 'Map<String,Int>?', 'Map<string,$T>', 'Map<String,$T?>?', 'Map<String,Collection<$T>>'])(
    'writes %s as parsed JSON, a map being an object whether or not it is nullable', (term) => {
      expect(openTermLiteralType(term)).toBe('Json')
    })

  it.each(['$T', '$T?', 'Collection<$T>?', 'Json', 'String', 'Int??', 'Map', 'Map<Int,Int>', 'Gauge<Map<String,Int>>', 'Map<String,Int'])('writes %s as text', (term) => {
    expect(openTermLiteralType(term)).toBe('String')
  })
})

describe('isMapTerm and isStringMapTerm', () => {
  it.each(['Map<String,String>', 'Map<string,STRING>', 'Map<String,Int>', 'Map<String,Int>?', 'Map<String,$T>'])('reads %s as a map', (term) => {
    expect(isMapTerm(term)).toBe(true)
  })

  it.each(['Json', 'Collection<String>', 'Map', 'Map<Int,Int>', 'Gauge<Map<String,Int>>', 'Map<String,String'])('does not read %s as a map', (term) => {
    expect(isMapTerm(term)).toBe(false)
  })

  it.each(['Map<String,String>', 'Map<string,STRING>'])('draws %s as key/value rows of text', (term) => {
    expect(isStringMapTerm(term)).toBe(true)
  })

  it.each(['Map<String,String>?', 'Map<String,String?>', 'Map<String,Int>', 'Map<String,$T>', 'Map<String,Collection<String>>', 'Collection<String>', 'String', 'Map'])(
    'leaves %s to the JSON text editor or another control', (term) => {
      expect(isStringMapTerm(term)).toBe(false)
    })
})
