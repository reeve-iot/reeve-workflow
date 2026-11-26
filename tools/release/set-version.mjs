import { readFileSync, readdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import process from 'node:process'

const version = process.argv[2]
if (!version) throw new Error('usage: node tools/release/set-version.mjs <version>')

for (const dir of readdirSync('packages')) {
  const file = join('packages', dir, 'package.json')
  const pkg = JSON.parse(readFileSync(file, 'utf8'))
  pkg.version = version
  for (const field of ['dependencies', 'peerDependencies', 'optionalDependencies']) {
    for (const name of Object.keys(pkg[field] ?? {})) {
      if (name.startsWith('@reeve-iot/workflow-')) pkg[field][name] = version
    }
  }
  writeFileSync(file, `${JSON.stringify(pkg, null, 2)}\n`)
}
