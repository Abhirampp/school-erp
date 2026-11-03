import { Component, ElementRef, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, Ctx } from './core';

interface Turn { role: 'user' | 'assistant'; content: string; tools?: string[]; error?: boolean }

const TOOL_NAMES: Record<string, string> = {
  school_overview: 'school overview', find_students: 'student search', student_details: 'student record', class_details: 'class',
  attendance_report: 'attendance', fee_report: 'fees', exam_results: 'exam results', staff_lookup: 'staff', admissions_report: 'admissions', payroll_report: 'payroll',
};

const SUGGESTIONS = [
  'Who was absent today?',
  'Which students have the largest overdue fees?',
  'How did Grade 5 A do in Term 1 exams?',
  'Who teaches Mathematics, and how many periods a week?',
];

/** Escapes HTML, then renders the small Markdown subset the assistant uses: paragraphs, bold, code, lists, tables. */
export function renderMarkdown(src: string): string {
  const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  const inline = (s: string) => esc(s).replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>').replace(/`([^`]+)`/g, '<code>$1</code>');
  const lines = src.replace(/\r/g, '').split('\n');
  const out: string[] = [];
  for (let i = 0; i < lines.length; ) {
    const line = lines[i];
    if (/^\s*\|.*\|\s*$/.test(line) && i + 1 < lines.length && /^\s*\|[\s:|-]+\|\s*$/.test(lines[i + 1])) {
      const cells = (l: string) => l.trim().replace(/^\||\|$/g, '').split('|').map((c) => inline(c.trim()));
      const head = cells(line);
      i += 2;
      const rows: string[][] = [];
      while (i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i])) rows.push(cells(lines[i++]));
      out.push(`<table><thead><tr>${head.map((h) => `<th>${h}</th>`).join('')}</tr></thead><tbody>${rows.map((r) => `<tr>${r.map((c) => `<td>${c}</td>`).join('')}</tr>`).join('')}</tbody></table>`);
    } else if (/^\s*([-*]|\d+\.)\s+/.test(line)) {
      const ordered = /^\s*\d+\./.test(line);
      const items: string[] = [];
      while (i < lines.length && /^\s*([-*]|\d+\.)\s+/.test(lines[i])) items.push(`<li>${inline(lines[i++].replace(/^\s*([-*]|\d+\.)\s+/, ''))}</li>`);
      out.push(ordered ? `<ol>${items.join('')}</ol>` : `<ul>${items.join('')}</ul>`);
    } else if (/^#{1,4}\s+/.test(line)) {
      out.push(`<p><strong>${inline(line.replace(/^#+\s+/, ''))}</strong></p>`); i++;
    } else if (line.trim() === '') { i++; }
    else {
      const para: string[] = [];
      while (i < lines.length && lines[i].trim() !== '' && !/^\s*([-*]|\d+\.)\s+|^\s*\||^#{1,4}\s/.test(lines[i])) para.push(inline(lines[i++]));
      out.push(`<p>${para.join('<br>')}</p>`);
    }
  }
  return out.join('');
}

@Component({
  selector: 'app-assistant',
  imports: [FormsModule],
  styles: [`
    .fab { position: fixed; inset-block-end: 20px; inset-inline-end: 20px; z-index: 40; border-radius: 999px; padding: 10px 18px; box-shadow: 0 6px 18px rgba(16,24,40,.25); }
    .panel { position: fixed; inset-block-end: 20px; inset-inline-end: 20px; z-index: 41; width: min(420px, calc(100vw - 32px)); height: min(620px, calc(100vh - 40px));
      background: var(--surface); border: 1px solid var(--border); border-radius: 12px; box-shadow: 0 16px 40px rgba(16,24,40,.3); display: flex; flex-direction: column; overflow: hidden; }
    header { display: flex; justify-content: space-between; align-items: center; padding: 12px 14px; border-bottom: 1px solid var(--border); gap: 8px; }
    header h2 { margin: 0; font-size: 15px; }
    .log { flex: 1; overflow-y: auto; padding: 14px; display: flex; flex-direction: column; gap: 10px; }
    .msg { max-width: 92%; padding: 9px 12px; border-radius: 12px; font-size: 13.5px; line-height: 1.5; overflow-wrap: anywhere; }
    .msg.user { align-self: flex-end; background: var(--accent); color: #fff; border-end-end-radius: 4px; white-space: pre-wrap; }
    .msg.assistant { align-self: flex-start; background: var(--surface-2); border-end-start-radius: 4px; }
    .msg.error { background: var(--danger-soft); color: var(--danger); }
    .msg :is(p, ul, ol) { margin: 0 0 6px; } .msg :is(p, ul, ol):last-child { margin-bottom: 0; }
    .msg ul, .msg ol { padding-inline-start: 18px; }
    .msg table { font-size: 12.5px; margin: 4px 0; background: var(--surface); border-radius: 6px; }
    .msg th, .msg td { padding: 4px 6px; }
    .tools { font-size: 11.5px; color: var(--muted); margin-top: 2px; }
    .chips { display: flex; flex-direction: column; gap: 6px; }
    .chips button { text-align: start; }
    form { display: flex; gap: 8px; padding: 10px; border-top: 1px solid var(--border); }
    textarea { min-height: 40px; max-height: 120px; resize: none; }
    .typing { color: var(--muted); font-size: 13px; }
  `],
  template: `
    @if (!open()) {
      <button class="btn primary fab" (click)="toggle()" aria-label="Open the data assistant">Ask the assistant</button>
    } @else {
      <section class="panel" role="dialog" aria-label="Data assistant">
        <header>
          <div><h2>Data assistant</h2><div class="small muted">Answers from the school's records you can see</div></div>
          <div class="row">
            @if (turns().length) { <button class="btn small" (click)="clear()">New chat</button> }
            <button class="btn small" (click)="toggle()" aria-label="Close">✕</button>
          </div>
        </header>
        <div class="log" #log aria-live="polite">
          @if (configured() === false) {
            <div class="alert warn">The assistant isn't set up on this server yet. An administrator needs to add an Anthropic API key; the README explains how.</div>
          }
          @if (!turns().length && configured() !== false) {
            <p class="small muted">Ask about students, classes, attendance, exam results, fees, staff, admissions or payroll. You'll only get answers from records your role can see.</p>
            <div class="chips">@for (s of suggestions; track s) { <button class="btn small" (click)="send(s)">{{ s }}</button> }</div>
          }
          @for (t of turns(); track $index) {
            @if (t.role === 'user') { <div class="msg user">{{ t.content }}</div> }
            @else {
              <div class="msg assistant" [class.error]="t.error" [innerHTML]="html(t.content)"></div>
              @if (t.tools?.length) { <div class="tools">Checked: {{ toolLabels(t.tools!) }}</div> }
            }
          }
          @if (busy()) { <div class="typing">Looking that up…</div> }
        </div>
        <form (ngSubmit)="send()">
          <label class="sr-only" for="assistant-input">Your question</label>
          <textarea id="assistant-input" name="q" [(ngModel)]="draft" placeholder="Ask a question" (keydown.enter)="onEnter($event)" [disabled]="busy() || configured() === false"></textarea>
          <button class="btn primary" type="submit" [disabled]="busy() || !draft.trim() || configured() === false">Send</button>
        </form>
      </section>
    }
  `,
})
export class AssistantPanel {
  private api = inject(Api);
  private ctx = inject(Ctx);
  private logRef = viewChild<ElementRef<HTMLElement>>('log');
  suggestions = SUGGESTIONS;
  open = signal(false); busy = signal(false); turns = signal<Turn[]>([]); configured = signal<boolean | null>(null);
  draft = '';

  async toggle() {
    this.open.set(!this.open());
    if (this.open() && this.configured() === null) {
      try { this.configured.set((await this.api.get('/assistant/status')).configured); } catch { this.configured.set(null); }
    }
    this.scroll();
  }
  clear() { this.turns.set([]); }
  html(md: string) { return renderMarkdown(md); }
  toolLabels(tools: string[]) { return [...new Set(tools.map((t) => TOOL_NAMES[t] ?? t))].join(', '); }
  onEnter(e: Event) {
    const k = e as KeyboardEvent;
    if (!k.shiftKey) { k.preventDefault(); this.send(); }
  }
  private scroll() { setTimeout(() => { const el = this.logRef()?.nativeElement; if (el) el.scrollTop = el.scrollHeight; }); }

  async send(text?: string) {
    const q = (text ?? this.draft).trim();
    if (!q || this.busy()) return;
    this.draft = '';
    // Failed turns are shown but not sent back as history.
    const history = this.turns().filter((t) => !t.error).map((t) => ({ role: t.role, content: t.content }));
    this.turns.update((t) => [...t, { role: 'user', content: q }]);
    this.busy.set(true); this.scroll();
    try {
      const r = await this.api.post('/assistant/chat', { messages: [...history, { role: 'user', content: q }], campusId: this.ctx.campusId() });
      this.turns.update((t) => [...t, { role: 'assistant', content: r.reply, tools: r.toolsUsed }]);
    } catch (e: any) {
      this.turns.update((t) => [...t, { role: 'assistant', content: e.message ?? 'Something went wrong.', error: true }]);
    } finally { this.busy.set(false); this.scroll(); }
  }
}
