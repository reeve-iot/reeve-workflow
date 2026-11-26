import type { PortCapabilityName } from './capabilities.js'
import type { PortTerm } from './grammar.js'
import type { PortLiteralForm } from './literalForm.js'
import type { PortTypeName } from './name.js'

/** One entry in the open port-type vocabulary, as the server publishes it. Hosts register their own domain types
 * (Device, Room, …) alongside the built-ins at initialisation; nothing in this package enumerates them. The server is
 * the registrar: the registry reads an entry and grades none of its rules. */
export interface PortTypeDefinition {
  /** Canonical name, matched byte for byte on the wire and persisted in saved graphs: a plain name
   * (`[A-Za-z_][A-Za-z0-9_]*`, not beginning with `collection`), or — beside an alias — a `Collection<…>` name. */
  name: PortTypeName
  /** Extra spellings accepted on input, matched ASCII case-insensitively, each a plain name. A plain `name` is
   * always an alias of itself; a collection-shaped one is reached through the aliases given here alone. */
  aliases?: readonly string[]
  /** Types a value of this type may also feed, as the server publishes them. The editor grades no wire, so the registry
   * carries them without reading them. */
  widensTo?: readonly PortTypeName[]
  /** False suppresses the derived `Collection<name>` companion in `list()` and `get()`, a collection-shaped
   * `name` having none anyway. Defaults to true; it does not make `Collection<name>` unknown to `has`. */
  collection?: boolean
  /** The capabilities this type implements, from the server's `GET port-types` (`implements`) or a host's own list. For a
   * type the library owns (`String`, `Int`, `Number`, `Boolean`, `DateTime`, `Json`) the registry ignores it and holds
   * the library's fixed set. Nothing is inferred: a type that lists none implements none. */
  implements?: readonly PortCapabilityName[]
  /** How a literal of this type is graded: the JSON shape it must have and, for a string, how its text is read. Without a
   * form no literal is a literal of this type and the editor writes none. For a type the library owns the registry ignores
   * it and holds the library's fixed form. */
  literal?: PortLiteralForm | null
  /** Free-form label for hosts that render a picker; the registry never interprets it. */
  displayName?: string
}

/** A host's one-argument type constructor, `Name<T>`, as the server publishes it. Read beside the definitions in
 * `createPortTypeRegistry`; `Collection` and `Map` are built in, and a registry refuses a record named either. */
export interface PortTypeConstructorDefinition {
  /** The constructor's name; it may not name a registered type, alias or constructor. */
  name: string
  /** The arguments the constructor admits, each a closed document term, nullable or not (`Boolean`, `Int?`,
   * `Collection<Int>`). An argument is admitted when it is exactly one of these, so `Boolean` does not admit `Boolean?`. */
  allowedArguments: readonly string[]
  /** The capabilities every `Name<T>` implements, whatever `T` is. */
  implements?: readonly PortCapabilityName[]
  /** How a literal of any `Name<T>` is graded, whatever `T` is; see `PortTypeDefinition.literal`. */
  literal?: PortLiteralForm | null
}

/** Why a term is refused by a vocabulary: the compiler's rule id and a sentence naming the type. */
export interface PortTermRefusal {
  code: string
  message: string
}

/** What the server says about the vocabulary beyond its types. */
export interface PortTypeRegistryOptions {
  /** True when the host registered a secret codec (`GET port-types` answers `secretCodec`). Absent reads as no codec. */
  readonly secretCodec?: boolean
}

export interface PortTypeRegistry {
  /** True when the host can protect a secret-capable entry, so the editor offers the hide toggle; false when it cannot. */
  readonly secretCodec: boolean
  /** True when `type` is a well-formed name whose element is a registered name or alias, or which wraps a registered
   * `Collection<…>` name at least as deep as that name is; false for a malformed name and for `Any`. */
  has(type: PortTypeName): boolean
  /** Every registered type, then each derived `Collection<name>` companion, in registration order. */
  list(): readonly PortTypeDefinition[]
  /** The definition a name `list()` returns stands for, and the alias that names it, a registered `Collection<…>`
   * name included; undefined for any other name, a collection of an alias and a deeper collection among them. */
  get(type: PortTypeName): PortTypeDefinition | undefined
  /** The canonical spelling of a well-formed name: every atom resolved through the aliases, e.g. `Collection<device>` → `Collection<Device>`. A malformed or blank name comes back unchanged and
   * is never repaired, so a graph saved against a newer host still round-trips through an older editor. */
  normalize(type: PortTypeName): PortTypeName
  /** Whether the closed `term` implements `capability`: a head by its `implements`, `Collection<T>` and `Map<String,T>` by `T` (only a
   * collection is `Iterable`), `T?` only `JsonSerializable`. Throws for an unregistered term or a type parameter: ask `refusalOf` first. */
  implementsCapability(term: PortTerm, capability: PortCapabilityName): boolean
  /** The form that grades a literal of the registered atom or constructor `head` (the library's own for `String`, `Boolean`,
   * `Int`, `Number`, `DateTime` and `Json`, the record's otherwise), or undefined when it declares none, is `Collection` or `Map`
   * or is not registered. Aliases are not resolved: ask with the canonical name. */
  literalFormOf(head: string): PortLiteralForm | undefined
  /** Whether `value` is a literal of the declared type `type`: `isLiteralCoercible` over this registry's forms, with aliases
   * and case variants read as the names they spell. A term that names a type parameter answers true, graded once it is
   * bound; a name that is not well formed answers false. The server answers the same, graded by `contracts/literal-coercion.json`. */
  isLiteralOf(value: unknown, type: PortTypeName): boolean
  /** The constructors a host registered, sorted by name. */
  constructors(): readonly PortTypeConstructorDefinition[]
  /** Why `term` is not made of registered types, or undefined when it is: an atom this registry does not know, a
   * constructor head no host registered, or an argument outside its head's allowed arguments, each `port-type-unregistered`.
   * `Collection` and `Map` admit any argument, and a bare type parameter argument (`Gauge<$T>`) is admitted here and judged
   * when it is bound; a parameter nested in an argument is not admitted. `has` still
   * answers false for a name made of a constructor. */
  refusalOf(term: PortTerm): PortTermRefusal | undefined
}
