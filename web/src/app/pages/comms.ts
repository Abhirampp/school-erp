import { Component, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, Auth, Ctx, DayPipe, TPipe, Toast } from '../core';
import { loadSections } from './people';
import { Modal } from '../modal';

@Component({
  imports: [FormsModule, DayPipe, TPipe, Modal],
  template: `
    <div class="page-head"><div><h1>{{ 'Communication' | t }}</h1><p class="muted">Announcements by campus, grade or section, and digital consent forms with a stored record.</p></div></div>
    <div class="tabs" role="tablist">@for (t of tabs; track t) { <button role="tab" [attr.aria-selected]="tab() === t" (click)="tab.set(t); load()">{{ t }}</button> }</div>

    @if (tab() !== 'Delivery log') {
      @if (tab() === 'Announcements' ? auth.can('comms.announce') : auth.can('consent.manage')) {
        <div class="row end" style="margin-bottom:12px"><button class="btn primary" (click)="composeOpen.set(true)">{{ tab() === 'Announcements' ? 'New announcement' : 'New consent form' }}</button></div>
      }
      <app-modal [open]="composeOpen()" [title]="tab() === 'Announcements' ? 'New announcement' : 'New consent form'" (closed)="composeOpen.set(false)">
      @if (composeOpen()) {
        <form class="stack" (ngSubmit)="tab() === 'Announcements' ? announce() : createConsent()">
          <div class="grid cols-2">
            <label class="field">Title<input type="text" name="title" [(ngModel)]="form.title" required /></label>
            <label class="field">Send to<select name="aud" [(ngModel)]="form.audienceType" (ngModelChange)="form.audienceId = null"><option value="campus">Whole campus</option><option value="grade">One grade</option><option value="section">One section</option></select></label>
            @if (form.audienceType === 'grade') { <label class="field">Grade<select name="gid" [(ngModel)]="form.audienceId" required>@for (g of grades(); track g.id) { <option [ngValue]="g.id">{{ g.name }}</option> }</select></label> }
            @if (form.audienceType === 'section') { <label class="field">Section<select name="sid" [(ngModel)]="form.audienceId" required>@for (s of sections(); track s.id) { <option [ngValue]="s.id">{{ s.label }}</option> }</select></label> }
          </div>
          @if (tab() === 'Consent forms') {
            <div class="grid cols-2">
              <label class="field">Purpose (what the consent covers)<input type="text" name="purpose" [(ngModel)]="form.purpose" required /></label>
              <label class="field">Respond by<input type="date" name="due" [(ngModel)]="form.dueDate" /></label>
            </div>
          }
          <label class="field">{{ tab() === 'Announcements' ? 'Message' : 'Statement the guardian agrees to' }}<textarea name="body" [(ngModel)]="form.body" required></textarea></label>
          @if (tab() === 'Announcements') {
            <div class="row"><span class="small muted">Channels:</span>@for (c of channels; track c.key) { <label class="check"><input type="checkbox" [name]="'ch' + c.key" [(ngModel)]="c.on" /> {{ c.label }}</label> }</div>
          }
          <div class="form-actions"><button class="btn" type="button" (click)="composeOpen.set(false)">Cancel</button><button class="btn primary" type="submit">{{ tab() === 'Announcements' ? 'Send' : 'Publish to guardians' }}</button></div>
        </form>
      }
      </app-modal>
    }

    @switch (tab()) {
      @case ('Announcements') {
        @for (a of announcements(); track a.id) {
          <div class="card"><div class="page-head"><h2>{{ a.title }}</h2><span class="small muted">{{ a.createdAt | day: true }}</span></div><p>{{ a.body }}</p>
            <p class="small muted">Sent by {{ a.sentByName }} to {{ a.recipientCount }} families ({{ a.audienceType }}) via {{ a.channels }}</p></div>
        } @empty { <p class="empty">No announcements yet.</p> }
      }
      @case ('Consent forms') {
        @for (c of consentForms(); track c.form.id) {
          <div class="card">
            <div class="page-head"><div><h2>{{ c.form.title }}</h2><p class="small muted">{{ c.form.purpose }} · {{ c.form.audienceType }}@if (c.form.dueDate) { · respond by {{ c.form.dueDate | day }} }</p></div>
              <div class="row"><span class="badge ok">{{ c.counts?.granted ?? 0 }} granted</span><span class="badge danger">{{ c.counts?.declined ?? 0 }} declined</span><span class="badge">{{ c.counts?.withdrawn ?? 0 }} withdrawn</span>
                <button class="btn small" (click)="showResponses(c.form.id)">Responses</button></div></div>
            <p>{{ c.form.body }}</p>
            @if (responsesFor() === c.form.id) {
              <table><thead><tr><th>Student</th><th>Decided by</th><th>Decision</th><th>When</th><th>IP address</th></tr></thead>
                <tbody>@for (r of responses(); track r.id) { <tr><td>{{ r.student }}</td><td>{{ r.grantedByName }}</td><td>{{ r.withdrawnAt ? 'withdrawn ' : r.decision }} @if (r.withdrawnAt) { <span class="small muted">{{ r.withdrawnAt | day: true }}</span> }</td><td>{{ r.decidedAt | day: true }}</td><td class="small muted">{{ r.ip }}</td></tr> }
                @empty { <tr><td colspan="5" class="empty">No responses yet.</td></tr> }</tbody></table>
            }
          </div>
        } @empty { <p class="empty">No consent forms yet.</p> }
      }
      @case ('Delivery log') {
        <div class="card table-wrap"><table>
          <thead><tr><th>Queued</th><th>Channel</th><th>To</th><th>Subject</th><th>Type</th><th>Delivery</th></tr></thead>
          <tbody>@for (n of log(); track n.id) {
            <tr><td class="nowrap">{{ n.createdAt | day: true }}</td><td>{{ n.channel }}</td><td class="small">{{ n.address ?? 'in-app' }}</td><td>{{ n.subject }}</td><td>{{ n.category }}</td>
              <td><span class="badge" [class.ok]="n.status === 'sent'" [class.warn]="n.status === 'pending'" [class.danger]="n.status === 'failed'">{{ n.status }}</span> @if (n.error) { <span class="small muted">{{ n.error }}</span> }</td></tr>
          } @empty { <tr><td colspan="6" class="empty">Nothing sent yet.</td></tr> }</tbody>
        </table></div>
      }
    }
  `,
})
export class CommunicationPage {
  auth = inject(Auth);
  private ctx = inject(Ctx);
  private api = inject(Api);
  private toast = inject(Toast);
  tabs = ['Announcements', 'Consent forms', ...(this.auth.can('comms.announce', 'audit.read') ? ['Delivery log'] : [])];
  tab = signal('Announcements');
  announcements = signal<any[]>([]); consentForms = signal<any[]>([]); log = signal<any[]>([]); grades = signal<any[]>([]); sections = signal<any[]>([]);
  responses = signal<any[]>([]); responsesFor = signal<string | null>(null);
  channels = [{ key: 'inapp', label: 'In-app', on: true }, { key: 'email', label: 'Email', on: true }, { key: 'sms', label: 'SMS', on: false }, { key: 'push', label: 'Push', on: false }];
  form: any = { audienceType: 'campus', audienceId: null }; composeOpen = signal(false);

  constructor() { effect(() => { this.ctx.campusId(); this.ctx.yearId(); untracked(() => this.load()); }); }
  async load() {
    const campusId = this.ctx.campusId();
    if (!campusId) return;
    try {
      this.grades.set(await this.api.get('/grade-levels', { campusId })); this.sections.set(await loadSections(this.api, this.ctx));
      if (this.tab() === 'Announcements') this.announcements.set(await this.api.get('/announcements', { campusId }));
      else if (this.tab() === 'Consent forms') this.consentForms.set(await this.api.get('/consent-forms', { campusId }));
      else this.log.set(await this.api.get('/notifications/log'));
    } catch (e) { this.toast.error(e); }
  }
  async announce() {
    try {
      const r = await this.api.post('/announcements', { ...this.form, campusId: this.ctx.campusId(), channels: this.channels.filter((c) => c.on).map((c) => c.key) });
      this.toast.ok(`Sent to ${r.recipientCount} families`); this.form = { audienceType: 'campus', audienceId: null }; this.composeOpen.set(false); await this.load();
    } catch (e) { this.toast.error(e); }
  }
  async createConsent() {
    try {
      await this.api.post('/consent-forms', { ...this.form, campusId: this.ctx.campusId(), dueDate: this.form.dueDate || null });
      this.toast.ok('Published; guardians notified'); this.form = { audienceType: 'campus', audienceId: null }; this.composeOpen.set(false); await this.load();
    } catch (e) { this.toast.error(e); }
  }
  async showResponses(id: string) {
    if (this.responsesFor() === id) { this.responsesFor.set(null); return; }
    try { this.responses.set(await this.api.get(`/consent-forms/${id}/responses`)); this.responsesFor.set(id); } catch (e) { this.toast.error(e); }
  }
}

@Component({
  imports: [FormsModule, DayPipe, TPipe, Modal],
  template: `
    <div class="page-head"><h1>{{ 'Messages' | t }}</h1><button class="btn primary" (click)="startNew()">{{ 'New message' | t }}</button></div>
    <app-modal [open]="composing()" [title]="'New message' | t" (closed)="composing.set(false)">
    @if (composing()) {
      <form class="stack" (ngSubmit)="create()">
        <div class="grid cols-2">
          <label class="field">About<select name="st" [(ngModel)]="draft.studentId" (ngModelChange)="loadContacts()" required>@for (s of students(); track s.id) { <option [ngValue]="s.id">{{ s.name }}</option> }</select></label>
          <label class="field">To<select name="to" [(ngModel)]="draft.recipientUserId" required>@for (c of contacts(); track c.userId) { <option [ngValue]="c.userId">{{ c.name }}</option> }</select></label>
          <label class="field">Subject<input type="text" name="sub" [(ngModel)]="draft.subject" required /></label>
        </div>
        <label class="field">{{ 'Message' | t }}<textarea name="body" [(ngModel)]="draft.body" required></textarea></label>
        <div class="form-actions"><button class="btn" type="button" (click)="composing.set(false)">{{ 'Cancel' | t }}</button><button class="btn primary" type="submit">{{ 'Send' | t }}</button></div>
      </form>
    }
    </app-modal>
    <div class="grid cols-2">
      <div class="card">
        @for (t of threads(); track t.id) {
          <button class="ticket" [style.border-color]="open()?.thread.id === t.id ? 'var(--accent)' : null" (click)="openThread(t.id)"><strong>{{ t.subject }}</strong><div class="small muted">{{ t.with }} · about {{ t.student }} · {{ t.lastMessageAt | day: true }}</div></button>
        } @empty { <p class="empty">No conversations yet.</p> }
      </div>
      <div class="card">
        @if (open(); as o) {
          <h2>{{ o.thread.subject }}</h2>
          @for (m of o.messages; track m.id) { <div class="bubble" [class.me]="m.senderUserId === auth.user()?.id"><div class="meta">{{ m.senderName }} · {{ m.createdAt | day: true }}</div>{{ m.body }}</div> }
          <form class="row" (ngSubmit)="reply()"><label class="sr-only" for="reply">Reply</label><input id="reply" type="text" name="reply" style="flex:1;width:auto" [(ngModel)]="replyText" required /><button class="btn primary" type="submit">{{ 'Send' | t }}</button></form>
        } @else { <p class="empty">Choose a conversation.</p> }
      </div>
    </div>
  `,
})
export class MessagesPage {
  auth = inject(Auth);
  private api = inject(Api);
  private ctx = inject(Ctx);
  private toast = inject(Toast);
  threads = signal<any[]>([]); open = signal<any>(null); composing = signal(false); students = signal<any[]>([]); contacts = signal<any[]>([]);
  draft: any = {}; replyText = '';

  constructor() { this.load(); }
  async load() { try { this.threads.set(await this.api.get('/messages/threads')); } catch (e) { this.toast.error(e); } }
  async openThread(id: string) { try { this.open.set(await this.api.get(`/messages/threads/${id}`)); } catch (e) { this.toast.error(e); } }
  async startNew() {
    try {
      const list = this.auth.isPortal() ? await this.api.get<any[]>('/portal/children') : (await this.api.get<any[]>('/students', { campusId: this.ctx.campusId(), status: 'active' })).map((s) => ({ id: s.id, name: `${s.firstName} ${s.lastName}` }));
      this.students.set(list); this.draft = { studentId: list[0]?.id }; this.composing.set(true); await this.loadContacts();
    } catch (e) { this.toast.error(e); }
  }
  async loadContacts() {
    if (!this.draft.studentId) return;
    try { const c = await this.api.get<any[]>('/messages/contacts', { studentId: this.draft.studentId }); this.contacts.set(c); this.draft.recipientUserId = c[0]?.userId; } catch (e) { this.toast.error(e); }
  }
  async create() { try { const t = await this.api.post('/messages/threads', this.draft); this.composing.set(false); await this.load(); await this.openThread(t.id); } catch (e) { this.toast.error(e); } }
  async reply() { try { await this.api.post(`/messages/threads/${this.open().thread.id}`, { body: this.replyText }); this.replyText = ''; await this.openThread(this.open().thread.id); } catch (e) { this.toast.error(e); } }
}
