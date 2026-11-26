import { describe, expect, it } from 'vitest'
import { generateTypes, templateOf, translate } from './generate.js'
import { parseSurface } from './surface.js'
import type { Surface } from './surface.js'

const minimal: Surface = {
  formatVersion: 1,
  source: 'test',
  endpoints: [
    {
      operation: 'getThing',
      method: 'GET',
      path: 'api/things/{id:guid}',
      pathParameters: [{ name: 'id', type: 'uuid', required: true }],
      queryParameters: [{ name: 'depth', type: 'integer', required: false }],
      responses: [
        { status: 200, type: '#Thing' },
        { status: 404, type: '#ProblemDetails' },
      ],
    },
  ],
  types: [{ name: 'Thing', properties: [{ name: 'id', type: 'uuid' }] }],
}

describe('translate', () => {
  it('reads a trailing ? as binding looser than the array it follows', () => {
    expect(translate('#Thing[]?')).toBe('ReadonlyArray<Thing> | null')
    expect(translate('#Thing?[]')).toBe('ReadonlyArray<Thing | null>')
  })

  it('erases the difference between the formats a date and an id arrive in', () => {
    expect(translate('uuid')).toBe('string')
    expect(translate('date-time')).toBe('string')
  })

  it('refuses a term it does not know rather than emitting a name nothing declares', () => {
    expect(() => translate('duration')).toThrow(/Unknown type term/)
  })

  it('nests a map inside an array without losing either', () => {
    expect(translate('map<#Thing>[]')).toBe('ReadonlyArray<Readonly<Record<string, Thing>>>')
  })
})

describe('templateOf', () => {
  it('drops the route constraint a caller has no way to satisfy', () => {
    expect(templateOf('api/workflows/{id:guid}/versions/{versionNumber:int}')).toBe(
      '/api/workflows/{id}/versions/{versionNumber}',
    )
  })
})

describe('generateTypes', () => {
  it('makes an optional query parameter optional and a path parameter not', () => {
    const emitted = generateTypes(minimal)
    expect(emitted).toContain('readonly id: string')
    expect(emitted).toContain('readonly depth?: number')
  })

  it('names only the success response as the result, whatever else is declared', () => {
    expect(generateTypes(minimal)).toContain('readonly result: Thing')
  })

  it('produces the same text twice, so a regeneration is not a diff', () => {
    expect(generateTypes(minimal)).toBe(generateTypes(minimal))
  })

  it('types a 204 as void and tells the client to expect no body', () => {
    const bodiless: Surface = {
      ...minimal,
      endpoints: [{ ...minimal.endpoints[0]!, responses: [{ status: 204 }] }],
    }
    const generated = generateTypes(bodiless)
    expect(generated).toContain('readonly result: void')
    expect(generated).toContain('empty: true')
  })

  it('refuses any other bodiless success, rather than typing it as void as well', () => {
    const bodiless: Surface = {
      ...minimal,
      endpoints: [{ ...minimal.endpoints[0]!, responses: [{ status: 200 }] }],
    }
    expect(() => generateTypes(bodiless)).toThrow(/success with no shape/)
  })

  it('refuses an endpoint with two success responses rather than picking one', () => {
    const ambiguous: Surface = {
      ...minimal,
      endpoints: [
        {
          ...minimal.endpoints[0]!,
          responses: [
            { status: 200, type: '#Thing' },
            { status: 201, type: '#Thing' },
          ],
        },
      ],
    }
    expect(() => generateTypes(ambiguous)).toThrow(/success responses/)
  })
})

describe('parseSurface', () => {
  it('refuses a format version it was not written against', () => {
    expect(() => parseSurface({ ...minimal, formatVersion: 2 })).toThrow(/format 2/)
  })

  it('refuses a document with no endpoints, which would generate an empty client silently', () => {
    expect(() => parseSurface({ ...minimal, endpoints: [] })).toThrow(/no endpoints/)
  })
})
