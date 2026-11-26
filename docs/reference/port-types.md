# Port types

Every port of a block, every workflow parameter and every dynamic port has a type. The library ships the types
every host shares and nothing domain-shaped: a host registers its own (`Device`, `Shipment`, ...) at start-up, on the
server and in the editor.

This page is for a developer who registers port types and for the author of a graph. How the compiler reports a
problem is listed in [validation-findings.md](validation-findings.md).

## Built-in types

| Type | Holds | Literal |
|---|---|---|
| `String` | text | JSON string |
| `Number` | double-precision number | finite JSON number |
| `Int` | 64-bit integer | JSON number with an integral value in the range of a `long` |
| `Boolean` | true or false | JSON `true` or `false` |
| `DateTime` | `DateTimeOffset` | JSON string |
| `Json` | any JSON value | any JSON value except a top-level `null` |

The built-in constructors are `Collection<T>`, `Map<String,V>` and, on a block's input only, `Expression<T>`.
Every type can be made nullable with a trailing `?`: `Int?`, `Collection<String>?`.

Port-type names are stored inside saved graphs, which is why the vocabulary is a registry and not a fixed enum:
an older build can still load a graph that a newer host saved.

## Names

A type name is written with no white space:

```text
Int                      an atom
Int?                     a nullable atom
Collection<Int>          a list
Map<String,Collection<Int?>>?
```

Parsing a name never exhausts the stack, whatever its length or nesting: it is read or refused with a fault.

An atom is `[A-Za-z_][A-Za-z0-9_]*`. In a stored document or an API value the name is one of these shapes: an atom,
`Name<T>` with one argument, or `Map<String,V>`, each optionally followed by `?`. A block or plugin declaration may
also write a type parameter such as `$T` (see [Type parameters](#type-parameters)); in a stored document `$` is an
illegal character.

### Grammar

```text
type        = ( atom | "Collection<" type ">" | atom "<" type ">" | "Map<" key "," type ">" ) [ "?" ]
key         = "String", compared ignoring ASCII case
document    = type
declaration = type, where an atom may also be written "$" atom (a type parameter)
```

`document` is the form of a stored document, an API value and a `typeArguments` value; `declaration` is the form of a
block or plugin declaration.

Rules for every name:

- The keywords `Collection` and `Map` match exactly, so `collection<Int>` is refused.
- Other atoms compare ignoring ASCII case: `Shipment` and `shipment` are one type. Only `A`-`Z` fold.
- A name is never trimmed or repaired. `Pt ` and `Collection< Int >` are refused.
- `Collection<Collection<Int>>` is a list of lists, a different type from `Collection<Int>`.
- A name that starts with `collection` in any case, other than `Collection<`, is reserved. This also refuses the
  concatenated spelling `CollectionInt`; the message says to write `Collection<T>` with the type of its elements.
- A map's key is the atom `String`, not nullable, with the value after one comma and no space: `Map<String,Int>`.
  The key is read ignoring case, so `Map<string,Int>` parses, but a stored document must spell it `String`: any other
  spelling is refused as `port-type-alias`. `Map<String,Json>` is refused like `Collection<Json>`.
- A level is nullable once: `Int??` is refused.
- A type parameter is a leaf: `$T<Int>` is refused (`UnexpectedOpen`). The reserved `collection` prefix does not apply
  to a parameter's name.

### Syntax faults

A name that is not a sentence of the grammar is reported with a fault and the index where it stops being one
(after a `$`, the index of the identifier). In C#, `PortValueType.Of` throws a `PortTypeSyntaxException` that
carries the fault and position, and `PortValueType.TryParse` returns them. In TypeScript `parsePortTypeName`
returns the same fault and position.

| Fault | When |
|---|---|
| `Absent` | The type is missing (property absent or `null`). |
| `Empty` | The text is empty. |
| `IllegalCharacter` | White space, a control character, a non-ASCII character, punctuation other than `< > ? ,` where the grammar allows it, or `$` in a stored term. |
| `ExpectedTypeName` | A name was expected and there is a bare `<`, `>` or `?`, or the text ended. |
| `CollectionWithoutBracket` | `Collection` is not followed by `<`. |
| `ReservedCollectionPrefix` | An identifier starts with `collection` in any case and is not `Collection<`. |
| `MissingClose` | The text ended before every `Name<` found its `>`. |
| `UnexpectedOpen` | A `<` where only `>` or the end was expected. |
| `UnexpectedClose` | A `>` with nothing left to close. |
| `DoubleNullable` | A second `?` right after one. |
| `MapWithoutBracket` | `Map` is not followed by `<`. |
| `MapKey` | The first argument of `Map` is not the atom `String` (another name, `String?`, a constructor or a parameter). |
| `MapWithoutValue` | The key is not followed by a comma and a value type: `Map<String>`. |

### Which error code you see

| Where the name is written | Code |
|---|---|
| A stored name that is malformed, blank or absent (dynamic port, declared output, workflow parameter, type argument, projection type) | `port-type-malformed` |
| A stored `Map` whose key is not `String` (the `MapKey` fault) | `port-type-map-key` |
| A stored name that is well formed but nothing registered it | `port-type-unregistered` |
| A stored alias or a case variant of a registered name (`string`), or a stored `Any` or `Float` | `port-type-alias` |
| `Json` where it is not allowed | `port-type-json-position` |
| A block or plugin declaration that breaks any of the above | `block-port-declaration-invalid` |
| A registration record that breaks a rule | `port-type-registration-invalid` or `port-type-name-clash` |

A `type` that is present but not a string, a dynamic-port entry that is not an object, or a dynamic-port entry whose
`name` is not a string makes the stored document unreadable: `definition-unreadable` for a definition,
`signature-unreadable` for a parameter list (see
[stored-document refusals](validation-findings.md#stored-document-refusals)). A port whose type is malformed still
exists by name with no type, so a wire naming it reports nothing further.

## Registering types

### In TypeScript

The editor reads a registry once, when it mounts. Build it with `createPortTypeRegistry`:

```ts
import { BUILTIN_PORT_TYPES, createPortTypeRegistry } from '@reeve-iot/workflow-core'

export const portTypes = createPortTypeRegistry([
  ...BUILTIN_PORT_TYPES,
  { name: 'Device', aliases: ['device'], displayName: 'Device' },
  { name: 'Room', aliases: ['room'], displayName: 'Room' },
  { name: 'Scene', aliases: ['scene'] },
])
```

The result is immutable. A type registered on the server later is missing from the editor until the page reloads.

Because the server is the source of truth, a host normally seeds the editor from `GET {root}/port-types` with
`readPortTypeVocabulary` and `readPortTypeConstructors` from `@reeve-iot/workflow-adapters`, instead of restating
its types in TypeScript. The response lists the registry `generation`, every type with its owner, aliases,
widenings, capabilities and literal form, every constructor, and whether the host has a secret codec.

A definition has these fields:

| Field | Meaning |
|---|---|
| `name` | The canonical name. It is persisted in saved graphs, so renaming it breaks every stored graph that holds the old name. |
| `aliases` | Other spellings of the type, compared ignoring ASCII case. A plain name is always its own alias. |
| `widensTo` | Types a value of this type may also feed (see [Widening](#widening-and-assignability)). |
| `collection` | `false` leaves out the `Collection<name>` companion in `list()` and `get()`. Defaults to true. |
| `literal` | The literal form, `{ gradedAs, format? }` (see [Literals](#literals)). Without one, no literal is a literal of this type. |
| `implements` | The capabilities the type has (see [Capabilities](#capabilities)). Nothing is inferred. |
| `displayName` | A label for host-rendered pickers. The registry does not read it. |

The library's own types ignore `literal` and `implements` and keep their fixed values.

`createPortTypeRegistry` throws `port-type-name-clash` when a canonical name is registered twice or an alias is
claimed by two types. It throws `port-type-registration-invalid` for a name that does not parse, for `Map` in any case
as a name, alias or constructor name, and for an allowed argument that is not a closed term. A constructor whose name a
type or another constructor already holds is `port-type-name-clash`. It grades nothing else: the server refuses the
other bad records when it registers them.

A host constructor goes in the second argument:

```ts
createPortTypeRegistry(types, [{ name: 'Gauge', allowedArguments: ['Boolean', 'Number'] }], {
  secretCodec: response.secretCodec,
})
```

### In C#

A host registers records at start-up. One `WorkflowTypeRecord` is one atom; one `WorkflowTypeConstructorRecord` is
one constructor.

```csharp
services.AddWorkflows(workflows =>
{
    workflows.Seams.PortTypes.Add(new WorkflowTypeRecord("Shipment", typeof(string))
    {
        Aliases = ["Consignment"],
        WidensTo = ["Parcel"],
        Claims = new HashSet<WorkflowCapability> { WorkflowCapability.Equatable, WorkflowCapability.JsonSerializable },
        JsonTypeInfo = JsonSerializerOptions.Default.GetTypeInfo(typeof(string)),
        Literal = new WorkflowLiteralForm(WorkflowLiteralKind.String),
    });

    workflows.Seams.PortTypes.Add(new WorkflowTypeRecord("Parcel", typeof(IComparable)));

    workflows.Seams.PortTypes.Add(new WorkflowTypeConstructorRecord("Gauge", typeof(double))
    {
        AllowedArguments = ["Boolean", "Number", "String"],
    });
});
```

A record the vocabulary refuses stops the host at start-up with a `WorkflowTypeRegistrationException` naming it.
Order does not matter: a record may name a type added after it.

A plugin registers its own records through `IWorkflowTypeRegistry.Register(owner, records)`; `Unregister(owner)`
removes all records of one owner at once. The owner must be `plugin:<id>`, otherwise both methods throw an
`ArgumentException`. `Register` does not throw for a refused record: it returns a `WorkflowTypeRegistrationResult`
that lists the refusals, and then applies none of the records.

`WorkflowTypeRecord` fields:

| Field | Meaning |
|---|---|
| `Name` | A plain identifier. See the rules below. |
| `ClrType` | The CLR type a value has. `object` and `ValueType` are refused. |
| `Aliases` | Other spellings, each a plain identifier. |
| `WidensTo` | Types a value may also feed. |
| `Claims` | Capabilities, verified against `ClrType`. |
| `JsonTypeInfo` | How a value is written as JSON. Required by a literal form and by a `JsonSerializable` claim. |
| `Literal` | The literal form. |

`WorkflowTypeConstructorRecord` has `Name`, `Erasure` (the one CLR type every `Name<T>` has), `AllowedArguments`,
`Claims`, `JsonTypeInfo` and `Literal`.

A name or alias is refused with `port-type-registration-invalid` when it is not a plain identifier, starts with
`collection` in any case, or is `Map`, `Any`, `Float` or `Expression` in any case, and when an alias is the name
itself in any case (`Shipment` with the alias `shipment`). It is refused with `port-type-name-clash` when a
library atom, alias or constructor, or a record registered earlier, already holds it in any case. The record registered second loses.

A reference to another type, in `WidensTo` or `AllowedArguments`, must be spelled canonically and name a type owned by
the library, by the host or by the record's own owner. A plugin can therefore name library and host types and its
own, never another plugin's. Anything else is `port-type-registration-invalid`. Host types never unregister, so a
reference never outlives the type it names.

A `WidensTo` row is also refused with `port-type-registration-invalid` when its target is not registered, is a
library type (`String`, `Int`, `Number`, ... no host row may lead into one), is the type itself, or has a CLR type
that the source's `ClrType` is not reference-assignable to. In the example, `Shipment` may widen to `Parcel` because
a `string` is an `IComparable`.

### Aliases and canonical names

Compilation accepts only the canonical spelling. A stored alias or case variant of a registered name (`string` for
`String`) is refused as `port-type-alias`, and the message names the canonical spelling to write. Stored documents
are never rewritten.

In the editor, `registry.normalize(name)` returns the canonical spelling of a well-formed name
(`Collection<device>` becomes `Collection<Device>`). It returns an unknown or malformed name unchanged, on purpose,
so a graph saved by a newer host still round-trips through an older editor. `registry.has(name)` is stricter: it is
false for a name this build cannot render, so a picker does not offer it.

A well-formed name nobody registered is kept by the editor and refused by the compiler as `port-type-unregistered`.

### Reserved names

`Any` and `Float` are reserved and no host may register them. Neither is a wildcard. Wherever one appears, at any
depth and in any case, it is refused:

| Where | Code |
|---|---|
| A stored type or a plugin's port type | `port-type-alias` |
| A registration | `port-type-registration-invalid` |
| A block declaration, read when the server loads its catalogue | `block-port-declaration-invalid` |

`Expression`, `Map` and names starting with `collection` are reserved too and are refused at registration the same
way. A numeric port-type value (an enum ordinal) in a saved `BlockPortDefinition` is refused, because it cannot be
mapped back to a name.

### Compile-time names

In TypeScript `PortTypeName` is an open union: known names autocomplete and any other string is accepted. A host can
add its names by declaration merging, which is optional and affects autocomplete only:

```ts
declare module '@reeve-iot/workflow-core' {
  interface PortTypeVocabulary {
    Device: true
    Room: true
  }
}
```

## Nullability

`T?` accepts the literal `null` and every value of `T`. A non-null term never accepts a nullable one, so
`Int?` does not feed an `Int` port. Substituting a nullable binding into `$T?` gives a single `?`.

## Collection and Map

`Collection<T>` is a list of `T`; `Map<String,V>` is a dictionary from strings to `V`.

- A `Collection<T>` value feeds a `Collection<U>` port when `T` feeds `U`: `Collection<Int>` feeds
  `Collection<Number>`. A collection never feeds a scalar port and a scalar never feeds a collection port.
- `Map<String,Int>` feeds `Map<String,Number>` and `Map<String,Int?>`. The key is fixed, so only the value varies.
  A map has no order.
- Nothing converts between a collection and a map. A `Map` is not iterable, so `IterateOver` refuses one.
- A list is written as a JSON array and a map as a JSON object (see [Literals](#literals)).

## Widening and assignability

Whether a value of one type may feed a port of another is decided by the compiler. The editor shows the compiler's
findings and has no copy of the rules. They are checked in the order A1 to A7, and the first that answers decides:

- **A1.** The two types are the same: yes. (Two collections or two maps go to A5 instead.)
- **A2.** The target is `U?`: `null` is accepted; a `T?` source is accepted when `T` feeds `U`; any other source when it
  feeds `U`. So `Int?` feeds `Number?`.
- **A3.** The source is nullable or `null` and the target is not: no.
- **A4.** The target is `Json`: yes when the source type is `JsonSerializable`. A wire from a `Json` source is never
  checked this way; it goes through a projection (see [Json](#json)).
- **A5.** Both are collections, or both are maps: yes when the source's element (or value) feeds the target's.
- **A6.** A widening row `source -> target` is registered: yes. The library has one, `Int -> Number`.
- **A7.** Otherwise no. Rows do not compose: `X -> Y` and `Y -> Z` do not make `X` feed `Z`, and a row into a
  `JsonSerializable` type does not make `X` feed `Json`.

Constructors a host registers are invariant. Two terms with one host head are assignable only when they are the
same term, so `Gauge<Int>` does not feed `Gauge<Number>`. Only `Collection` and `Map` are covariant.

Host widening rows are one hop and one way, and their targets are plain or closed-constructor terms, never a
`Collection`, a `Map` or a term holding `Json`. A widening is stated on the element: an `Origin` record with
`WidensTo = ["Waypoint"]` lets `Collection<Origin>` feed `Collection<Waypoint>` too. For a list of `Item`, register
`Item` and write `Collection<Item>`. Also, at most one row leads from an atom into any one constructor head.

A host constructor `Gauge<T>` admits an argument only when it is exactly one of its `AllowedArguments`, so
`Boolean` does not admit `Boolean?`. A bare `$T` argument is judged when it is bound, and `Collection<$T>` inside a
constructor argument is not admitted. A term that breaks this is `port-type-unregistered`. A constructor record is
refused when its list is empty or an argument is open, malformed, not canonical (`bool`) or holds `Json`, `Any` or
`Float`.

### What a match costs at run time

The conversion follows from the CLR types:

- `Int` to `Number` converts `long` to `double`.
- A non-nullable value feeding a nullable value-type target is wrapped.
- A collection or map whose element or value needs converting is copied to a new list or dictionary.
- A source feeding `Json` is serialized with the source type's `JsonTypeInfo`. A present value that serializes
  to JSON `null` on a non-null `Json` output fails the node with `block-output-type-mismatch`.

### Least upper bound

When several sources feed one type parameter, the compiler joins them. The join of `Int` and `Number` is `Number`;
`null` or a nullable source makes the result nullable.

| Sources | Join |
|---|---|
| `Int`, `Number` | `Number` |
| `Int`, `Int?` or `Int`, `null` | `Int?` |
| `Int?`, `Number` | `Number?` |
| `Collection<Int>`, `Collection<Number>` | `Collection<Number>` |
| `Collection<Int?>`, `Collection<Number>` | `Collection<Number?>` |
| `Map<String,Int>`, `Map<String,Number>` | `Map<String,Number>` |
| `Json`, `Json?` or `Json`, `null` | `Json?`, usable only as a projection subject |
| `ReeferShipment`, `TankerShipment` (both with a row to `Shipment`) | `Shipment` |
| `Int`, `String` | none |
| `Int`, `Json` | none |
| `Gauge<Boolean>`, `Gauge<Number>` | none (invariant) |
| only `null`, or nothing | none; this means "no lower bound", not a conflict |

The join follows four rules:

- **L1.** `null` and the nullable form of a source are set aside, and the join is nullable when any source was either.
  With no source left there is no join.
- **L2.** Sources that are the same term join to that term.
- **L3.** When every source is a collection, or every source is a map, the join is the collection or map of the join
  of the elements, or values.
- **L4.** Otherwise the join is the one term, among the sources and the targets of their widening rows, that every
  source feeds and that feeds every other such term. `Json` is never a candidate. No such term, or more than one,
  means no join.

A join is `Json` only when every non-null source is `Json` or `Json?`. A mixed join never becomes `Json`. A join
that does not exist is `type-parameter-conflict`, or `expression-type-mismatch` inside an expression.

## Literals

A literal in a mapping, or a value in a node's configuration, is checked against the port it feeds, by the compiler
and by the editor alike. A literal that does not fit is `literal-port-value-invalid`.

| Type | Accepted literal |
|---|---|
| `Boolean` | JSON `true` or `false` |
| `Int` | JSON number with an integral value in `long` range |
| `Number` | finite JSON number |
| `String` | JSON string |
| `DateTime` | JSON string that the `DateTimeOffset` JSON converter reads. Text with no offset is UTC, so a date alone is its midnight UTC. |
| `Json` | any JSON value but a top-level `null` |
| `Collection<T>` | JSON array whose elements are literals of `T` |
| `Map<String,V>` | JSON object whose members are literals of `V`, with no member name repeated |
| host atom or constructor | the registered form, or none |
| `T?` | JSON `null`, or a literal of `T` |
| `Expression<T>` | an `expr:` text whose result type feeds `T` |

Rules:

- No text is read into another type. `"5"` is not an `Int` and `"true"` is not a `Boolean`.
- `null` is a literal of a nullable type and of no other. A `null` member of a map needs a nullable `V`.
- A list is an array and not an object; a map is an object and not an array.
- A string beginning `expr:` is an expression, evaluated before the block reads it and checked against the port by
  its result type (`expression-result-type-mismatch`). On an `Expression<T>` input it is checked against `T` and
  delivered unevaluated.
- A configuration field that names an input is checked against that input's declared type, whatever type the
  field has.
- A port typed with a type parameter is checked once the parameter is bound. The editor leaves it unchecked.

### Literal forms for host types

A host atom or constructor sets `Literal` (C#) or `literal` (TypeScript, published by `GET port-types`). The form
has two parts: how the JSON value is graded, and for a string how its text is read further.

| `GradedAs` | Accepts |
|---|---|
| `String` | JSON string |
| `Boolean` | `true` or `false` |
| `Int` | integral number in `long` range |
| `Number` | finite number |
| `Json` | any JSON value but a top-level `null` |

| `Format` (with `String` only) | Text read as |
|---|---|
| `Uuid` | `8-4-4-4-12` hexadecimal digits of either case |
| `DateTime` | text the `DateTimeOffset` JSON converter reads |

A type with no form has no literal: every literal written for it is refused, an object included. A form needs a
`JsonTypeInfo` on the record, or registration is refused with `port-type-registration-invalid`. The compiler grades
the form and then reads the literal with the record's `JsonTypeInfo`. The editor grades the form only, and checks a
`DateTime` or `Uuid` format only as "is a JSON string".

### In the editor

The editor writes each literal in the form of its port's type and offers no value field for a type with no form. A
`Map<String,String>` is edited as key/value rows and written as an object. A row without a key, or with a key another
row holds, is marked and not written. Any other map is edited as JSON text.

### Secret-capable inputs

A map input that a block marks `secretValues` (such as request headers) can hold hidden entries.

If the host registered an `IWorkflowSecretCodec`, `GET port-types` says so and the editor is built with
`secretCodec: true`. Each row of such an input then has a "Hide" toggle, and a hidden entry is stored as
`protect:<value>`. When it is read back, the host returns `***` for the entry. Without a codec `protect:` is refused
and values are used as written.

What the author sees in a `secretValues` input:

- An entry read back as `***` is shown as hidden. Its key is fixed and its value is read-only.
- "Replace" opens a hidden value box. Until a value is typed the row is marked and nothing is written, so saving
  still carries `***` and the stored secret stays. "Cancel" returns the row to `***`.
- A replaced row starts hidden and is written as `protect:<value>` with a codec, and as typed without one. A
  hidden row left empty is marked, so a stored `protect:` with an empty body cannot be saved.
- The editor does not decide what is sensitive. A matching row typed in clear is sent in clear and read back
  masked by the host.

Only the rows of a `Map<String,String>` input offer hiding. In a `Map<String,String>?` input and in the literal
cells of a mapping, `***` shows as plain text with no toggle, and the author types `protect:<value>` to protect an
entry.

### Example: `HttpRequest.Headers`

`Headers` is a `Map<String,String>`. One entry is one header field: the key is its name and the value its value,
sent in the map's order (the store's, not the author's).

```json
{"Authorization": "Bearer abc", "Accept": "application/json", "Content-Type": "text/csv"}
```

Each of these fails the node, naming the key and never the value:

- a key that is not an RFC 9110 token (for such a key, only the part before its first bad character is named);
- a value with a character that is not visible ASCII, a space or a tab;
- a field the block or the connection writes itself: `Content-Length`, `Transfer-Encoding`, `Host`, `Connection`,
  `Upgrade`, `Keep-Alive`, `Proxy-Connection`, `TE`, `Content-Encoding`, `Accept-Encoding`, `Expect`, `Trailer`;
- a body header (`Content-Type` included) with no `Body`;
- a `Content-Type` whose `charset` is not `utf-8`;
- two keys that name one field ignoring case (`Accept` and `accept`).

A field is sent once or refused, never merged. A list of field lines (`["Accept: */*"]`) is
`literal-port-value-invalid`, and a wire or `expr:` producing `Collection<String>` is a type mismatch
(`port-type-mismatch` on a wire, `expression-result-type-mismatch` on an `expr:`); nothing converts either. A node's
recorded inputs mask the value of a key matching the sensitive-name rule, so `Authorization` and `X-Api-Key` are
masked and `Accept` is kept.

## Json

`Json` is a producer's type. It may appear only as the whole type, `Json` or `Json?`, of an output. Nothing reads it
except a projection.

| Position | `Json`, `Json?` | Nested (`Collection<Json>`, `Gauge<Json>`) |
|---|---|---|
| Output: a block's declared or dynamic output, `_error`, a container's declared output, a workflow or sub-canvas output, an output of a resolver's answer, a namespace or helper check's answer | allowed | refused |
| Input: a block's declared or dynamic input, a sub-canvas input, a workflow input parameter, an input of a resolver's answer, a `typeArguments` value, a projection's `type` | refused | refused |
| A type-parameter binding | refused | refused |
| The stored type `T` of a collecting output such as `ForLoop`'s | refused | refused |

A declaration that breaks this is `block-port-declaration-invalid` when the block is registered. A stored type or a
resolver's answer is `port-type-json-position`. A wire or literal into an input is read as an input.

A `Json` value is read only through a projection (`jsonPath`, `jsonPathText` or a wire projection) that delivers
library scalars, or collections or maps of them. The whole `Json` value flows only into a `WorkflowOutput` port of
type `Json`.

**J.** A JSON `null` reads as `null`, and only on a nullable type. A `Json` value is never a JSON `null`: a site that
produces JSON turns a `null` or undefined element into a CLR `null`, so an expression reads such a value as `Json?`,
and an output port of type `Json` or `Json?` refuses a JSON `null` with `block-output-type-mismatch`.

`Expression<T>` is a block's whole declared input type. It takes only `expr:` text whose result must feed `T`, and no
wire feeds it. `T` must be a closed type without `Json`.

A node port resolver (the `SubWorkflow` resolver included) may answer closed types only; a refused answer leaves the
node's ports unresolved (`node-ports-unresolved`).

## Capabilities

A type has capabilities from a closed set: `Iterable<Item>`, `Numeric`, `Comparable`, `Equatable` and
`JsonSerializable`. A block uses them to constrain a type parameter, and rule A4 uses `JsonSerializable`.

| Type | Capabilities |
|---|---|
| `String`, `DateTime` | `Comparable`, `Equatable`, `JsonSerializable` |
| `Int`, `Number` | `Numeric`, `Comparable`, `Equatable`, `JsonSerializable` |
| `Boolean` | `Equatable`, `JsonSerializable` |
| `Json` | `JsonSerializable` |
| `Collection<T>` | `Iterable<Item=T>`; `Equatable` and `JsonSerializable` when `T` is |
| `Map<String,V>` | `Equatable` and `JsonSerializable` when `V` is; never `Iterable` |
| `T?` | `JsonSerializable` when `T` is, nothing else |
| host atom or constructor | exactly what its record claims |

A host can claim only `Comparable`, `Equatable` and `JsonSerializable`. Registration checks each claim and infers
none: `string` implements `IComparable<string>` and is still `Comparable` only when its record says so.

| Claim | Needs |
|---|---|
| `Comparable` | `IComparable<Clr>` |
| `Equatable` | `IEquatable<Clr>` or a record type |
| `JsonSerializable` | a `JsonTypeInfo` whose `Type` is the record's CLR type |

A claim the type does not bear out, or one only the library may make (`Iterable`, `Numeric`), is
`capability-claim-invalid`. `JsonSerializable` without a `JsonTypeInfo`, or any `JsonTypeInfo` that describes another
type than the record's CLR type, is `port-type-registration-invalid`. The serializer is not run at registration, so
one that does not round-trip fails when a value is written or read.

In the editor, a definition's `implements` lists its capabilities and `registry.implementsCapability(term, name)`
answers like the compiler. Both refuse a type nobody registered (`port-type-unregistered`) and a term holding a
type parameter.

## Type parameters

A block that is generic in some ports declares type parameters and writes them as `$T` in its port types. `Filter`
declares `$T` and types both `Collection` and `Filtered` as `Collection<$T>`. `IterateOver` declares `$T` and
`$R`, with `Collection: Collection<$T>`, `Results: Collection<$R>`, and a body whose `Item` is `$T` and whose
`Result` is `$R`. A parameter may carry constraints (`Numeric`, `Comparable`, `Equatable`, `JsonSerializable`)
that its binding must implement. A block cannot constrain a parameter by `Iterable`. No configuration value binds a
parameter by itself.

```csharp
public IReadOnlyList<WorkflowTypeParameter> TypeParameters => [new("T")];

public IReadOnlyCollection<BlockPortDefinition> Inputs => new[]
{
    new BlockPortDefinition("Collection", "Collection", PortValueType.OfDeclaration("Collection<$T>"), true),
};
```

### How a node binds its parameters

The compiler binds each node's parameters from what reaches its inputs:

- Exact evidence: a type argument on the node (`typeArguments`), and a source matched through a host constructor
  (`Gauge<$T>` fed `Gauge<Number>`) or an atom whose widening row reaches that constructor. Two exact bindings
  that differ are `type-parameter-conflict`.
- Lower bounds: a wire's source type, the result type of an `expr:`, and the type of a literal or configuration
  value. A parameter with no exact binding is bound to the join of its lower bounds (see
  [Least upper bound](#least-upper-bound)). No join is `type-parameter-conflict`.
- A parameter that a sub-canvas output declares (such as `$R`) is bound from what reaches the body's
  `WorkflowOutput` port.

The type of a literal is `Int` for an integral number, `Number` for any other, `String`, `Boolean`, the null type,
or `Collection<L>` for a non-empty list whose elements join to `L`. An object, an empty list and a list with no
join have no type: an object could be a map or a host type, so it is refused where nothing else decides. A list
into a `Collection<...>` input is matched element by element, and an object into a `Map<String,$T>` input member by
member, so `[1, "a"]` into `Collection<$T>` is `type-parameter-conflict`, and `[]` or `{}` binds nothing.

| Situation | Code |
|---|---|
| A binding breaks a declared constraint | `type-parameter-constraint` |
| A parameter nothing binds, and something reads one of its ports (a wire, `${previous}`, a `defaultOutputJson` key) or writes a literal that needs the binding (`[]`, `null`) | `type-parameter-unbound` |
| A site no binding could fit, such as an `Int` wire into `Collection<$T>` | `port-type-mismatch` or `literal-port-value-invalid` |

An unbound parameter's ports still exist. Every mapping into an input is a site, so two edges into one input are
both checked. An `IterateOver` whose body has no `Result` compiles.

### Passthrough names

Some nodes pass a name through without declaring it. Its type is that of its feed: the last mapping into the name,
else the configuration value the run merges. A wire from a node the canvas does not have, or naming an output its
source does not have, feeds nothing, so the configuration value is next.

- A mapped literal or configuration value that has no type (an object, `[]`) is `port-type-unresolved`, and the
  name stays untyped.
- A name that nothing feeds is not passed on. A wire reading it is `wire-port-unknown`, and an `expr:` reading it
  is `expression-out-of-scope`.

### Configuration values

A configuration value that the run merges into an input is checked against the input's type and counts as evidence
for its parameter when the input is declared, dynamic, answered by a trusted resolver with no configuration field
of the same name, or a passthrough name whose last mapping is a wire. Any other configuration value is checked
against its field's type. A boundary node (`WorkflowInput`, `WorkflowOutput`) has no such sites: its ports are its
canvas's. The editor checks no passthrough value; only the compiler does.

### Collecting outputs

A collecting declared output (`ForLoop`) of stored type `T` is a node output of type `Collection<T?>`, or
`Collection<T>` when `T` is already nullable, and the body's `WorkflowOutput` takes `T`. A `T` that holds `Json`
is `port-type-json-position`.

## Error codes

Codes a port type can produce, and where each appears.

| Code | Reported when |
|---|---|
| `port-type-malformed` | A stored name is malformed, blank or absent. |
| `port-type-map-key` | A stored `Map` key is not `String`. |
| `port-type-unregistered` | A well-formed name, constructor or constructor argument nobody registered. |
| `port-type-alias` | A stored alias or case variant of a registered name, or a stored `Any` or `Float`. |
| `port-type-json-position` | `Json` where it is not allowed in a stored term or a resolver's answer. |
| `port-type-mismatch` | A wire's source type does not feed the target port. |
| `port-type-unresolved` | A passthrough name has a mapped literal or configuration value with no type. |
| `port-type-registration-invalid` | A registration record breaks a rule of its own (names, reserved words, references, literal form, `JsonTypeInfo`, `JsonSerializable` claim). |
| `port-type-name-clash` | A registered name, alias or constructor is already held. |
| `capability-claim-invalid` | A claim the type does not bear out, or one only the library makes. |
| `block-port-declaration-invalid` | A block's declaration breaks a rule above; the library's own blocks included. |
| `literal-port-value-invalid` | A literal does not fit the port. |
| `expression-result-type-mismatch` | An `expr:` result does not feed the port. |
| `expression-type-mismatch` | An expression joins types that have no join. |
| `block-output-type-mismatch` | A block returns a value its output type does not accept, such as JSON `null` for `Json`. |
| `node-ports-unresolved` | A node port resolver's answer was refused. |
| `type-parameter-conflict` | Two exact bindings differ, or the lower bounds have no join. |
| `type-parameter-constraint` | A binding breaks a declared constraint. |
| `type-parameter-unbound` | Something needs a parameter that nothing binds. |
| `definition-unreadable`, `signature-unreadable` | A stored document or parameter list cannot be read. |
