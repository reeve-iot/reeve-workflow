import { spawn } from 'node:child_process'
import type { ChildProcess } from 'node:child_process'
import { existsSync, mkdirSync, rmSync, writeFileSync } from 'node:fs'
import { createServer } from 'node:net'
import { setTimeout as delay } from 'node:timers/promises'
import { fileURLToPath } from 'node:url'
import { PostgreSqlContainer } from '@testcontainers/postgresql'
import type { StartedPostgreSqlContainer } from '@testcontainers/postgresql'
import { RedisContainer } from '@testcontainers/redis'
import type { StartedRedisContainer } from '@testcontainers/redis'
import { DEFAULT_OPERATOR_ID } from '../../src/host'
import { healthy } from './api-client'
import { STATE_FILE, readHarnessState } from './state'
import type { HarnessState } from './state'

const POSTGRES_IMAGE = 'timescale/timescaledb:latest-pg18'
const REDIS_IMAGE = 'redis:7-alpine'

const PAGE_ROOT = fileURLToPath(new URL('../../dist-page/', import.meta.url))

const HOST_ENTRYPOINT = process.env['E2E_HOST_ENTRYPOINT']
  ?? fileURLToPath(new URL('../../../../artifacts/dispatch-host/Dispatch.Host', import.meta.url))

const OPERATOR_ID = process.env['E2E_OPERATOR_ID'] ?? DEFAULT_OPERATOR_ID

const READY_TIMEOUT_MS = 120_000

const HEALTH_TIMEOUT_MS = 5_000

const HOST_CONSOLE_FILE = fileURLToPath(new URL('../test-results/host-console.log', import.meta.url))

const MAX_ATTEMPTS = 3

class HostExitedError extends Error {}

interface Running {
  readonly postgres: StartedPostgreSqlContainer
  readonly redis: StartedRedisContainer
  readonly host: ChildProcess
  readonly console: string[]
}

let running: Running | null = null

export async function startHarness(): Promise<HarnessState> {
  if (!existsSync(HOST_ENTRYPOINT)) {
    throw new Error(
      `The published host is not at '${HOST_ENTRYPOINT}'. Publish it:`
      + ' dotnet publish examples/dispatch-host/Dispatch.Host/Dispatch.Host.csproj'
      + ' -c Release -r linux-x64 --self-contained true -o artifacts/dispatch-host',
    )
  }
  if (!existsSync(PAGE_ROOT)) {
    throw new Error(`The page is not built at '${PAGE_ROOT}'. Build it with: npm run build --workspace @reeve-iot/workflow-playground`)
  }

  const postgres = await new PostgreSqlContainer(POSTGRES_IMAGE).withTmpFs({ '/var/lib/postgresql': 'rw' }).start()
  const redis = await new RedisContainer(REDIS_IMAGE).withTmpFs({ '/data': 'rw' }).start()
  try {
    for (let attempt = 1; ; attempt++) {
      const port = await freePort()
      const baseUrl = `http://127.0.0.1:${String(port)}`
      const host = spawn(HOST_ENTRYPOINT, [], {
        env: {
          ...process.env,
          ASPNETCORE_URLS: baseUrl,
          DISPATCH_POSTGRES: `Host=${postgres.getHost()};Port=${String(postgres.getPort())};`
            + `Database=${postgres.getDatabase()};Username=${postgres.getUsername()};Password=${postgres.getPassword()}`,
          DISPATCH_REDIS: `${redis.getHost()}:${String(redis.getPort())}`,
          DISPATCH_REPLICA_ID: 'e2e-editor',
          Logging__Console__FormatterName: 'simple',
          Logging__Console__FormatterOptions__TimestampFormat: 'yyyy-MM-ddTHH:mm:ss.fff ',
          Logging__Console__FormatterOptions__UseUtcTimestamp: 'true',
          DISPATCH_TEST_STATIC_ROOT: PAGE_ROOT,
        },
        stdio: ['ignore', 'pipe', 'pipe'],
      })
      const console: string[] = []
      host.stdout?.on('data', (chunk: Buffer) => { console.push(chunk.toString()) })
      host.stderr?.on('data', (chunk: Buffer) => { console.push(chunk.toString()) })

      running = { postgres, redis, host, console }
      try {
        await waitUntilServing(baseUrl, host, console)
      } catch (error) {
        if (attempt >= MAX_ATTEMPTS || !(error instanceof HostExitedError)) throw error
        await stopHost(host)
        continue
      }

      const state: HarnessState = { baseUrl, operatorId: OPERATOR_ID, hostPid: host.pid ?? 0 }
      mkdirSync(fileURLToPath(new URL('../test-results/', import.meta.url)), { recursive: true })
      writeFileSync(STATE_FILE, `${JSON.stringify(state, null, 2)}\n`)
      return state
    }
  } catch (error) {
    await stopHarness()
    throw error
  }
}

export async function stopHarness(): Promise<void> {
  const started = running
  running = null
  if (started === null) {
    killLeftoverHost()
    return
  }
  await stopHost(started.host)
  keepHostConsole(started.console)
  await started.redis.stop()
  await started.postgres.stop()
}

function keepHostConsole(console: readonly string[]): void {
  try {
    writeFileSync(HOST_CONSOLE_FILE, console.join(''))
  } catch {
  }
}

async function waitUntilServing(baseUrl: string, host: ChildProcess, console: readonly string[]): Promise<void> {
  const deadline = Date.now() + READY_TIMEOUT_MS
  while (Date.now() < deadline) {
    if (host.exitCode !== null) {
      throw new HostExitedError(`The host exited with ${String(host.exitCode)} before serving.\n${console.join('')}`)
    }
    try {
      if (await healthy(baseUrl, HEALTH_TIMEOUT_MS)) return
    } catch {
    }
    await delay(100)
  }
  throw new Error(`The host never answered /health within ${String(READY_TIMEOUT_MS)}ms.\n${console.join('')}`)
}

function stopHost(host: ChildProcess): Promise<void> {
  if (host.exitCode !== null || host.signalCode !== null) return Promise.resolve()
  return new Promise<void>((resolve) => {
    const done = setTimeout(() => { host.kill('SIGKILL'); resolve() }, 10_000)
    host.once('exit', () => { clearTimeout(done); resolve() })
    host.kill('SIGTERM')
  })
}

function killLeftoverHost(): void {
  let state: HarnessState
  try {
    state = readHarnessState()
  } catch {
    return
  }
  try {
    process.kill(state.hostPid, 'SIGTERM')
  } catch {
  }
  rmSync(STATE_FILE, { force: true })
}

async function freePort(): Promise<number> {
  const probe = createServer()
  await new Promise<void>((resolve, reject) => {
    probe.once('error', reject)
    probe.listen(0, '127.0.0.1', resolve)
  })
  const address = probe.address()
  const port = typeof address === 'object' && address !== null ? address.port : 0
  await new Promise<void>((resolve) => { probe.close(() => { resolve() }) })
  if (port === 0) throw new Error('The probe socket reported no port.')
  return port
}
