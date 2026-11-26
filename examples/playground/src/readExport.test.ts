import { describe, expect, it } from 'vitest'
import { BUILTIN_PORT_TYPES, createPortTypeRegistry } from '@reeve-iot/workflow-core'
import { readExport } from './readExport.js'

const registry = createPortTypeRegistry([...BUILTIN_PORT_TYPES])

describe('readExport', () => {
  it('refuses a file that repeats a member name, naming it, where JSON.parse would keep the last', () => {
    const read = readExport('{"name":"a","definitionJson":null,"definitionJson":"{}"}', registry)
    expect(read).toEqual({
      ok: false,
      error: "This file is ambiguous JSON: the member name 'definitionJson' occurs more than once in the object at $.",
    })
  })

  it('opens a file whose members are all named once', () => {
    expect(readExport('{"name":"a","definitionJson":null}', registry).ok).toBe(true)
  })
})
