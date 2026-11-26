import { describe, expect, it } from 'vitest'
import { parseDynamicPorts } from './dynamicPorts.js'

describe('parseDynamicPorts', () => {
  it('reads the declared ports', () => {
    const ports = parseDynamicPorts('[{"name":"A","displayName":"Port A","type":"Device","isRequired":true}]')
    expect(ports).toEqual([{ name: 'A', displayName: 'Port A', type: 'Device', isRequired: true }])
  })

  it('yields no ports rather than throwing on malformed JSON', () => {
    expect(parseDynamicPorts('{ not json')).toEqual([])
  })

  it('yields no ports when the payload is not a list', () => {
    expect(parseDynamicPorts('{"name":"A"}')).toEqual([])
  })

  it.each([[5], [[{ name: 'A', type: 'Int' }]], [{ name: 'A' }]])('yields no ports for a value that is not a string, %j', (value) => {
    expect(parseDynamicPorts(value as unknown as string)).toEqual([])
  })

  it('treats an absent configuration entry as no ports', () => {
    expect(parseDynamicPorts(undefined)).toEqual([])
  })

  it('falls back to the port name when no display name was given', () => {
    expect(parseDynamicPorts('[{"name":"A"}]')[0]?.displayName).toBe('A')
  })

  it('reads an untyped port as blank, for the validator to report, rather than inventing a type', () => {
    expect(parseDynamicPorts('[{"name":"A"}]')[0]?.type).toBe('')
  })

  it('reads isRequired only from true, so a written false is not required', () => {
    expect(parseDynamicPorts('[{"name":"A","isRequired":false},{"name":"B","isRequired":true}]').map((p) => p.isRequired)).toEqual([false, true])
  })

  it('refuses a display name that is not text with the rest of the port list', () => {
    expect(parseDynamicPorts('[{"name":"A","displayName":5}]')).toEqual([])
  })

  it('keeps a malformed type exactly as written', () => {
    expect(parseDynamicPorts('[{"name":"A","type":" CollectionInt"}]')[0]?.type).toBe(' CollectionInt')
  })

  it.each([
    ['a type that is not text', '[{"name":"A","type":5}]'],
    ['a name that is not text', '[{"name":7,"type":"Int"}]'],
    ['a null entry', '[null]'],
    ['a readable entry beside an unreadable one', '[{"name":"A","type":"Int"},{"Name":"B"}]'],
    ['two keys of one name', '[{"name":"A","name":"B","type":"Int"}]'],
  ])('yields no ports for %s', (_, json) => {
    expect(parseDynamicPorts(json)).toEqual([])
  })
})
