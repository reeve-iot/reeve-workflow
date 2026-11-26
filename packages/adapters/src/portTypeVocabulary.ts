import type { PortTypeVocabularyResponse } from '@reeve-iot/workflow-client'
import { describePortTypeSyntaxError, isPortCapabilityName, literalFormRefusal, parsePortTypeName } from '@reeve-iot/workflow-core'
import type { PortCapabilityName, PortLiteralForm, PortTypeConstructorDefinition, PortTypeDefinition } from '@reeve-iot/workflow-core'

function implementsOf(response: PortTypeVocabularyResponse, name: string, listed: readonly string[]): PortCapabilityName[] {
  const published = new Set(response.capabilities.map((c) => c.name))
  return listed.map((capability) => {
    if (!published.has(capability) || !isPortCapabilityName(capability)) {
      throw new Error(`The server published '${name}' as implementing '${capability}', which is not a capability of its vocabulary this editor knows.`)
    }
    return capability
  })
}

function literalOf(name: string, published: { readonly gradedAs: string; readonly format: string | null } | null): PortLiteralForm | null {
  if (published === null) return null
  const form = { gradedAs: published.gradedAs, format: published.format } as PortLiteralForm
  const why = literalFormRefusal(form)
  if (why !== undefined) throw new Error(`The server published '${name}' with a literal form this editor cannot grade: ${why}.`)
  return form.format === null ? { gradedAs: form.gradedAs } : form
}

/** The server's vocabulary (`getPortTypes`) as `createPortTypeRegistry`'s definitions, `implements` included; throws,
 * naming it, for a name that does not parse, a capability this editor does not know or a literal form outside the
 * closed set. */
export function readPortTypeVocabulary(response: PortTypeVocabularyResponse): PortTypeDefinition[] {
  const definitions: PortTypeDefinition[] = []
  for (const type of response.types) {
    const parsed = parsePortTypeName(type.name)
    if (!parsed.ok) throw new Error(`The server published port type '${type.name}', which does not parse: ${describePortTypeSyntaxError(type.name, parsed)}`)
    if (parsed.depth > 0) {
      if (type.aliases.length > 0) definitions.push({ name: type.name, aliases: [...type.aliases] })
      continue
    }
    definitions.push({
      name: type.name,
      aliases: [...type.aliases],
      widensTo: [...type.widensTo],
      implements: implementsOf(response, type.name, type.implements),
      literal: literalOf(type.name, type.literal),
    })
  }
  return definitions
}

const BUILT_IN_CONSTRUCTORS: ReadonlySet<string> = new Set(['Collection', 'Map'])
const MAP_ARITY = 2
const MAP_KEY = 'String'

/** The host constructors as `createPortTypeRegistry`'s second argument, built-in `Collection` and `Map` left out. Throws for one
 * that lists no argument, and for a `Map` published with an arity or key this editor's grammar does not read. */
export function readPortTypeConstructors(response: PortTypeVocabularyResponse): PortTypeConstructorDefinition[] {
  for (const constructor of response.constructors) {
    const published: { arity?: number; key?: string | null } = constructor
    if (constructor.name === 'Map' && (published.arity !== undefined || published.key !== undefined) && (published.arity !== MAP_ARITY || published.key !== MAP_KEY)) {
      throw new Error(`The server published built-in constructor 'Map' with arity ${String(published.arity)} and key ${String(published.key)}, which this editor cannot read.`)
    }
  }
  return response.constructors
    .filter((constructor) => !BUILT_IN_CONSTRUCTORS.has(constructor.name))
    .map((constructor) => {
      const allowed = constructor.allowedArguments
      if (allowed === null || allowed.length === 0) {
        throw new Error(`The server published port type constructor '${constructor.name}' with no allowed argument.`)
      }
      return {
        name: constructor.name,
        allowedArguments: [...allowed],
        implements: implementsOf(response, constructor.name, constructor.implements),
        literal: literalOf(constructor.name, constructor.literal),
      }
    })
}
