# Validation findings

The editor returns structured findings from `validateStructure()`, `validateWorkflow()` and
`validateWorkflowWarnings()` (aliases of the `*Findings` methods). A finding has a stable `code`, named `params`,
a node or canvas location, and an English `message` the library wrote at the site. Compiler diagnostics from a
publish carry the compiler's rule id and its message, and no params.

## How nodes are named

A compiler message names a node by its block's display name (`Workflow Input` and `Workflow Output` for the
boundary nodes), or by the block type id for a block the catalogue does not hold or that declares no display
name. When another node anywhere in the document, on any canvas, has the same name, the node id follows in
parentheses: `'Compare (n2)'`. When a node of that name on another canvas has that id too, a node off the root
canvas adds its canvas: `'Compare (n2 in box→Body)'`. A boundary node never takes a suffix. The editor's own
findings name a node the same way (`Node 'Compare (n2)' …`, and the `nodeLabel` param).

The diagnostic's `nodeId` always carries the id, so an editor focuses the node from it, not from the text. In the
compiler's messages, three kinds name the id instead of the label: messages about the id itself (a duplicate or
over-long id), graph-structure messages (connection endpoints and repeated connections, extra roots, unreachable
nodes, inputs fed twice), and the port type name and error policy messages. The editor's own findings always use
the label.

## Stored-document refusals

- A stored node policy that holds `routeToErrorPort` under any value, `null` included, is refused with the
  compiler rule `error-policy-route-unread`, because the runner does not read that member. The finding names the
  field `errorPolicy`. A document holding the `false` default or `true` is refused the same way.
- Stored connections need unique non-empty ids and endpoints, and use object mappings of type `direct` or
  `literal`. Duplicate ids report `duplicate-edge-id`; duplicate source/target pairs report `duplicate-edge-pair`
  at compile and in editor structure validation.
- The `outputMappings` key is refused whole with `output-mappings-unread`; an output is a wire into the
  `WorkflowOutput` node.
- A stored document the reader refuses is one finding. Six reader rules have a code of their own, in a definition
  or a parameter list alike: `duplicate-node-id`, `duplicate-edge-id`, `stored-key-unknown`, `dotted-name`,
  `error-policy-route-unread` and `output-mappings-unread`. Any other reader rule (`L1` to `L5` and `L7`) is reported as
  `definition-unreadable` or `signature-unreadable`, with the rule in `fault`. The backend and the editor answer the
  same way.

### Stored-document rules

A stored definition, and each of its two parameter lists, is read by one reader that returns the first rule it
breaks and never throws. The editor reads documents by the same rules.

| Rule | A document breaks it when |
|---|---|
| L1 | It is not one JSON value, or there is no document (blank text). |
| L2 | It nests more than 64 arrays and objects, the outermost counting as one. |
| L3 | One object has a second property of the same name. |
| L4 | A string, a property name or the model holds an unpaired surrogate. |
| L5 | A listed key is spelled in another case than the grammar lists; keys match exactly. |
| L7 | A value has the wrong kind, a required field is missing, or a number is not its double's shortest round-trip form. |

A fault carries its rule, a JSON pointer (RFC 6901) and the enclosing node and canvas. Blank means absent, empty or
only JSON white space (space, tab, line feed, carriage return). An absent parameter list means no parameters; a list
that is blank or the literal `null` is refused (L7).

A number is accepted only if the double nearest to its text is finite and the decimal value the text spells equals
the value of that double's shortest round-trip form (.NET `"R"`, JavaScript `String(x)`), because a save writes
numbers in that form and any other value would change silently on the next save. `1.0` and `1e0` pass.
`0.1000000000000000055511` does not (its shortest form is `0.1`), and neither does `4611686018427387904`. Any zero
passes; NaN and infinities are refused.

## What a finding shows

Each library code has one catalogue entry: the parameters its English template reads (`requiredParams`) and the
template. `validationMessage(finding, catalogue?)`, which `ValidationFindings`, the editor summary and the
playground all call, picks the text like this:

1. A library code whose finding carries every required parameter shows the host's catalogue entry for that code, or
   the library's template when the host has none.
2. A library code whose finding lacks a required parameter shows the finding's own `message`.
3. A code the library has no entry for (a host rule, say) shows the host's catalogue entry, or the finding's own
   `message` when the host has none.

A publish diagnostic carries no params, so it shows the server's message unless its code's template reads none
(`no-start-node` and `input-has-incoming` read none, so they show the template). Put every value a translation needs
in `params`, and do not copy the message into it. `params` may carry more than the template reads (the "Other
parameters" column).

## Library codes

A code is usually a compiler rule id, or `<rule id>.<variant>` where one rule has several message shapes that the
editor tells apart (`wire-port-unknown.input` and `.output` are both the compiler's `wire-port-unknown`). A row
whose English message is "the diagnostic's own message" is a compiler rule id with no editor template.

Some codes exist only in the editor: `definition-undrawable`, `validation-rule-failed` and the `branch-output-*`
warnings. The write-refusal codes (`host-metadata-invalid`,
`secret-value-unmatched`, `secret-codec-missing`, `secret-refused`, `secret-key-invalid`, `origin-not-accepted`)
come from a save the Editor API refuses with a 400, not from the compiler.

| Code | Required parameters | Other parameters | English message |
|---|---|---|---|
| `cycle` | `location` | none | `Workflow contains a cycle{location}` |
| `block-port-declaration-invalid` | `blockTypeId`, `where`, `reason` | none | `Block '{blockTypeId}' {where} {reason}.` |
| `no-start-node` | none | none | `Workflow has no start node. Add a Workflow Input node as the single entry point.` |
| `input-has-incoming` | none | none | `The Workflow Input node must be the single start point and cannot have incoming connections` |
| `extra-root` | `nodeLabel` | none | `Node '{nodeLabel}' has no incoming connection — the workflow must start only from the Workflow Input node` |
| `unreachable` | `nodeLabel` | none | `Node '{nodeLabel}' is not reachable from the Workflow Input start node` |
| `duplicate-input-mapping` | `nodeLabel`, `inputName`, `location` | none | `Node '{nodeLabel}' input '{inputName}' is mapped from multiple incoming connections{location} — only one will apply` |
| `duplicate-edge-pair` | `location`, `sourceNodeId`, `targetNodeId` | none | `More than one connection joins '{sourceNodeId}' to '{targetNodeId}'{location}` |
| `duplicate-edge-id` | `fault` | none | `Connection id is duplicated: {fault}.` |
| `edge-endpoint-unknown` | `edgeId`, `location`, `end`, `nodeId` | none | `Connection '{edgeId}'{location} names {end} node '{nodeId}', which does not exist on its canvas` |
| `duplicate-node-id` | `duplicateId`, `location` | none | `Node id '{duplicateId}' is used by more than one node{location}` |
| `dotted-name` | none | none | the diagnostic's own message |
| `literal-port-value-invalid` | `nodeLabel`, `portName`, `expectedType` | none | `Node '{nodeLabel}' has a value for '{portName}' that cannot be read as {expectedType}.` |
| `literal-port-value-invalid.enum` | `nodeLabel`, `portName`, `allowedValues` | none | `Node '{nodeLabel}' must set '{portName}' to one of {allowedValues}.` |
| `required-input-unfed` | `nodeLabel`, `portLabel` | `portName` | `Node '{nodeLabel}' is missing required mapping for '{portLabel}'` |
| `required-input-unfed.exit` | `exitNoun`, `portLabel` | `portName` | `{exitNoun} '{portLabel}' is not connected` |
| `reference-input-mapped` | `nodeLabel`, `reference` | none | `Node '{nodeLabel}' takes the workflow it runs from its '{reference}' setting only; a mapping into it is refused` |
| `node-ports-unresolved` | `nodeLabel`, `reference` | none | `Node '{nodeLabel}' names no workflow to run in '{reference}'` |
| `sub-canvas-name-unknown` | `nodeLabel`, `branchName` | none | `Node '{nodeLabel}' has a branch '{branchName}' its block does not declare` |
| `required-config-missing` | `nodeLabel`, `fieldLabel` | `fieldName` | `Node '{nodeLabel}' is missing required configuration field '{fieldLabel}'` |
| `resilience-config-invalid` | `nodeLabel`, `key`, `value`, `expectation` | none | `Node '{nodeLabel}' has '{key}' set to {value}, which the engine cannot read as {expectation}` |
| `resilience-config-invalid.timeout-too-long` | `nodeLabel`, `key`, `value`, `maxSeconds` | none | `Node '{nodeLabel}' sets '{key}' to {value}, and the longest deadline the engine applies is {maxSeconds} seconds (one day)` |
| `declared-output-shadows-static` | `nodeLabel`, `portName` | none | `Node '{nodeLabel}' declared output '{portName}' shadows a static output — rename it to avoid collision` |
| `error-policy-in-configuration` | `nodeLabel` | none | `Node '{nodeLabel}' keeps its error policy in its configuration, where the run never reads it: set the policy on the node instead` |
| `error-policy-in-configuration.boundary` | `nodeLabel` | none | `Node '{nodeLabel}' keeps an error policy in its configuration, where the run never reads it, and an entry or exit node has none: remove the key` |
| `error-policy-route-unread` | `nodeLabel` | none | `Node '{nodeLabel}' sets 'routeToErrorPort', which no run reads: remove it. A node offers '_error' under EmitError alone` |
| `wire-port-unknown.output` | `nodeLabel`, `portName`, `edgeId` | none | `Node '{nodeLabel}' has no output '{portName}' for connection '{edgeId}' to map` |
| `wire-port-unknown.input` | `nodeLabel`, `portName`, `edgeId` | none | `Node '{nodeLabel}' has no input '{portName}' for connection '{edgeId}' to feed` |
| `port-type-malformed` | `portName`, `detail`, `origin` | `rawType` | `Port type of '{portName}' is not a well-formed name: {detail} It is {origin}.` |
| `port-type-map-key` | `portName`, `detail`, `origin` | `rawType` | `Port type of '{portName}' has a Map type whose key is not String: {detail} It is {origin}.` |
| `port-type-json-position` | `portName` | none | `Port type '{portName}' contains Json, which is not allowed here.` |
| `type-argument-unknown` | `name`, `blockType` | none | `Type argument '{name}' names no type parameter of block '{blockType}'.` |
| `signature-unreadable` | `subject`, `fault` | none | `{subject} cannot be read: {fault}.` |
| `definition-unreadable` | `subject`, `fault` | none | `{subject} cannot be read: {fault}.` |
| `stored-key-unknown` | `subject`, `fault` | none | `{subject} cannot be read: {fault}.` |
| `output-mappings-unread` | `subject`, `fault` | none | `{subject} cannot be read: {fault}.` |
| `definition-undrawable` | `fault` | none | `The editor cannot draw this workflow: {fault}.` |
| `branch-output-unset` | `branchName`, `nodeLabel`, `portName` | none | `Branch '{branchName}' of '{nodeLabel}' does not set output '{portName}' (returns null)` |
| `branch-output-maybe-unset` | `nodeLabel`, `portName`, `satisfyingBranches` | none | `Output '{portName}' of '{nodeLabel}' may be null on some execution path (set it {satisfyingBranches})` |
| `validation-rule-failed` | `nodeLabel`, `reason` | none | `A validation rule failed on '{nodeLabel}': {reason}` |
| `host-metadata-invalid` | `detail` | none | `The workflow's host metadata cannot be stored. {detail}` |
| `secret-value-unmatched` | `detail` | none | `A hidden or protected value cannot be kept. {detail}` |
| `secret-codec-missing` | `detail` | none | `A value cannot be hidden because this host stores no protected values. {detail}` |
| `secret-refused` | `detail` | none | `The host did not accept a secret in this workflow. {detail}` |
| `secret-key-invalid` | `detail` | none | `A header name is not valid. {detail}` |
| `origin-not-accepted` | `detail` | none | `This request cannot declare that origin. {detail}` |
| `structure` | none | none | the diagnostic's own message |
| `block-type-unknown` | none | none | the diagnostic's own message |
| `wire-port-unknown` | none | none | the diagnostic's own message |
| `port-type-mismatch` | none | none | the diagnostic's own message |
| `secret-input-passthrough` | none | none | the diagnostic's own message |
| `secret-input-key-invalid` | none | none | the diagnostic's own message |
| `compilation-fault` | none | none | the diagnostic's own message |
| `port-type-unresolved` | none | none | the diagnostic's own message |
| `port-type-alias` | none | none | the diagnostic's own message |
| `port-type-unregistered` | none | none | the diagnostic's own message |
| `port-list-without-dynamic-ports` | none | none | the diagnostic's own message |
| `declared-outputs-unsupported` | none | none | the diagnostic's own message |
| `declared-output-source-unknown` | none | none | the diagnostic's own message |
| `json-projection-invalid` | none | none | the diagnostic's own message |
| `error-policy-on-boundary-node` | none | none | the diagnostic's own message |
| `type-parameter-conflict` | none | none | the diagnostic's own message |
| `type-parameter-unbound` | none | none | the diagnostic's own message |
| `type-parameter-constraint` | none | none | the diagnostic's own message |
| `expression-syntax` | none | none | the diagnostic's own message |
| `expression-out-of-scope` | none | none | the diagnostic's own message |
| `expression-unknown-namespace` | none | none | the diagnostic's own message |
| `expression-field-access-invalid` | none | none | the diagnostic's own message |
| `expression-type-mismatch` | none | none | the diagnostic's own message |
| `expression-result-type-mismatch` | none | none | the diagnostic's own message |
| `node-id-too-long` | none | none | the diagnostic's own message |
| `block-type-id-too-long` | none | none | the diagnostic's own message |

`location` is the English suffix the editor appends (` in branch 'x' of 'y'`); `exitNoun` and `subject` are
English noun phrases. A translation that must reorder them reads the finding's `canvasPath` and `nodeId` instead.

A node's `_error.code` is not a finding, but a host may show it with the same catalogue. `secret-unresolved` has
a row of its own: `input` is the secret-capable input, and `entry` names the key (`key 'X-Token'`, or `row 2` for
a key that is no field name).

| Code | Required parameters | Other parameters | English message |
|---|---|---|---|
| `secret-unresolved` | `input`, `entry` | none | `Input '{input}' {entry} holds a secret that could not be revealed.` |

## Localizing messages

Provide a catalogue when creating an editor provider. Each function receives the finding parameters and the finding.

```tsx
import { WorkflowEditorStoreProvider } from '@reeve-iot/workflow-core'
import type { ValidationMessageCatalogue } from '@reeve-iot/workflow-core'

const messages: ValidationMessageCatalogue = {
  'required-input-unfed': (params) => `Connect the required input ${String(params.portLabel)}.`,
}

<WorkflowEditorStoreProvider options={{ portTypes, validationMessages: messages }}>
  <Editor />
</WorkflowEditorStoreProvider>
```

Host rules return their own `code`, `params`, and English `message`. Put every value a translated sentence
needs in `params`.

## Rendering findings

`@reeve-iot/workflow-mui` exports `ValidationFindings`. It renders editor findings (and warnings, in the warning colour)
and publish diagnostics, one row per finding with `data-code`, `data-source` (`editor` or `publish`) and
`data-severity`. A row that names a node is a button that opens that node's canvas, selects the node, then calls
`onSelectNode`; a graph-wide row is plain text. The provider catalogue supplies host-wide translations; the
component's `catalogue` prop overrides it for one instance.
