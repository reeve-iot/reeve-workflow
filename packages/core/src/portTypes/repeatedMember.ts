/** A member name found twice in one object, and the path of that object; `name` is null for a member name that does
 * not decode (an unpaired surrogate), which no text can spell. */
export interface RepeatedMember {
  name: string | null
  path: string
}

interface Frame {
  array: boolean
  path: string
  names: Set<string>
  index: number
  key: string
  awaitingKey: boolean
}

const PLAIN_NAME = /^[A-Za-z0-9_-]+$/
const UNPAIRED_SURROGATE = /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/

const childPath = (path: string, name: string): string =>
  PLAIN_NAME.test(name) ? `${path}.${name}` : `${path}['${name.replaceAll('\\', '\\\\').replaceAll("'", "\\'")}']`

/** The same sentence the backend gives for a repeated member name. */
export const describeRepeatedMember = (repeat: RepeatedMember): string =>
  repeat.name === null
    ? 'the text holds an unpaired surrogate'
    : `the member name '${repeat.name}' occurs more than once in the object at ${repeat.path}`

/** The first member name, in document order at any depth of the JSON `text`, that its object already holds, or one that
 * does not decode. `JSON.parse` keeps the last of two silently, so the text is scanned; text that ends early ends the scan. */
export function findRepeatedMember(text: string): RepeatedMember | undefined {
  const stack: Frame[] = []
  const valuePath = (): string => {
    const top = stack[stack.length - 1]
    if (top === undefined) return '$'
    return top.array ? `${top.path}[${top.index}]` : childPath(top.path, top.key)
  }
  const afterValue = (): void => {
    const top = stack[stack.length - 1]
    if (top?.array) top.index++
  }
  let at = 0
  while (at < text.length) {
    const char = text[at]!
    if (char === '{' || char === '[') {
      stack.push({ array: char === '[', path: valuePath(), names: new Set(), index: 0, key: '', awaitingKey: char === '{' })
      at++
    } else if (char === '}' || char === ']') {
      stack.pop()
      afterValue()
      at++
    } else if (char === ',') {
      const top = stack[stack.length - 1]
      if (top !== undefined && !top.array) top.awaitingKey = true
      at++
    } else if (char === '"') {
      let end = at + 1
      while (end < text.length && text[end] !== '"') end += text[end] === '\\' ? 2 : 1
      if (end >= text.length) return undefined
      const top = stack[stack.length - 1]
      if (top?.awaitingKey) {
        const name = JSON.parse(text.slice(at, end + 1)) as string
        if (UNPAIRED_SURROGATE.test(name)) return { name: null, path: top.path }
        if (top.names.has(name)) return { name, path: top.path }
        top.names.add(name)
        top.key = name
        top.awaitingKey = false
      } else {
        afterValue()
      }
      at = end + 1
    } else if (' \t\r\n:'.includes(char)) {
      at++
    } else {
      while (at < text.length && !' \t\r\n,]}'.includes(text[at]!)) at++
      afterValue()
    }
  }
  return undefined
}
