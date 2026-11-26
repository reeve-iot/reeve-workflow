import type { Page, Request, Response } from '@playwright/test'
import { expect } from './fixtures'

export const DROP_POINT = { x: 440, y: 160 }
export const NODE_HEADER_POINT = { x: 24, y: 12 }

export async function dropPaletteBlock(
  page: Page, blockTypeId: string, category: string, targetPosition = DROP_POINT,
): Promise<void> {
  const item = page.getByTestId(`workflow-block-${blockTypeId}`)
  if (await item.count() === 0) await page.getByTestId(`palette-category-${category}`).click()
  await expect(item).toHaveCount(1)
  await expect(page.locator('.MuiCollapse-entered').filter({ has: item })).toHaveCount(1)
  const header = page.getByTestId(`palette-category-${category}`)
  const headerBounds = await header.boundingBox()
  const itemBounds = await item.boundingBox()
  if (headerBounds === null || itemBounds === null || itemBounds.y < headerBounds.y + headerBounds.height) {
    throw new Error(`Palette item '${blockTypeId}' overlaps its '${category}' header.`)
  }
  await item.scrollIntoViewIfNeeded()
  const nodes = page.getByTestId(`workflow-node-${blockTypeId}`)
  const before = await nodes.count()
  await item.dragTo(page.getByTestId('workflow-canvas'), { targetPosition })
  await expect(nodes).toHaveCount(before + 1)
}

type RequestPath = string | RegExp

const ROW = /^\/api\/workflows\/[^/]+$/
const PUBLISH = /^\/api\/workflows\/[^/]+\/publish$/
const EXECUTE = /^\/api\/workflows\/[0-9a-f-]{36}\/execute$/
const IMPORT_VALIDATE = /\/api\/workflows\/import\/validate$/
const BLOCK_CATALOGUE = /\/api\/workflow-blocks$/

const pathnameOf = (request: Request | Response): string => new URL(request.url()).pathname
const matches = (path: RequestPath, pathname: string): boolean => (typeof path === 'string' ? pathname === path : path.test(pathname))

export interface RequestLog {
  answered: (method: string, path: RequestPath, times?: number) => Promise<Response[]>
  answeredOnce: (method: string, path: RequestPath) => Promise<Response>
  started: (method: string, path: RequestPath) => number
  stop: () => void
}

export function logRequests(page: Page, mayBeRefused?: { method: string; path: RequestPath }): RequestLog {
  const began: Request[] = []
  const finished: Request[] = []
  const statuses = new Map<Request, number>()
  const onRequest = (request: Request): void => { began.push(request) }
  const onResponse = (response: Response): void => {
    if (began.includes(response.request())) statuses.set(response.request(), response.status())
  }
  const onFinished = (request: Request): void => { if (began.includes(request)) finished.push(request) }
  page.on('request', onRequest)
  page.on('response', onResponse)
  page.on('requestfinished', onFinished)
  const hits = (list: readonly Request[], method: string, path: RequestPath): Request[] =>
    list.filter((request) => request.method() === method && matches(path, pathnameOf(request)))
  const failOnRefusedWrite = async (): Promise<void> => {
    for (const [request, status] of statuses) {
      const allowed = mayBeRefused !== undefined && hits([request], mayBeRefused.method, mayBeRefused.path).length > 0
      if (status < 400 || request.method() === 'GET' || allowed) continue
      const body = await (await request.response())?.text().catch(() => '')
      throw new Error(`${request.method()} ${pathnameOf(request)} answered ${String(status)}: ${body ?? ''}`)
    }
  }
  const answered = async (method: string, path: RequestPath, times = 1): Promise<Response[]> => {
    await expect.poll(async () => {
      await failOnRefusedWrite()
      return hits(finished, method, path).length
    }, { message: `${method} ${String(path)} x${String(times)}`, timeout: 0 }).toBeGreaterThanOrEqual(times)
    return Promise.all(hits(finished, method, path).slice(0, times).map(async (request) => {
      const response = await request.response()
      if (response === null) throw new Error(`${method} ${pathnameOf(request)} finished without a response.`)
      return response
    }))
  }
  return {
    answered,
    async answeredOnce(method, path) {
      const [only] = await answered(method, path)
      if (only === undefined) throw new Error(`${method} ${String(path)} was counted and then gone.`)
      return only
    },
    started: (method, path) => hits(began, method, path).length,
    stop: () => {
      page.off('request', onRequest)
      page.off('response', onResponse)
      page.off('requestfinished', onFinished)
    },
  }
}

async function accepted(responses: readonly Response[], what: string): Promise<void> {
  for (const response of responses) {
    if (response.ok()) continue
    throw new Error(`${what} answered ${String(response.status())}: ${await response.text().catch(() => '')}`)
  }
}

async function settleRow(log: RequestLog, row: string, reads: { documents: number; versions: number }): Promise<void> {
  const documents = await log.answered('GET', row, reads.documents)
  await accepted(documents, `GET ${row}`)
  const drafts = new Map<string, number>()
  for (const document of documents) {
    const body = await document.json() as { draftVersionId?: string | null; draftVersionNumber?: number; documentsFrom?: string }
    if (!body.draftVersionId || body.documentsFrom === 'Draft') continue
    if (typeof body.draftVersionNumber !== 'number') throw new Error(`GET ${row} names the draft ${body.draftVersionId} and no draft version number.`)
    const path = `${row}/versions/${String(body.draftVersionNumber)}`
    drafts.set(path, (drafts.get(path) ?? 0) + 1)
  }
  for (const [path, times] of drafts) await accepted(await log.answered('GET', path, times), `GET ${path}`)
  await accepted(await log.answered('GET', `${row}/versions`, reads.versions), `GET ${row}/versions`)
}

export type Outcome = 'accepted' | 'refused' | 'either'

function judge(response: Response, outcome: Outcome, what: string): Response {
  if (outcome === 'refused' && response.ok()) throw new Error(`${what} should have been refused and answered ${String(response.status())}.`)
  return response
}

const refusable = (outcome: Outcome, method: string, path: RequestPath) => (outcome === 'accepted' ? undefined : { method, path })

export async function openPlayground(page: Page, url: string): Promise<void> {
  const log = logRequests(page)
  try {
    await page.goto(url)
    await accepted(await log.answered('GET', BLOCK_CATALOGUE), 'GET /api/workflow-blocks')
  } finally {
    log.stop()
  }
}

export async function createWorkflowRequest(page: Page, name: string): Promise<void> {
  await page.getByRole('button', { name: 'New workflow', exact: true }).click()
  await page.getByTestId('new-workflow-name').fill(name)
  const log = logRequests(page)
  try {
    await page.getByRole('button', { name: 'Create', exact: true }).click()
    const created = await log.answeredOnce('POST', '/api/workflows')
    const { id } = await created.json() as { id: string }
    await settleRow(log, `/api/workflows/${id}`, { documents: 2, versions: 1 })
  } finally {
    log.stop()
  }
}

export async function saveWorkflowRequest(page: Page, outcome: Outcome = 'accepted'): Promise<Response> {
  const log = logRequests(page, refusable(outcome, 'PUT', ROW))
  try {
    await page.getByRole('button', { name: 'Save', exact: true }).click()
    const saved = await log.answeredOnce('PUT', ROW)
    if (judge(saved, outcome, 'Save').ok()) await settleRow(log, pathnameOf(saved), { documents: 1, versions: 1 })
    return saved
  } finally {
    log.stop()
  }
}

export async function publishWorkflowRequest(page: Page, outcome: Outcome = 'accepted'): Promise<Response> {
  const log = logRequests(page, refusable(outcome, 'POST', PUBLISH))
  try {
    await page.getByRole('button', { name: 'Publish', exact: true }).click()
    const published = await log.answeredOnce('POST', PUBLISH)
    const row = pathnameOf(published).replace(/\/publish$/, '')
    const saved = log.started('PUT', ROW) > 0
    const reads = saved ? 2 : 1
    if (judge(published, outcome, 'Publish').ok()) await settleRow(log, row, { documents: reads, versions: reads })
    else if (saved) await settleRow(log, row, { documents: 1, versions: 1 })
    return published
  } finally {
    log.stop()
  }
}

async function until(check: () => Promise<string>, message: string): Promise<string> {
  let seen = 'waiting'
  await expect.poll(async () => { seen = await check(); return seen }, { message, timeout: 0 }).not.toBe('waiting')
  return seen
}

export async function runWorkflowRequest(page: Page, inputs: Record<string, string> = {}): Promise<void> {
  const log = logRequests(page)
  try {
    await page.getByRole('button', { name: 'Run', exact: true }).click()
    const dialog = page.getByRole('dialog')
    const sent = (): boolean => log.started('POST', EXECUTE) > 0
    const opened = await until(async () => (sent() ? 'sent' : await dialog.isVisible() ? 'inputs' : 'waiting'),
      'Run sent no request and opened no input dialog')
    if (opened === 'inputs') {
      for (const [name, value] of Object.entries(inputs)) await dialog.getByLabel(`Run input ${name}`).fill(value)
      await dialog.getByRole('button', { name: 'Run workflow' }).click()
      const alert = dialog.getByRole('alert')
      const asked = await until(async () => (sent() ? 'sent' : await alert.isVisible() ? 'invalid' : 'waiting'),
        'Run workflow sent no request and reported no invalid input')
      if (asked === 'invalid') throw new Error(`The run inputs were refused: ${(await alert.innerText()).trim()}`)
    }
    const [run] = await log.answered('POST', EXECUTE)
    if (run === undefined) throw new Error('The run was answered and then gone.')
    const executions = pathnameOf(run).replace(/\/execute$/, '/executions')
    const answers = [run]
    if (run.ok()) {
      const [list] = await log.answered('GET', executions)
      if (list !== undefined) answers.push(list)
      if (list?.ok()) {
        const newest = (await list.json() as { items?: { id: string }[] }).items?.[0]
        if (newest !== undefined) answers.push(...await log.answered('GET', `${executions}/${newest.id}`))
      }
    }
    const failed = answers.find((answer) => !answer.ok())
    if (failed !== undefined) {
      const message = page.getByTestId('status').filter({ hasText: /^Running:/ })
      await expect(message).toBeVisible()
      throw new Error(`The run was refused (${String(failed.status())}): ${(await message.innerText()).trim()}`)
    }
  } finally {
    log.stop()
  }
}

export async function rollbackWorkflowRequest(page: Page, versionNumber: number): Promise<Response> {
  const path = new RegExp(`^/api/workflows/[^/]+/versions/${String(versionNumber)}/rollback$`)
  const log = logRequests(page)
  try {
    await page.getByRole('button', { name: `Roll back to version ${String(versionNumber)}`, exact: true }).click()
    const rolled = await log.answeredOnce('POST', path)
    await settleRow(log, pathnameOf(rolled).replace(/\/versions\/\d+\/rollback$/, ''), { documents: 1, versions: 1 })
    return rolled
  } finally {
    log.stop()
  }
}

export async function openFileRequest(
  page: Page, file: { name: string; contents: string }, outcome: Outcome = 'accepted',
): Promise<Response> {
  const log = logRequests(page, refusable(outcome, 'POST', IMPORT_VALIDATE))
  try {
    await page.getByLabel('Open file').setInputFiles({
      name: file.name, mimeType: 'application/json', buffer: Buffer.from(file.contents),
    })
    return judge(await log.answeredOnce('POST', IMPORT_VALIDATE), outcome, 'Open file')
  } finally {
    log.stop()
  }
}
