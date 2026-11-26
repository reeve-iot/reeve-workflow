// @vitest-environment node
import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

const root = new URL('../../../', import.meta.url)

describe('root tsconfig.json project references', () => {
  it('name every example workspace that has a tsconfig of its own', () => {
    const tsconfig = JSON.parse(readFileSync(new URL('tsconfig.json', root), 'utf8')) as { references: { path: string }[] }
    const referenced = tsconfig.references.map((r) => r.path.replace(/^\.\//, ''))
    const examples = readdirSync(new URL('examples/', root))
      .filter((dir) => existsSync(new URL(`examples/${dir}/tsconfig.json`, root)))
      .map((dir) => `examples/${dir}`)

    expect(examples.length).toBeGreaterThan(0)
    for (const example of examples) expect(referenced).toContain(example)
  })
})
