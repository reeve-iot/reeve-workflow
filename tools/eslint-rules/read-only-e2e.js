import path from 'node:path'

const E2E_ROOT = 'examples/playground/e2e/'

export const CLIENT_MODULE = 'support/api-client'

export const CLIENT_OPERATIONS = Object.freeze([
  'getBlockTypes', 'getWorkflows', 'getWorkflow', 'getExecutions', 'getExecution', 'getExecutionReplay',
  'getVersions', 'diffVersions', 'getVersion', 'executeWorkflow', 'validateImport',
])

const FETCH_KEYS = new Set(['method', 'signal'])

const CLIENT_CALLS = new Set(['readOnlyApi'])

const MODULES = new Map([
  ['@playwright/test', new Set(['test', 'expect', 'defineConfig', 'devices'])],
  ['@reeve-iot/workflow-core', null],
  ['node:fs', null],
  ['node:fs/promises', null],
  ['node:path', null],
  ['node:url', null],
  ['node:timers/promises', null],
])

export const GRANTS = new Map([
  [CLIENT_MODULE, {
    reason: 'The one client: every request it makes is judged call by call below, and nothing else may import it whole.',
    modules: new Map([['@reeve-iot/workflow-client', new Set(['createWorkflowClient'])], ['../src/host', null]]),
    process: [],
  }],
  ['support/harness', {
    reason: 'Starts PostgreSQL, Redis and the published host, and stops them. It holds no client, so it creates no workflow.',
    modules: new Map([
      ['node:child_process', null], ['node:net', null], ['@testcontainers/postgresql', null],
      ['@testcontainers/redis', null], ['../src/host', null],
    ]),
    process: ['kill'],
  }],
  ['support/rules', {
    reason: 'Reads the host page\'s translations to know what a publish row says. That module holds data and imports a type.',
    modules: new Map([['../src/validationMessages', null]]),
    process: [],
  }],
  ['global-setup', {
    reason: 'Playwright runs it once, before the tests, to start the harness.',
    modules: new Map([['support/harness', null]]),
    process: [],
  }],
  ['global-teardown', {
    reason: 'Playwright runs it once, after the tests, to stop the harness.',
    modules: new Map([['support/harness', null]]),
    process: [],
  }],
])

const PRIVILEGED = new Set(['support/harness'])

const NETWORK_GLOBALS = new Set(['fetch', 'XMLHttpRequest', 'WebSocket', 'EventSource', 'navigator'])
const CODE_GLOBALS = new Set(['eval', 'Function', 'Reflect', 'require', 'module'])
const GLOBAL_OBJECTS = new Set(['globalThis', 'window', 'self', 'global'])

const REFUSED_MEMBERS = new Set([
  'request', 'route', 'routeFromHAR', 'routeWebSocket', 'addInitScript', 'addScriptTag', 'setContent',
  'exposeFunction', 'exposeBinding', 'evaluate', 'evaluateHandle', 'evaluateAll', '$eval', '$$eval',
  'waitForFunction', 'newCDPSession', 'newBrowserCDPSession', 'constructor',
])

const REFUSED_TYPES = new Set(['APIRequestContext', 'APIRequest'])

const EXTENSION = /\.(?:[cm]?[jt]sx?)$/

function e2ePath(filename) {
  const normalized = filename.replaceAll('\\', '/')
  const at = normalized.lastIndexOf(E2E_ROOT)
  const relative = at >= 0 ? normalized.slice(at + E2E_ROOT.length) : path.posix.basename(normalized)
  return relative.replace(EXTENSION, '')
}

function keyName(key, computed) {
  if (!computed) return key.type === 'Identifier' || key.type === 'PrivateIdentifier' ? key.name : String(key.value)
  if (key.type === 'Literal' && (typeof key.value === 'string' || typeof key.value === 'number')) return String(key.value)
  if (key.type === 'TemplateLiteral' && key.expressions.length === 0) return key.quasis[0]?.value.cooked ?? undefined
  return undefined
}

export default {
  rules: {
    'read-only-api': {
      meta: {
        type: 'problem',
        docs: {
          description: 'The editor E2E reaches the server only through its client module, and that module changes no workflow.',
        },
        schema: [],
        messages: {
          module: "'{{name}}' is not a module this file may import: the suite reaches the server only through support/api-client.ts.",
          name: "'{{name}}' is not something this file may take from '{{module}}'.",
          whole: "'{{module}}' is imported whole, which takes names this file may not have from it.",
          client: '@reeve-iot/workflow-client is imported only by support/api-client.ts; elsewhere a test takes its types, never its values.',
          dynamic: 'An import whose module is computed at run time cannot be judged: import it by a literal name.',
          network: '{{name}} reaches the network: only support/api-client.ts makes a request, and its own fetch is only a GET.',
          code: '{{name}} runs a string as code or reads a member by a name built at run time, so the rule cannot see what it reaches.',
          global: '{{name}} hands out every global, fetch among them, under whatever name the caller picks.',
          process: 'process is used here only as {{allowed}}: the rest of it can load a module or reach a socket.',
          member: "'{{name}}' hands the test Playwright's HTTP client, the page's routing, or a script run in the page: the suite clicks and reads through its client.",
          computed: 'A member named at run time cannot be judged: name it with a literal.',
          type: "{{name}} is Playwright's own HTTP client: the suite reads through support/api-client.ts.",
          'client-call': 'The client is bound once and used only as client.<operation>(…), for an operation CLIENT_OPERATIONS lists.',
          'client-bare': 'readOnlyApi is called with no argument: it reads the harness state itself, so no test can steer the origin the operations it allows reach.',
          'client-fetch': "fetch is called here only as fetch(url, { method: 'GET', signal }), so the request is a GET and nothing more.",
        },
      },
      create(context) {
        const sourceCode = context.sourceCode
        const file = e2ePath(context.physicalFilename ?? context.filename)
        const grant = GRANTS.get(file)
        const isClientModule = file === CLIENT_MODULE
        const clientFactories = []
        const clientCallers = []
        const reported = new Set()

        function report(node, messageId, data = {}) {
          const key = `${String(node.range[0])}:${String(node.range[1])}:${messageId}`
          if (reported.has(key)) return
          reported.add(key)
          context.report({ node, messageId, data })
        }

        function allowance(spec) {
          if (spec.startsWith('.')) {
            const target = path.posix.normalize(path.posix.join(path.posix.dirname(file), spec)).replace(EXTENSION, '')
            if (target.startsWith('../') || PRIVILEGED.has(target)) return grant?.modules.get(target)
            return null
          }
          return MODULES.has(spec) ? MODULES.get(spec) : grant?.modules.get(spec)
        }

        function isClientModuleSpec(spec) {
          if (typeof spec !== 'string' || !spec.startsWith('.')) return false
          const target = path.posix.normalize(path.posix.join(path.posix.dirname(file), spec)).replace(EXTENSION, '')
          return target === CLIENT_MODULE
        }

        function judgeModule(node, spec, names, whole) {
          if (typeof spec !== 'string') {
            report(node, 'dynamic')
            return
          }
          const allowed = allowance(spec)
          if (allowed === undefined) {
            report(node, spec === '@reeve-iot/workflow-client' ? 'client' : 'module', { name: spec })
            return
          }
          if (allowed === null) return
          if (whole) report(node, 'whole', { module: spec })
          for (const [nameNode, name] of names) {
            if (!allowed.has(name)) report(nameNode, 'name', { name, module: spec })
          }
        }

        const specifierName = (node) => (node.type === 'Identifier' ? node.name : node.value)

        function judgeMember(node) {
          const name = keyName(node.property, node.computed)
          if (name === undefined) {
            report(node.property, 'computed')
            return
          }
          if (!REFUSED_MEMBERS.has(name) && !name.startsWith('_')) return
          if (name === 'request' && node.parent.type === 'CallExpression' && node.parent.callee === node) return
          report(node.property, 'member', { name })
        }

        function judgePatternKey(node) {
          const name = keyName(node.key, node.computed)
          if (name === undefined) report(node.key, 'computed')
          else if (REFUSED_MEMBERS.has(name) || name.startsWith('_')) report(node.key, 'member', { name })
        }

        const isCallee = (node) => node.parent.type === 'CallExpression' && node.parent.callee === node
        const isMemberObject = (node) => node.parent.type === 'MemberExpression' && node.parent.object === node

        function isClientFetch(identifier) {
          if (!isClientModule || !isCallee(identifier)) return false
          const [input, init, ...rest] = identifier.parent.arguments
          if (input === undefined || input.type === 'SpreadElement' || init?.type !== 'ObjectExpression' || rest.length > 0) return false
          let get = false
          for (const property of init.properties) {
            if (property.type !== 'Property' || property.computed || property.kind !== 'init' || property.method) return false
            if (property.key.type !== 'Identifier' || !FETCH_KEYS.has(property.key.name)) return false
            if (property.key.name === 'method') get = property.value.type === 'Literal' && property.value.value === 'GET'
          }
          return get
        }

        function judgeGlobal(reference) {
          const identifier = reference.identifier
          const name = identifier.name
          if (NETWORK_GLOBALS.has(name)) {
            if (name === 'fetch' && isClientFetch(identifier)) return
            report(identifier, isClientModule && name === 'fetch' ? 'client-fetch' : 'network', { name })
          } else if (CODE_GLOBALS.has(name)) {
            report(identifier, 'code', { name })
          } else if (GLOBAL_OBJECTS.has(name)) {
            report(identifier, 'global', { name })
          } else if (name === 'process') {
            const allowed = ['env', ...(grant?.process ?? [])]
            const member = identifier.parent
            const used = isMemberObject(identifier) && !member.computed ? member.property.name : undefined
            if (!allowed.includes(used)) report(identifier, 'process', { allowed: allowed.map((n) => `process.${n}`).join(', ') })
          }
        }

        function judgeClientFactory(specifier) {
          for (const variable of sourceCode.getDeclaredVariables(specifier)) {
            for (const reference of variable.references) {
              const call = reference.identifier.parent
              const declarator = call.parent
              const bound = isCallee(reference.identifier) && declarator.type === 'VariableDeclarator'
                && declarator.init === call && declarator.id.type === 'Identifier' && declarator.parent.kind === 'const'
              if (!bound) {
                report(reference.identifier, 'client-call')
                continue
              }
              for (const client of sourceCode.getDeclaredVariables(declarator)) {
                for (const use of client.references) {
                  if (use.init) continue
                  const member = use.identifier.parent
                  const called = isMemberObject(use.identifier) && !member.computed && member.property.type === 'Identifier'
                    && CLIENT_OPERATIONS.includes(member.property.name) && isCallee(member)
                  if (!called) report(use.identifier, 'client-call')
                }
              }
            }
          }
        }

        function judgeClientCaller(specifier) {
          for (const variable of sourceCode.getDeclaredVariables(specifier)) {
            for (const reference of variable.references) {
              const bare = isCallee(reference.identifier)
                && reference.identifier.parent.type === 'CallExpression'
                && reference.identifier.parent.arguments.length === 0
              if (!bare) report(reference.identifier, 'client-bare')
            }
          }
        }

        function judgeImport(node) {
          if (node.importKind === 'type') return
          const values = node.specifiers.filter((specifier) => specifier.importKind !== 'type')
          if (node.specifiers.length > 0 && values.length === 0) return
          const whole = values.some((specifier) => specifier.type !== 'ImportSpecifier')
          const names = values.filter((specifier) => specifier.type === 'ImportSpecifier')
            .map((specifier) => [specifier, specifierName(specifier.imported)])
          judgeModule(node.source, node.source.value, names, whole)
          if (isClientModule && node.source.value === '@reeve-iot/workflow-client') {
            for (const [specifier, name] of names) if (name === 'createWorkflowClient') clientFactories.push(specifier)
          }
          if (isClientModuleSpec(node.source.value)) {
            for (const [specifier, name] of names) if (CLIENT_CALLS.has(name)) clientCallers.push(specifier)
          }
        }

        return {
          ImportDeclaration: judgeImport,
          ExportNamedDeclaration(node) {
            if (node.source === null || node.source === undefined || node.exportKind === 'type') return
            const values = node.specifiers.filter((specifier) => specifier.exportKind !== 'type')
            judgeModule(node.source, node.source.value, values.map((specifier) => [specifier, specifierName(specifier.local)]), false)
          },
          ExportAllDeclaration(node) {
            if (node.exportKind !== 'type') judgeModule(node.source, node.source.value, [], true)
          },
          ImportExpression(node) {
            const spec = node.source.type === 'Literal' ? node.source.value : keyName(node.source, true)
            judgeModule(node.source, spec, [], true)
          },
          TSImportEqualsDeclaration(node) {
            if (node.importKind === 'type') return
            const reference = node.moduleReference
            if (reference.type === 'TSExternalModuleReference') judgeModule(reference, reference.expression.value, [], true)
          },
          MemberExpression: judgeMember,
          'ObjectPattern > Property': judgePatternKey,
          Identifier(node) {
            if (REFUSED_TYPES.has(node.name)) report(node, 'type', { name: node.name })
          },
          'Program:exit'() {
            const globalScope = sourceCode.scopeManager.globalScope
            const implicit = globalScope.variables.filter((variable) => variable.defs.length === 0)
            for (const reference of [...globalScope.through, ...implicit.flatMap((variable) => variable.references)]) {
              if (reference.isValueReference === false) continue
              judgeGlobal(reference)
            }
            for (const specifier of clientFactories) judgeClientFactory(specifier)
            for (const specifier of clientCallers) judgeClientCaller(specifier)
          },
        }
      },
    },
  },
}
