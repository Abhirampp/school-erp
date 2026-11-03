import { Component, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, Auth, Ctx, DayPipe, TPipe, Toast } from '../core';
import { loadSections } from './people';
import { Modal } from '../modal';

const STATUS_CLASS: Record<string, string> = { open: 'warn', submitted: 'info', approved: 'info', published: 'ok' };

@Component({
  imports: [FormsModule, DayPipe, TPipe, Modal],
  template: `
    <div class="page-head"><div><h1>{{ 'Exams' | t }}</h1><p class="muted">Teachers enter marks, the principal approves, then results are published to parents.</p></div></div>
    <div class="tabs" role="tablist">
      @for (t of tabs; track t) { <button role="tab" [attr.aria-selected]="tab() === t" (click)="tab.set(t); marks.set(null)">{{ t }}</button> }
    </div>

    @if (tab() !== 'Grading schemes') {
      <div class="card row">
        <label class="field">{{ 'Section' | t }}<select [(ngModel)]="sectionId" (ngModelChange)="load()">@for (s of sections(); track s.id) { <option [ngValue]="s.id">{{ s.label }}</option> }</select></label>
        <label class="field">{{ 'Term' | t }}<select [(ngModel)]="termId" (ngModelChange)="load()">@for (t of terms(); track t.id) { <option [ngValue]="t.id">{{ t.name }}</option> }</select></label>
        @if (tab() === 'Report cards') {
          <label class="field">PDF language<select [(ngModel)]="lang"><option value="">Campus default</option><option value="en">English</option><option value="ar">العربية</option><option value="hi">हिन्दी</option></select></label>
          <button class="btn primary" style="align-self:end" [disabled]="!cards().length" (click)="pdf()">Print all (PDF)</button>
        }
      </div>
    }

    @switch (tab()) {
      @case ('Marks') {
        @if (marks(); as m) {
          <div class="card">
            <div class="page-head">
              <div><h2>{{ m.plan.name }} <span class="badge" [class]="'badge ' + statusClass[m.plan.status]">{{ m.plan.status }}</span></h2>
                @if (m.plan.marksDeadline) { <p class="small muted">Marks due {{ m.plan.marksDeadline | day: true }}</p> }</div>
              <div class="row">
                <button class="btn" (click)="marks.set(null)">Back</button>
                @if (m.plan.status === 'open' && canEnter) { <button class="btn primary" (click)="saveMarks()">Save marks</button><button class="btn" (click)="workflow(m.plan, 'submit')">Submit for approval</button> }
              </div>
            </div>
            @if (m.plan.status !== 'open') { <div class="alert">Marks are locked. Only the principal can reopen this plan.</div> }
            <div class="table-wrap"><table>
              <thead><tr><th>Roll</th><th>{{ 'Name' | t }}</th>@for (c of m.components; track c.id) { <th>{{ c.name }}<div class="small muted">out of {{ c.maxMarks }} · {{ c.weight }}%</div></th> }<th class="num">%</th><th>{{ 'Grade' | t }}</th></tr></thead>
              <tbody>@for (r of m.rows; track r.enrollmentId) {
                <tr><td>{{ r.rollNumber }}</td><td>{{ r.name }}</td>
                  @for (s of r.scores; track s.assessmentId; let i = $index) {
                    <td><div class="row" style="flex-wrap:nowrap">
                      <input type="number" style="width:76px" min="0" [max]="m.components[i].maxMarks" step="0.5" [(ngModel)]="s.marks" [disabled]="m.plan.status !== 'open' || s.isAbsent || !canEnter" [attr.aria-label]="m.components[i].name + ' for ' + r.name" />
                      <label class="check small" title="Absent"><input type="checkbox" [(ngModel)]="s.isAbsent" [disabled]="m.plan.status !== 'open' || !canEnter" /> Abs</label>
                    </div></td>
                  }
                  <td class="num">{{ r.result.percent ?? '–' }}</td>
                  <td><strong>{{ r.result.grade ?? '–' }}</strong> @if (!r.result.complete) { <span class="badge warn">incomplete</span> }</td></tr>
              }</tbody>
            </table></div>
          </div>
        } @else {
          @if (canManage) { <div class="row end" style="margin-bottom:12px"><button class="btn primary" (click)="planOpen.set(true)">New assessment plan</button></div> }
          <div class="card table-wrap"><table>
            <thead><tr><th>{{ 'Subject' | t }}</th><th>Plan</th><th>Components</th><th>Scheme</th><th>{{ 'Status' | t }}</th><th></th></tr></thead>
            <tbody>@for (p of plans(); track p.id) {
              <tr><td>{{ p.subject }}</td><td>{{ p.name }}</td><td class="small muted">@for (c of p.components; track c.id) { {{ c.name }} {{ c.weight }}%{{ $last ? '' : ', ' }} }</td><td class="small">{{ p.scheme }}</td>
                <td><span [class]="'badge ' + statusClass[p.status]">{{ p.status }}</span> @if (p.approvedByName) { <div class="small muted">approved by {{ p.approvedByName }}</div> }</td>
                <td class="nowrap" style="text-align:end">
                  <button class="btn small" (click)="openMarks(p)">{{ p.status === 'open' && canEnter ? 'Enter marks' : 'View marks' }}</button>
                  @if (canApprove && p.status === 'submitted') { <button class="btn small primary" (click)="workflow(p, 'approve')">Approve</button> }
                  @if (canApprove && p.status === 'approved') { <button class="btn small primary" (click)="workflow(p, 'publish')">Publish to parents</button> }
                  @if (canApprove && (p.status === 'submitted' || p.status === 'approved')) { <button class="btn small" (click)="workflow(p, 'reopen')">Reopen</button> }
                  @if (canManage && p.status === 'open') { <button class="btn small danger" (click)="deletePlan(p)">Delete</button> }
                </td></tr>
            } @empty { <tr><td colspan="6" class="empty">No assessment plans for this section and term.</td></tr> }</tbody>
          </table></div>

          <app-modal [open]="planOpen()" title="New assessment plan" width="min(860px, 94vw)" (closed)="planOpen.set(false)">
          @if (planOpen()) {
            <form class="stack" (ngSubmit)="createPlan()">
              <div class="grid cols-2">
                <label class="field">Subject<select name="cs" [(ngModel)]="plan.classSubjectId" required>@for (c of classSubjects(); track c.id) { <option [ngValue]="c.id">{{ c.label }}</option> }</select></label>
                <label class="field">Grading scheme<select name="gs" [(ngModel)]="plan.gradingSchemeId" required>@for (s of schemes(); track s.id) { <option [ngValue]="s.id">{{ s.name }} v{{ s.version }}</option> }</select></label>
                <label class="field">Name<input type="text" name="name" [(ngModel)]="plan.name" required /></label>
                <label class="field">Marks entry deadline<input type="datetime-local" name="dl" [(ngModel)]="plan.marksDeadline" /></label>
              </div>
              <table><thead><tr><th>Component</th><th>Weight %</th><th>Maximum marks</th><th></th></tr></thead><tbody>
                @for (c of plan.components; track $index) {
                  <tr><td><input type="text" [name]="'cn' + $index" [(ngModel)]="c.name" aria-label="Component name" required /></td><td><input type="number" [name]="'cw' + $index" [(ngModel)]="c.weight" aria-label="Weight" min="0" /></td>
                    <td><input type="number" [name]="'cm' + $index" [(ngModel)]="c.maxMarks" aria-label="Maximum marks" min="1" /></td><td><button type="button" class="btn small danger" (click)="plan.components.splice($index, 1)">Remove</button></td></tr>
                }
              </tbody></table>
              <div><button type="button" class="btn small" (click)="plan.components.push({ name: '', weight: 0, maxMarks: 100 })">Add component</button></div>
              <div class="form-actions"><button class="btn" type="button" (click)="planOpen.set(false)">Cancel</button><button class="btn primary" type="submit">Create plan</button></div>
            </form>
          }
          </app-modal>
        }
      }
      @case ('Report cards') {
        <div class="card table-wrap"><table>
          <thead><tr><th>Roll</th><th>{{ 'Name' | t }}</th><th>Subjects graded</th><th class="num">{{ 'Overall' | t }} %</th><th class="num">GPA</th><th class="num">{{ 'Attendance' | t }}</th><th></th></tr></thead>
          <tbody>@for (c of cards(); track c.enrollmentId) {
            <tr><td>{{ c.rollNumber }}</td><td>{{ c.student }}</td><td>@for (s of c.subjects; track s.subject) { <span class="badge" [title]="s.percent + '%'">{{ s.subject }}: {{ s.grade ?? '–' }}</span> }</td>
              <td class="num">{{ c.overallPercent ?? '–' }}</td><td class="num">{{ c.gpa ?? '–' }}</td><td class="num">{{ c.attendanceTotal ? c.attendanceAttended + '/' + c.attendanceTotal : '–' }}</td>
              <td style="text-align:end"><button class="btn small" (click)="pdf(c.enrollmentId)">PDF</button></td></tr>
          } @empty { <tr><td colspan="7" class="empty">No approved results for this section and term yet.</td></tr> }</tbody>
        </table></div>
        <p class="small muted">Report cards include approved and published results. Parents see published results only.</p>
      }
      @case ('Grading schemes') {
        <app-modal [open]="!!scheme()" [title]="(scheme()?.id ? 'Edit' : 'New') + ' grading scheme'" width="min(900px, 94vw)" (closed)="scheme.set(null)">
        @if (scheme(); as s) {
          <form class="stack" (ngSubmit)="saveScheme()">
            <div class="grid cols-3">
              <label class="field">Name<input type="text" name="name" [(ngModel)]="s.name" required /></label>
              <label class="field">Scale type<select name="st" [(ngModel)]="s.scaleType">@for (x of ['percentage', 'letter', 'gpa', 'ib', 'cambridge', 'cbse']; track x) { <option [value]="x">{{ x }}</option> }</select></label>
              <label class="field">Pass mark %<input type="number" name="pm" [(ngModel)]="s.definition.passMark" /></label>
              <label class="field">Rounding<select name="rd" [(ngModel)]="s.definition.rounding"><option value="half-up">Round half up</option><option value="floor">Round down</option><option value="none">No rounding</option></select></label>
              <label class="field">Decimal places<input type="number" name="dc" [(ngModel)]="s.definition.decimals" min="0" max="4" /></label>
              <label class="check" style="align-self:end"><input type="checkbox" name="az" [(ngModel)]="s.definition.absentCountsAsZero" /> Unexcused absence scores zero</label>
            </div>
            <table><thead><tr><th>From %</th><th>Grade</th><th>Grade points</th><th>Descriptor</th><th></th></tr></thead><tbody>
              @for (b of s.definition.bands; track $index) {
                <tr><td><input type="number" [name]="'bm' + $index" [(ngModel)]="b.min" aria-label="From percent" /></td><td><input type="text" [name]="'bg' + $index" [(ngModel)]="b.grade" aria-label="Grade" /></td>
                  <td><input type="number" [name]="'bp' + $index" [(ngModel)]="b.points" step="0.1" aria-label="Points" /></td><td><input type="text" [name]="'bd' + $index" [(ngModel)]="b.descriptor" aria-label="Descriptor" /></td>
                  <td><button type="button" class="btn small danger" (click)="s.definition.bands.splice($index, 1)">Remove</button></td></tr>
              }
            </tbody></table>
            <div><button type="button" class="btn small" (click)="s.definition.bands.push({ min: 0, grade: '', points: null, descriptor: '' })">Add band</button></div>
            <p class="small muted">If results already depend on this scheme, saving creates a new version and leaves existing results as they are.</p>
            <div class="form-actions"><button class="btn" type="button" (click)="scheme.set(null)">{{ 'Cancel' | t }}</button><button class="btn primary" type="submit">{{ 'Save' | t }}</button></div>
          </form>
        }
        </app-modal>
        <div class="card">
          <div class="page-head"><h2>Schemes for {{ ctx.year()?.name }}</h2>
            @if (canManage) {
              <div class="row"><label class="sr-only" for="preset">Preset</label>
                <select id="preset" style="width:auto" [(ngModel)]="preset">@for (p of presets(); track p.key) { <option [value]="p.key">{{ p.name }}</option> }</select>
                <button class="btn" (click)="addPreset()">Add from preset</button><button class="btn primary" (click)="newScheme()">New scheme</button></div>
            }
          </div>
          <div class="table-wrap"><table>
            <thead><tr><th>{{ 'Name' | t }}</th><th>Type</th><th>Version</th><th>Pass mark</th><th>Bands</th><th></th></tr></thead>
            <tbody>@for (s of schemes(); track s.id) {
              <tr><td>{{ s.name }}</td><td>{{ s.scaleType }}</td><td>v{{ s.version }}</td><td>{{ s.definition.passMark }}%</td>
                <td class="small">@for (b of s.definition.bands; track $index) { <span class="badge">{{ b.grade }} ≥ {{ b.min }}</span> }</td>
                <td style="text-align:end">@if (canManage) { <button class="btn small" (click)="editScheme(s)">{{ 'Edit' | t }}</button> }</td></tr>
            } @empty { <tr><td colspan="6" class="empty">No grading schemes yet. Add one from a preset.</td></tr> }</tbody>
          </table></div>
          <p class="small muted">Presets are starting points. Boards publish their own boundaries per subject and session, so review them with your exams officer.</p>
        </div>
      }
    }
  `,
})
export class ExamsPage {
  ctx = inject(Ctx);
  private auth = inject(Auth);
  private api = inject(Api);
  private toast = inject(Toast);
  statusClass = STATUS_CLASS;
  canManage = this.auth.can('exams.manage'); canApprove = this.auth.can('exams.approve'); canEnter = this.auth.can('exams.marks', 'exams.manage');
  tabs = ['Marks', ...(this.canManage || this.canApprove ? ['Report cards'] : []), 'Grading schemes'];
  tab = signal('Marks');
  sections = signal<any[]>([]); terms = signal<any[]>([]); plans = signal<any[]>([]); schemes = signal<any[]>([]); presets = signal<any[]>([]); classSubjects = signal<any[]>([]);
  marks = signal<any>(null); planOpen = signal(false); cards = signal<any[]>([]); scheme = signal<any>(null);
  sectionId: string | null = null; termId: string | null = null; lang = ''; preset = 'cbse';
  plan: any = this.blankPlan();

  constructor() { effect(() => { this.ctx.campusId(); this.ctx.yearId(); untracked(() => this.init()); }); }
  blankPlan() { return { classSubjectId: null, gradingSchemeId: null, name: '', marksDeadline: '', components: [{ name: 'Class test', weight: 30, maxMarks: 30 }, { name: 'Term exam', weight: 70, maxMarks: 100 }] }; }

  async init() {
    const yearId = this.ctx.yearId();
    if (!yearId) return;
    try {
      const [sections, terms, schemes, presets] = await Promise.all([loadSections(this.api, this.ctx), this.api.get<any[]>('/terms', { academicYearId: yearId }), this.api.get<any[]>('/exams/grading-schemes', { academicYearId: yearId }), this.api.get<any[]>('/exams/grading-schemes/presets')]);
      this.sections.set(sections); this.terms.set(terms.sort((a, b) => a.order - b.order)); this.schemes.set(schemes); this.presets.set(presets);
      this.sectionId = sections[0]?.id ?? null; this.termId = this.terms()[0]?.id ?? null; this.marks.set(null);
      await this.load();
    } catch (e) { this.toast.error(e); }
  }
  async load() {
    const yearId = this.ctx.yearId();
    if (!yearId || !this.sectionId || !this.termId) { this.plans.set([]); this.cards.set([]); return; }
    try {
      this.plans.set(await this.api.get('/exams/plans', { academicYearId: yearId, sectionId: this.sectionId, termId: this.termId }));
      if (this.canManage || this.canApprove) this.cards.set(await this.api.get('/exams/report-cards', { sectionId: this.sectionId, termId: this.termId }));
      if (this.canManage) {
        const [cs, subjects] = await Promise.all([this.api.get<any[]>('/class-subjects', { sectionId: this.sectionId }), this.api.get<any[]>('/subjects', { campusId: this.ctx.campusId() })]);
        const names = new Map(subjects.map((s) => [s.id, s.name]));
        this.classSubjects.set(cs.map((c) => ({ ...c, label: names.get(c.subjectId) })));
      }
    } catch (e) { this.toast.error(e); }
  }
  async openMarks(p: any) { try { this.marks.set(await this.api.get(`/exams/plans/${p.id}/marks`)); } catch (e) { this.toast.error(e); } }
  async saveMarks() {
    const m = this.marks();
    const scores = m.rows.flatMap((r: any) => r.scores.map((s: any) => ({ assessmentId: s.assessmentId, enrollmentId: r.enrollmentId, marks: s.marks === '' ? null : s.marks, isAbsent: s.isAbsent, isExcused: s.isExcused })));
    try { await this.api.put(`/exams/plans/${m.plan.id}/marks`, { scores }); this.toast.ok('Marks saved'); await this.openMarks(m.plan); return true; } catch (e) { this.toast.error(e); return false; }
  }
  async workflow(p: any, action: string) {
    if (action === 'submit' && !(await this.saveMarks())) return;
    if (action === 'publish' && !confirm('Publish these results to parents and students?')) return;
    try {
      await this.api.post(`/exams/plans/${p.id}/${action}`);
      this.toast.ok({ submit: 'Submitted for approval', approve: 'Approved', reopen: 'Reopened for marks entry', publish: 'Published; parents notified' }[action]!);
      await this.load();
      if (this.marks()) await this.openMarks(p);
    } catch (e) { this.toast.error(e); }
  }
  async createPlan() {
    try {
      await this.api.post('/exams/plans', { ...this.plan, termId: this.termId, marksDeadline: this.plan.marksDeadline ? new Date(this.plan.marksDeadline).toISOString() : null });
      this.plan = this.blankPlan(); this.planOpen.set(false); this.toast.ok('Plan created'); await this.load();
    } catch (e) { this.toast.error(e); }
  }
  async deletePlan(p: any) { if (!confirm('Delete this plan and its marks?')) return; try { await this.api.del(`/exams/plans/${p.id}`); await this.load(); } catch (e) { this.toast.error(e); } }
  pdf(enrollmentId?: string) { this.api.open('/exams/report-cards/pdf', { sectionId: this.sectionId, termId: this.termId, enrollmentId, lang: this.lang }).catch((e) => this.toast.error(e)); }

  newScheme() { this.scheme.set({ name: '', scaleType: 'percentage', definition: { bands: [{ min: 0, grade: '', points: null, descriptor: '' }], passMark: 40, decimals: 1, rounding: 'half-up', absentCountsAsZero: true } }); }
  editScheme(s: any) { this.scheme.set(JSON.parse(JSON.stringify(s))); }
  async addPreset() { try { await this.api.post('/exams/grading-schemes/from-preset', { campusId: this.ctx.campusId(), academicYearId: this.ctx.yearId(), preset: this.preset }); await this.init(); } catch (e) { this.toast.error(e); } }
  async saveScheme() {
    const s = this.scheme();
    const body = { campusId: this.ctx.campusId(), academicYearId: this.ctx.yearId(), name: s.name, scaleType: s.scaleType, definition: { ...s.definition, bands: s.definition.bands.map((b: any) => ({ ...b, points: b.points === '' ? null : b.points })) } };
    try {
      const r = s.id ? await this.api.put(`/exams/grading-schemes/${s.id}`, body) : await this.api.post('/exams/grading-schemes', body);
      this.toast.ok(r?.newVersion ? `Saved as version ${r.version}. Existing results keep the old version.` : 'Scheme saved');
      this.scheme.set(null); await this.init();
    } catch (e) { this.toast.error(e); }
  }
}
