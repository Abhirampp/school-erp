import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, Auth, Ctx, DAYS, DayPipe, NetworkError, TPipe, Toast, today } from '../core';
import { Col, CrudTable } from '../crud-table';
import { loadSections } from './people';
import { Modal } from '../modal';

@Component({
  imports: [FormsModule, CrudTable, TPipe, Modal],
  template: `
    <div class="page-head"><div><h1>{{ 'Timetable' | t }}</h1><p class="muted">Map subjects and teachers to each class, then build the weekly grid. Teacher and room clashes are blocked.</p></div></div>
    <div class="card row">
      <label class="field">View<select [(ngModel)]="mode" (ngModelChange)="loadGrid()"><option value="section">By class</option><option value="teacher">By teacher</option></select></label>
      @if (mode === 'section') {
        <label class="field">Class<select [(ngModel)]="sectionId" (ngModelChange)="loadGrid()">@for (s of sections(); track s.id) { <option [ngValue]="s.id">{{ s.label }}</option> }</select></label>
      } @else {
        <label class="field">Teacher<select [(ngModel)]="teacherId" (ngModelChange)="loadGrid()">@for (s of staff(); track s.id) { <option [ngValue]="s.id">{{ s.firstName }} {{ s.lastName }}</option> }</select></label>
      }
      <label class="field">School week<select [(ngModel)]="week"><option value="mf">Monday to Friday</option><option value="st">Sunday to Thursday</option><option value="ms">Monday to Saturday</option></select></label>
      @if (canEdit) { <button class="btn" style="align-self:end" (click)="audit()">Check whole year for clashes</button> }
    </div>

    @if (clashes(); as c) {
      <div class="alert" [class.ok]="!c.length" [class.danger]="c.length">
        @if (!c.length) { No clashes in this academic year. } @else { {{ c.length }} clash(es): @for (x of c; track $index) { <div>{{ x.section }}, {{ dayName(x.day) }}: {{ x.message }}</div> } }
      </div>
    }

    <div class="card table-wrap">
      <table class="tt">
        <thead><tr><th style="width:110px">{{ 'Period' | t }}</th>@for (d of days(); track d) { <th>{{ dayName(d) | t }}</th> }</tr></thead>
        <tbody>
          @for (p of periods(); track p.id) {
            <tr>
              <th scope="row">{{ p.name }}<div class="small muted">{{ p.startTime.slice(0, 5) }}–{{ p.endTime.slice(0, 5) }}</div></th>
              @if (p.isBreak) { <td class="break" [attr.colspan]="days().length">{{ p.name }}</td> }
              @else {
                @for (d of days(); track d) {
                  @let s = cell(d, p.id);
                  <td [class.slot]="editable()" (click)="edit(d, p, s)" [attr.tabindex]="editable() ? 0 : null" (keydown.enter)="edit(d, p, s)">
                    @if (s) { <strong>{{ s.subject }}</strong><div class="small muted">{{ mode === 'teacher' ? s.section : s.teacher ?? 'No teacher' }}@if (s.room) { · {{ s.room }} }</div> }
                  </td>
                }
              }
            </tr>
          } @empty { <tr><td class="empty">Define the period structure in School setup first.</td></tr> }
        </tbody>
      </table>
    </div>

    <app-modal [open]="!!editing()" [title]="editing() ? dayName(editing().day) + ' · ' + editing().period.name : ''" width="min(480px, 94vw)" (closed)="editing.set(null)">
    @if (editing(); as e) {
        <form class="stack" (ngSubmit)="saveSlot()">
          <label class="field">Subject<select name="cs" [(ngModel)]="e.classSubjectId" required>@for (c of classSubjects(); track c.id) { <option [ngValue]="c.id">{{ c.label }}</option> }</select></label>
          <label class="field">Room<input type="text" name="room" [(ngModel)]="e.room" /></label>
          @if (slotError()) { <div class="alert danger" role="alert">{{ slotError() }}</div> }
          <div class="form-actions">
            @if (e.id) { <button class="btn danger" type="button" style="margin-inline-end:auto" (click)="clearSlot()">Clear slot</button> }
            <button class="btn" type="button" (click)="editing.set(null)">{{ 'Cancel' | t }}</button>
            <button class="btn primary" type="submit">{{ 'Save' | t }}</button></div>
        </form>
    }
    </app-modal>

    @if (canEdit && mode === 'section' && sectionId) {
      <crud-table title="Subjects and teachers for this class" path="/class-subjects" [columns]="csCols()" [scope]="{ academicYearId: ctx.yearId(), sectionId: sectionId }" (changed)="loadGrid()" />
    }
  `,
})
export class TimetablePage {
  ctx = inject(Ctx);
  private api = inject(Api);
  private auth = inject(Auth);
  private toast = inject(Toast);
  canEdit = this.auth.can('academics.manage');
  mode = 'section'; week = 'mf'; sectionId: string | null = null; teacherId: string | null = null;
  sections = signal<any[]>([]); staff = signal<any[]>([]); subjects = signal<any[]>([]); periods = signal<any[]>([]); slots = signal<any[]>([]);
  classSubjects = signal<any[]>([]); editing = signal<any>(null); slotError = signal(''); clashes = signal<any[] | null>(null);
  days = () => (this.week === 'st' ? [7, 1, 2, 3, 4] : this.week === 'ms' ? [1, 2, 3, 4, 5, 6] : [1, 2, 3, 4, 5]);
  editable = () => this.canEdit && this.mode === 'section';
  csCols = computed<Col[]>(() => [
    { key: 'subjectId', label: 'Subject', type: 'select', required: true, options: this.subjects().map((s) => ({ value: s.id, label: s.name })) },
    { key: 'teacherId', label: 'Teacher', type: 'select', options: this.staff().map((s) => ({ value: s.id, label: `${s.firstName} ${s.lastName}` })) },
  ]);

  constructor() {
    effect(() => { this.ctx.campusId(); this.ctx.yearId(); untracked(() => this.init()); });
  }
  dayName(d: number) { return DAYS[d - 1]; }
  cell(day: number, periodId: string) { return this.slots().find((s) => s.day === day && s.periodId === periodId); }
  async init() {
    const campusId = this.ctx.campusId();
    if (!campusId) return;
    try {
      const [sections, staff, subjects, periods] = await Promise.all([loadSections(this.api, this.ctx), this.api.get<any[]>('/staff', { campusId }), this.api.get<any[]>('/subjects', { campusId }), this.api.get<any[]>('/periods', { campusId })]);
      this.sections.set(sections); this.staff.set(staff); this.subjects.set(subjects); this.periods.set(periods.sort((a, b) => a.order - b.order));
      this.sectionId = sections[0]?.id ?? null;
      this.teacherId = this.auth.user()?.staffId ?? staff[0]?.id ?? null;
      this.clashes.set(null);
      await this.loadGrid();
    } catch (e) { this.toast.error(e); }
  }
  async loadGrid() {
    const yearId = this.ctx.yearId();
    if (!yearId) return;
    try {
      const filter = this.mode === 'section' ? { sectionId: this.sectionId } : { teacherId: this.teacherId };
      this.slots.set(Object.values(filter)[0] ? await this.api.get('/timetable', { academicYearId: yearId, ...filter }) : []);
      if (this.mode === 'section' && this.sectionId) {
        const cs = await this.api.get<any[]>('/class-subjects', { sectionId: this.sectionId });
        const subj = new Map(this.subjects().map((s) => [s.id, s.name])); const st = new Map(this.staff().map((s) => [s.id, `${s.firstName} ${s.lastName}`]));
        this.classSubjects.set(cs.map((c) => ({ ...c, label: `${subj.get(c.subjectId)} (${st.get(c.teacherId) ?? 'no teacher'})` })));
      }
    } catch (e) { this.toast.error(e); }
  }
  edit(day: number, period: any, slot: any) {
    if (!this.editable()) return;
    this.slotError.set('');
    this.editing.set({ day, period, id: slot?.id, classSubjectId: slot?.classSubjectId ?? this.classSubjects()[0]?.id, room: slot?.room ?? '' });
  }
  async saveSlot() {
    const e = this.editing();
    try {
      await this.api.put('/timetable', { sectionId: this.sectionId, classSubjectId: e.classSubjectId, periodId: e.period.id, day: e.day, room: e.room });
      this.editing.set(null); await this.loadGrid();
    } catch (err: any) { this.slotError.set(err.message); }
  }
  async clearSlot() { try { await this.api.del(`/timetable/${this.editing().id}`); this.editing.set(null); await this.loadGrid(); } catch (e) { this.toast.error(e); } }
  async audit() { try { this.clashes.set(await this.api.get('/timetable/clashes', { academicYearId: this.ctx.yearId() })); } catch (e) { this.toast.error(e); } }
}

const QUEUE_KEY = 'erp.attendanceQueue';

@Component({
  imports: [FormsModule, DayPipe, TPipe, Modal],
  template: `
    <div class="page-head"><div><h1>{{ 'Attendance' | t }}</h1><p class="muted">{{ ctx.campus()?.attendanceMode === 'period' ? 'Marked per period' : 'Marked once a day' }} for this campus. Parents are alerted when a child is marked absent or late.</p></div></div>
    <div class="tabs" role="tablist">
      @for (t of tabs(); track t) { <button role="tab" [attr.aria-selected]="tab() === t" (click)="tab.set(t); refresh()">{{ t | t }}</button> }
    </div>

    @if (queued().length) {
      <div class="alert warn" role="status">{{ queued().length }} register(s) saved on this device while offline. <button class="btn small" (click)="flush()">Sync now</button></div>
    }

    @if (tab() !== 'Leave') {
      <div class="card row">
        <label class="field">{{ 'Section' | t }}<select [(ngModel)]="sectionId" (ngModelChange)="refresh()">@for (s of sections(); track s.id) { <option [ngValue]="s.id">{{ s.label }}</option> }</select></label>
        @if (tab() === 'Register') {
          <label class="field">{{ 'Date' | t }}<input type="date" [(ngModel)]="date" [max]="maxDate" (ngModelChange)="refresh()" /></label>
          @if (ctx.campus()?.attendanceMode === 'period') {
            <label class="field">{{ 'Period' | t }}<select [(ngModel)]="periodId" (ngModelChange)="refresh()">@for (p of periods(); track p.id) { <option [ngValue]="p.id">{{ p.name }}</option> }</select></label>
          }
        } @else {
          <label class="field">From<input type="date" [(ngModel)]="from" (ngModelChange)="refresh()" /></label>
          <label class="field">To<input type="date" [(ngModel)]="to" (ngModelChange)="refresh()" /></label>
        }
      </div>
    }

    @switch (tab()) {
      @case ('Register') {
        <div class="card">
          <div class="page-head">
            <h2>{{ sheet().length }} students</h2>
            @if (canMark) { <div class="row"><button class="btn" (click)="allPresent()">Mark all present</button><button class="btn primary" [disabled]="!complete()" (click)="save()">Save register</button></div> }
          </div>
          <div class="table-wrap"><table>
            <thead><tr><th>Roll</th><th>{{ 'Name' | t }}</th><th>{{ 'Status' | t }}</th><th>Remark</th></tr></thead>
            <tbody>
              @for (r of sheet(); track r.enrollmentId) {
                <tr>
                  <td>{{ r.rollNumber }}</td><td>{{ r.name }}</td>
                  <td><div class="codes" role="group" [attr.aria-label]="'Status for ' + r.name">
                    @for (c of codes(); track c.id) {
                      <button type="button" [class.absent]="c.category === 'unauthorized'" [class.late]="c.category === 'late'" [attr.aria-pressed]="r.code === c.code" [title]="c.name" [disabled]="!canMark" (click)="r.code = c.code; touch()">{{ c.code }}</button>
                    }
                  </div></td>
                  <td><input type="text" [(ngModel)]="r.remarks" [disabled]="!canMark" [attr.aria-label]="'Remark for ' + r.name" /></td>
                </tr>
              } @empty { <tr><td colspan="4" class="empty">No students in this section.</td></tr> }
            </tbody>
          </table></div>
          <p class="small muted" style="margin-top:8px">@for (c of codes(); track c.id) { <span class="nowrap"><strong>{{ c.code }}</strong> {{ c.name }}@if (c.notifyParents) { (alerts parents) } &nbsp; </span> }</p>
        </div>
      }
      @case ('Summary') {
        <div class="card table-wrap"><table>
          <thead><tr><th>Roll</th><th>{{ 'Name' | t }}</th><th class="num">Days</th><th class="num">{{ 'Present' | t }}</th><th class="num">{{ 'Late' | t }}</th><th class="num">Authorised</th><th class="num">Unauthorised</th><th class="num">Attendance</th></tr></thead>
          <tbody>@for (r of summary(); track r.rollNumber) {
            <tr><td>{{ r.rollNumber }}</td><td>{{ r.name }}</td><td class="num">{{ r.total }}</td><td class="num">{{ r.present }}</td><td class="num">{{ r.late }}</td><td class="num">{{ r.authorized }}</td><td class="num">{{ r.unauthorized }}</td>
              <td class="num">@if (r.percent !== null) { <span class="badge" [class.ok]="r.percent >= 90" [class.warn]="r.percent < 90 && r.percent >= 75" [class.danger]="r.percent < 75">{{ r.percent }}%</span> } @else { – }</td></tr>
          }</tbody>
        </table></div>
      }
      @case ('Leave') {
        @if (auth.user()?.staffId) { <div class="row end" style="margin-bottom:12px"><button class="btn primary" (click)="leaveOpen.set(true)">Request leave</button></div> }
        <app-modal [open]="leaveOpen()" title="Request leave" width="min(560px, 94vw)" (closed)="leaveOpen.set(false)">
        @if (leaveOpen()) {
          <form (ngSubmit)="requestLeave()"><div class="grid cols-2">
            <label class="field">From<input type="date" name="f" [(ngModel)]="leave.fromDate" required /></label>
            <label class="field">To<input type="date" name="t" [(ngModel)]="leave.toDate" required /></label>
            </div>
            <label class="field" style="margin-top:12px">Reason<input type="text" name="r" [(ngModel)]="leave.reason" required /></label>
            <label class="check" style="margin-top:12px"><input type="checkbox" name="u" [(ngModel)]="leave.unpaid" /> Unpaid leave (deducted from salary)</label>
            <div class="form-actions"><button class="btn" type="button" (click)="leaveOpen.set(false)">Cancel</button><button class="btn primary" type="submit">Send request</button></div>
          </form>
        }
        </app-modal>
        <div class="card table-wrap"><table>
          <thead><tr><th>Staff</th><th>From</th><th>To</th><th>Reason</th><th>{{ 'Status' | t }}</th><th></th></tr></thead>
          <tbody>@for (l of leaves(); track l.id) {
            <tr><td>{{ l.staff }}</td><td>{{ l.fromDate | day }}</td><td>{{ l.toDate | day }}</td><td>{{ l.reason }} @if (l.unpaid) { <span class="badge">unpaid</span> }</td>
              <td><span class="badge" [class.ok]="l.status === 'approved'" [class.danger]="l.status === 'rejected'" [class.warn]="l.status === 'pending'">{{ l.status }}</span> @if (l.decidedByName) { <span class="small muted">by {{ l.decidedByName }}</span> }</td>
              <td class="nowrap" style="text-align:end">@if (l.status === 'pending' && auth.can('staff.leave.approve')) { <button class="btn small" (click)="decide(l, true)">Approve</button> <button class="btn small danger" (click)="decide(l, false)">Reject</button> }</td></tr>
          } @empty { <tr><td colspan="6" class="empty">No leave requests.</td></tr> }</tbody>
        </table></div>
      }
    }
  `,
})
export class AttendancePage {
  ctx = inject(Ctx);
  auth = inject(Auth);
  private api = inject(Api);
  private toast = inject(Toast);
  canMark = this.auth.can('attendance.mark');
  tabs = computed(() => (this.auth.can('attendance.mark', 'attendance.read') ? ['Register', 'Summary', 'Leave'] : ['Leave']));
  tab = signal(this.tabs()[0]);
  sections = signal<any[]>([]); codes = signal<any[]>([]); periods = signal<any[]>([]); sheet = signal<any[]>([]); summary = signal<any[]>([]); leaves = signal<any[]>([]);
  queued = signal<any[]>(JSON.parse(localStorage.getItem(QUEUE_KEY) ?? '[]'));
  private version = signal(0);
  complete = computed(() => { this.version(); return this.sheet().length > 0 && this.sheet().every((r) => r.code); });
  sectionId: string | null = null; periodId: string | null = null; date = today(); maxDate = today(); to = today(); from = new Date(Date.now() - 30 * 864e5).toISOString().slice(0, 10);
  leave: any = { reason: '' }; leaveOpen = signal(false);

  constructor() {
    effect(() => { this.ctx.campusId(); this.ctx.yearId(); untracked(() => this.init()); });
    // A register saved while offline is sent as soon as the connection returns.
    window.addEventListener('online', () => this.flush());
  }
  touch() { this.version.update((v) => v + 1); }
  async init() {
    const campusId = this.ctx.campusId();
    if (!campusId) return;
    try {
      const [sections, codes, periods] = await Promise.all([loadSections(this.api, this.ctx), this.api.get<any[]>('/absence-codes', { campusId }), this.api.get<any[]>('/periods', { campusId })]);
      const rank = ['present', 'late', 'unauthorized', 'authorized'];
      this.sections.set(sections); this.codes.set(codes.sort((a, b) => rank.indexOf(a.category) - rank.indexOf(b.category)));
      this.date = this.maxDate = this.to = today(this.ctx.campus()?.timeZone); this.periods.set(periods.filter((p) => !p.isBreak).sort((a, b) => a.order - b.order));
      this.sectionId = sections[0]?.id ?? null;
      this.periodId = this.ctx.campus()?.attendanceMode === 'period' ? this.periods()[0]?.id ?? null : null;
      await this.refresh();
    } catch (e) { this.toast.error(e); }
  }
  async refresh() {
    try {
      if (this.tab() === 'Leave') { this.leaves.set(await this.api.get('/leave-requests')); return; }
      if (!this.sectionId) { this.sheet.set([]); this.summary.set([]); return; }
      if (this.tab() === 'Register') this.sheet.set(await this.api.get('/attendance/sheet', { sectionId: this.sectionId, date: this.date, periodId: this.periodId }));
      else this.summary.set(await this.api.get('/attendance/summary', { sectionId: this.sectionId, from: this.from, to: this.to }));
      this.touch();
    } catch (e) { this.toast.error(e); }
  }
  allPresent() {
    const present = this.codes().find((c) => c.category === 'present');
    if (present) { this.sheet().forEach((r) => (r.code ??= present.code)); this.touch(); }
  }
  async save() {
    const body = { sectionId: this.sectionId, date: this.date, periodId: this.periodId, markedAt: new Date().toISOString(), marks: this.sheet().map((r) => ({ enrollmentId: r.enrollmentId, code: r.code, remarks: r.remarks || null })) };
    try {
      const r = await this.api.post('/attendance/sheet', body);
      this.toast.ok(`Register saved. ${r.alerts} parent alert(s) sent.`);
    } catch (e) {
      if (e instanceof NetworkError) { this.setQueue([...this.queued(), body]); this.toast.ok('You are offline. The register is saved on this device and will sync when you reconnect.'); }
      else this.toast.error(e);
    }
  }
  private setQueue(q: any[]) { this.queued.set(q); localStorage.setItem(QUEUE_KEY, JSON.stringify(q)); }
  async flush() {
    const remaining: any[] = [];
    for (const body of this.queued()) {
      try { await this.api.post('/attendance/sheet', body); } catch (e) { if (e instanceof NetworkError) remaining.push(body); else this.toast.error(e); }
    }
    const sent = this.queued().length - remaining.length;
    this.setQueue(remaining);
    if (sent) { this.toast.ok(`${sent} offline register(s) synced.`); this.refresh(); }
  }
  async requestLeave() { try { await this.api.post('/leave-requests', this.leave); this.leave = { reason: '' }; this.leaveOpen.set(false); this.toast.ok('Leave requested'); await this.refresh(); } catch (e) { this.toast.error(e); } }
  async decide(l: any, approve: boolean) { try { await this.api.post(`/leave-requests/${l.id}/decision`, { approve }); await this.refresh(); } catch (e) { this.toast.error(e); } }
}
