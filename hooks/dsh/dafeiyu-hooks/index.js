// 大肥鱼事件转发：把 DSH 的 Agent 事件转发给桌宠，让她按 events.txt 演一小下。
// 不经过模型、不花 token；发不出去静默。事件是 scope-filtered 的（@deepseek-ai/dsh-scope）：
// agent/* 事件要挂在每个 agent 的 ctx 上（agent.ctx），tools/result、subagent/end 挂在 root ctx。
import { readFile, appendFile } from 'node:fs/promises'

export const name = 'dafeiyu-hooks'

// 编译、测试、失败正则：照搬 Claude 的 fish_hook.py。
const BUILD = /\b(dotnet\s+(build|publish|msbuild)|msbuild|npm\s+run\s+build|yarn\s+build|pnpm\s+build|cargo\s+build|go\s+build|make\b|cmake\s+--build|gradle|mvn|tsc\b|vite\s+build|setup\.ps1)/i
const TEST = /(\bdotnet\s+test\b|\.Tests\b|\bpytest\b|python\S*\s+-m\s+(pytest|unittest)|\bnpm\s+(run\s+)?test\b|\byarn\s+test\b|\bpnpm\s+test\b|\bjest\b|\bvitest\b|\bcargo\s+test\b|\bgo\s+test\b|\bmvn\s+test\b|\bgradle\w*\s+test\b)/i
const FAILED = /(\b[1-9]\d*\s+(failed|failing|errors?)\b|\bFAILED\b|\bFailed!|失败\s*[:：]?\s*[1-9]|Build FAILED|\berror\s+[A-Z]{2,}\d+)/
const LONG_TASK = 180 * 1000 // 这一轮从用户发消息算起超过 3 分钟算「长任务」

export function apply(ctx, config) {
  const apiPath = config?.apiPath // 插件里的 api.json（cordis.patch.yml 里填）
  const logPath = new URL('./hook.log', import.meta.url) // 调试用：最近转发了什么、桌宠怎么回的

  async function log(msg) {
    try { await appendFile(logPath, `${new Date().toISOString()} ${msg}\n`, 'utf-8') } catch {}
  }

  async function sendEvent(eventName) {
    try {
      const api = JSON.parse(await readFile(apiPath, 'utf-8'))
      const res = await fetch(`${api.url}/call/event`, {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${api.token}`,
          'Content-Type': 'application/json',
          'X-FishTray': '1',
        },
        body: JSON.stringify({ name: eventName }),
      })
      const text = await res.text()
      await log(`sent ${eventName}: ${text.slice(0, 120)}`)
    } catch (e) {
      await log(`FAIL ${eventName}: ${e?.message || e}`)
    }
  }

  // pwsh / bash 工具的命令在 exec.arguments.command 里。
  function commandOf(exec) {
    const a = exec?.arguments
    return a && typeof a === 'object' && typeof a.command === 'string' ? a.command : ''
  }

  // 从工具结果里拼出完整文本、退出码和是否抛错。DSH 的 pwsh/bash 命令即使退出码非 0，
  // 工具本身也不抛异常（isError 只表示基础设施失败），退出码在渲染结果末尾的 `[exit code: N]` 里。
  function outcomeOf(result) {
    const parts = []
    let exitCode = null
    for (const block of result?.content || []) {
      if (block && typeof block === 'object' && typeof block.text === 'string') parts.push(block.text)
    }
    const v = result?.value
    if (typeof v === 'string') parts.push(v)
    else if (v && typeof v === 'object') {
      for (const key of ['stdout', 'stderr', 'output', 'text', 'result']) {
        if (typeof v[key] === 'string') parts.push(v[key])
      }
      if (typeof v.exitCode === 'number') exitCode = v.exitCode
      else if (typeof v.exit_code === 'number') exitCode = v.exit_code
    }
    if (result?.isError) parts.push(String(result?.error?.message ?? ''))
    const text = parts.join('\n')
    if (exitCode === null) {
      const m = /\[exit code:\s*(\d+)\]/i.exec(text)
      if (m) exitCode = Number(m[1])
    }
    return { text, exitCode, isError: result?.isError === true }
  }

  const disposers = new Map()
  const turnStart = new Map() // agentId -> 用户发消息的时间（毫秒）

  function registerAgent(agent) {
    if (!agent?.ctx || typeof agent.ctx.on !== 'function' || disposers.has(agent.id)) return
    const actx = agent.ctx
    const dispose = actx.effect(() => {
      actx.on('agent/error', () => void sendEvent('出错'))
      actx.on('agent/inbox/inserted', () => {
        turnStart.set(agent.id, Date.now())
        void sendEvent('收到')
      })
      actx.on('agent/turn-stopping', () => {
        const started = turnStart.get(agent.id)
        turnStart.delete(agent.id)
        void sendEvent(started && Date.now() - started > LONG_TASK ? '长任务完成' : '完成')
      })
      actx.on('approval/request', (_req, next) => { void sendEvent('授权'); return next() })
      actx.on('user-questions/request', (_req, next) => { void sendEvent('等你'); return next() })
    })
    disposers.set(agent.id, () => { turnStart.delete(agent.id); dispose() })
    void log(`registered ${agent.id}`)
  }

  // 1. 枚举现有 agent：插件加载前就已经创建的（比如当前会话）也挂上，不用重启。
  try {
    const agents = ctx.get?.('agents')
    const list = agents?.list?.() || agents?.roots?.() || []
    for (const agent of list) registerAgent(agent)
  } catch {}

  // 2. 未来新创建的 agent。
  const offCreated = ctx.on('agent/created', (payload) => registerAgent(payload?.agent))

  // 3. subagent/end 挂在 root 上（已验证能收到）。
  const offSubagent = ctx.on('subagent/end', () => void sendEvent('子任务完成'))

  // 4. tools/result 挂在 root 上（Scoped<ToolsService>，和 subagent/end 一样不是 agent 级）：命令执行完。
  const offTools = ctx.on('tools/result', (exec, result) => {
    const command = commandOf(exec)
    if (!command) return
    const { text, exitCode, isError } = outcomeOf(result)
    const failed = isError || (exitCode !== null && exitCode !== 0)
    const isBuild = BUILD.test(command)
    const isTest = TEST.test(command)
    if (failed) {
      // 命令失败（退出码非 0 或工具抛错）：编译/测试命令 → 编译失败，其余照旧 → 出错。
      const name = isBuild || isTest ? '编译失败' : '出错'
      void log(`tool fail -> ${name} cmd=${command.slice(0, 100)}`)
      void sendEvent(name)
    } else if (isTest) {
      // 测试命令成功：输出里看得出有失败 → 编译失败，否则 → 测试通过。
      const name = FAILED.test(text) ? '编译失败' : '测试通过'
      void log(`tool ok(test) -> ${name} cmd=${command.slice(0, 100)}`)
      void sendEvent(name)
    }
    // 编译命令成功（退出码 0）什么都不发，和 fish_hook 一致。
  })

  return () => {
    offCreated?.()
    offSubagent?.()
    offTools?.()
    for (const d of disposers.values()) d?.()
  }
}
