import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { findRepeatedMember } from './repeatedMember.js'

interface Row {
  name: string
  text: string
  repeat: { name: string | null; path: string } | null
}

const { rows } = JSON.parse(readFileSync('contracts/repeated-member.json', 'utf8')) as { rows: Row[] }

describe('findRepeatedMember', () => {
  it.each(rows.filter((row) => row.repeat !== null).map((row) => [row.name, row] as const))('finds %s', (_, row) => {
    expect(findRepeatedMember(row.text)).toEqual(row.repeat)
  })

  it.each(rows.filter((row) => row.repeat === null).map((row) => [row.name, row] as const))('finds none in %s', (_, row) => {
    expect(findRepeatedMember(row.text)).toBeUndefined()
  })

  it('is not misled by braces, quotes and commas inside a string value', () => {
    expect(findRepeatedMember('{"a": "x\\"}, \\"b\\": 1", "b": 2}')).toBeUndefined()
  })

  it.each(['', '   ', '{', '{"a": 1', '{"a": 1, "b', '[1, {"a":', '"abc', '{"a\\'])('returns for text that ends early: %j', (text) => {
    expect(findRepeatedMember(text)).toBeUndefined()
  })
})
