// Integration fixture for the installed Hermes renderer (not a mock renderer).
// Copy beside Hermes's tool-group.test.tsx and run Vitest's ui project.
// CDX_HERMES_DISPLAY_ROWS: JSON export of the repaired real SQLite message rows,
// with tool_calls parsed as arrays. The captured regression covers 588 calls.
import { readFileSync } from 'node:fs'
import { AssistantRuntimeProvider, type ThreadMessage, useExternalStoreRuntime } from '@assistant-ui/react'
import { cleanup, fireEvent, render } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { toChatMessages } from '@/lib/chat-messages'
import { $activeSessionId } from '@/store/session'
import { $toolDisclosureStates } from '@/store/tool-view'
import { Thread } from '../thread'
import { buildToolView, type ToolPart } from './fallback-model'

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => window.setTimeout(() => cb(0), 0))
vi.stubGlobal('cancelAnimationFrame', window.clearTimeout)
Element.prototype.scrollTo = () => {}
Element.prototype.animate = () => ({ cancel() {}, finished: Promise.resolve() }) as Animation
for (const prop of ['offsetWidth', 'offsetHeight'])
  Object.defineProperty(HTMLElement.prototype, prop, { configurable: true, get: () => 800 })
const rows = JSON.parse(readFileSync(process.env.CDX_HERMES_DISPLAY_ROWS ?? 'C:/Users/l3gi0n/AppData/Local/Temp/cdx-hermes-display-renderer.json', 'utf8'))
const sessions = [...new Set(rows.map((r: any) => r.session_id))]
const parts = sessions.flatMap(id => toChatMessages(rows.filter((r: any) => r.session_id === id)))
  .flatMap(m => m.parts).filter(p => p.type === 'tool-call') as ToolPart[]

function Harness({ part }: { part: ToolPart }) {
  const message = {
    id: part.toolCallId, role: 'assistant', content: [part], createdAt: new Date(),
    status: { type: 'complete', reason: 'stop' },
    metadata: { unstable_state: null, unstable_annotations: [], unstable_data: [], steps: [], custom: {} }
  } as ThreadMessage
  const runtime = useExternalStoreRuntime<ThreadMessage>({ messages: [message], isRunning: false, onNew: async () => {} })
  return <AssistantRuntimeProvider runtime={runtime}><Thread /></AssistantRuntimeProvider>
}
afterEach(cleanup)
it('all 588 actual imported calls contain renderable arguments and results', () => {
  expect(parts).toHaveLength(588)
  for (const part of parts) {
    const view = buildToolView(part, '')
    expect(view.detail, part.toolCallId).toContain('Arguments:')
    expect(view.detail, part.toolCallId).toContain('Result:')
    expect(view.detail).toContain('Source tool:')
    if (part.toolName === 'execute_code') expect(view.title).not.toBe('Exec')
  }
})
it.each(['execute_code', 'terminal', 'Edit', 'Read', 'Grep', 'apply_patch', 'request_user_input_async'].filter(name => parts.some(p => p.toolName === name)))
  ('clicking an imported %s row shows the actual arguments and result', async name => {
    const part = parts.find(p => p.toolName === name)!
    $activeSessionId.set('cdx-display-test')
    $toolDisclosureStates.set({})
    const { container } = render(<Harness part={part} />)
    const group = container.querySelector('[data-tool-summary] button[aria-expanded="false"]')
    if (group) fireEvent.click(group)
    const toggle = container.querySelector('[data-tool-row] button[aria-expanded="false"]')
    expect(toggle, `missing expandable row for ${name}`).not.toBeNull()
    fireEvent.click(toggle!)
    expect(container.querySelector('[data-tool-row] button[aria-expanded="true"]')).not.toBeNull()
    const detail = container.querySelector('[data-tool-row]')!.textContent!
    expect(detail).toContain('Arguments:')
    expect(detail).toContain('Result:')
    expect(detail).toContain('Source tool:')
  })
