import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, Auth, Ctx, DayPipe, TPipe, Toast } from '../core';
import { Modal } from '../modal';

const STAGES = ['enquiry', 'applied', 'assessed', 'offered', 'waitlisted', 'rejected', 'enrolled'];

/** Sections of the selected year with their grade name, used by several screens. */
export async function loadSections(api: Api, ctx: Ctx): Promise<any[]> {
  if (!ctx.campusId() || !ctx.yearId()) return [];
  const [grades, sections] = await Promise.all([api.get<any[]>('/grade-levels', { campusId: ctx.campusId() }), api.get<any[]>('/sections', { academicYearId: ctx.yearId() })]);
  const byId = new Map(grades.map((g) => [g.id, g]));
  return sections.map((s) => ({ ...s, label: `${byId.get(s.gradeLevelId)?.name ?? ''} ${s.name}`, order: byId.get(s.gradeLevelId)?.order ?? 0 }))
    .sort((a, b) => a.order - b.order || a.name.localeCompare(b.name));
}

@Component({
  imports: [FormsModule, RouterLink, TPipe, DayPipe, Modal],
  template: `
    <div class="page-head">
      <div><h1>{{ 'Students' | t }}</h1><p class="muted">{{ rows().length }} shown</p></div>
      @if (auth.can('students.write')) { <button class="btn primary" (click)="startAdd()">Add student</button> }
    </div>
    <app-modal [open]="adding()" title="Add student" (closed)="adding.set(false)">
    @if (adding()) {
      <form (ngSubmit)="create()"><div class="grid cols-2">
        <label class="field">First name<input type="text" name="fn" [(ngModel)]="form.firstName" required /></label>
        <label class="field">Last name<input type="text" name="ln" [(ngModel)]="form.lastName" /></label>
        <label class="field">Name in native script<input type="text" name="nn" [(ngModel)]="form.nativeName" /></label>
        <label class="field">Date of birth<input type="date" name="dob" [(ngModel)]="form.dateOfBirth" /></label>
        <label class="field">Gender<select name="g" [(ngModel)]="form.gender"><option [ngValue]="null">–</option><option value="F">Female</option><option value="M">Male</option><option value="X">Other</option></select></label>
        <label class="field">Section<select name="sec" [(ngModel)]="form.sectionId"><option [ngValue]="null">Not enrolled yet</option>@for (s of sections(); track s.id) { <option [ngValue]="s.id">{{ s.label }}</option> }</select></label>
        <label class="field">Guardian name<input type="text" name="gn" [(ngModel)]="form.guardianName" /></label>
        <label class="field">Guardian email<input type="email" name="ge" [(ngModel)]="form.guardianEmail" /></label>
        <label class="field">Guardian phone<input type="tel" name="gp" [(ngModel)]="form.guardianPhone" /></label>
        </div>
        <div class="form-actions"><button class="btn" type="button" (click)="adding.set(false)">Cancel</button><button class="btn primary" type="submit">Save student</button></div>
      </form>
    }
    </app-modal>
    <div class="card">
      <div class="row" style="margin-bottom:12px">
        <label class="sr-only" for="q">{{ 'Search' | t }}</label>
        <input id="q" type="search" style="max-width:280px" placeholder="Name or admission number" [(ngModel)]="search" (ngModelChange)="load()" />
        <label class="sr-only" for="sec">Section</label>
        <select id="sec" style="width:auto" [(ngModel)]="sectionId" (ngModelChange)="load()"><option [ngValue]="null">All sections</option>@for (s of sections(); track s.id) { <option [ngValue]="s.id">{{ s.label }}</option> }</select>
        <label class="sr-only" for="st">Status</label>
        <select id="st" style="width:auto" [(ngModel)]="status" (ngModelChange)="load()"><option value="">Any status</option>@for (s of ['active', 'withdrawn', 'transferred', 'graduated']; track s) { <option [value]="s">{{ s }}</option> }</select>
      </div>
      <div class="table-wrap"><table>
        <thead><tr><th>Admission no.</th><th>Name</th><th>Class</th><th>Roll</th><th>Date of birth</th><th>Status</th></tr></thead>
        <tbody>
          @for (s of rows(); track s.id) {
            <tr class="clickable" [routerLink]="['/students', s.id]">
              <td>{{ s.admissionNumber }}</td><td><a [routerLink]="['/students', s.id]">{{ s.firstName }} {{ s.lastName }}</a> @if (s.nativeName) { <span class="muted" dir="auto">· {{ s.nativeName }}</span> }</td>
              <td>{{ s.section ?? '–' }}</td><td>{{ s.rollNumber ?? '–' }}</td><td>{{ s.dateOfBirth | day }}</td><td><span class="badge" [class.ok]="s.status === 'active'">{{ s.status }}</span></td>
            </tr>
          } @empty { <tr><td colspan="6" class="empty">No students match.</td></tr> }
        </tbody>
      </table></div>
    </div>
  `,
})
export class StudentsPage {
  auth = inject(Auth);
  private ctx = inject(Ctx);
  private api = inject(Api);
  private toast = inject(Toast);
  private router = inject(Router);
  rows = signal<any[]>([]); sections = signal<any[]>([]); adding = signal(false);
  search = ''; sectionId: string | null = null; status = '';
  form: any = { gender: null, sectionId: null };
  startAdd() { this.form = { gender: null, sectionId: null }; this.adding.set(true); }

  constructor() {
    effect(() => { this.ctx.campusId(); this.ctx.yearId(); untracked(async () => { this.sectionId = null; this.sections.set(await loadSections(this.api, this.ctx)); this.load(); }); });
  }
  async load() {
    if (!this.ctx.campusId()) return;
    try { this.rows.set(await this.api.get('/students', { campusId: this.ctx.campusId(), sectionId: this.sectionId, search: this.search, status: this.status })); } catch (e) { this.toast.error(e); }
  }
  async create() {
    try {
      const r = await this.api.post('/students', { ...this.form, campusId: this.ctx.campusId(), dateOfBirth: this.form.dateOfBirth || null });
      this.toast.ok(`Created ${r.admissionNumber}`);
      this.router.navigate(['/students', r.id]);
    } catch (e) { this.toast.error(e); }
  }
}

@Component({
  imports: [FormsModule, RouterLink, DayPipe, Modal],
  template: `
    @if (d(); as d) {
      <div class="page-head">
        <div>
          <a routerLink="/students" class="small">← Students</a>
          <h1>{{ d.student.firstName }} {{ d.student.lastName }} @if (d.student.nativeName) { <span class="muted" dir="auto">· {{ d.student.nativeName }}</span> }</h1>
          <p class="muted">{{ d.student.admissionNumber }} · <span class="badge" [class.ok]="d.student.status === 'active'">{{ d.student.status }}</span></p>
        </div>
        <div class="row">
          @if (auth.can('students.write') && d.student.status === 'active') { <button class="btn danger" (click)="panel.set('withdraw')">Withdraw / transfer out</button> }
          @if (auth.can('students.write') && (d.student.status === 'withdrawn' || d.student.status === 'transferred')) { <button class="btn" (click)="tc()">Transfer certificate (PDF)</button> }
        </div>
      </div>

      <app-modal [open]="panel() === 'withdraw'" title="Withdraw or transfer out" (closed)="panel.set('')">
      @if (panel() === 'withdraw') {
        <form (ngSubmit)="withdraw()"><div class="grid cols-2">
          <label class="field">Reason<input type="text" name="reason" [(ngModel)]="wd.reason" required /></label>
          <label class="field">Leaving date<input type="date" name="date" [(ngModel)]="wd.date" /></label>
          <label class="check" style="align-self:end"><input type="checkbox" name="tr" [(ngModel)]="wd.transfer" /> Transferring to another school</label>
          </div>
          <p class="small muted" style="margin-top:12px">The student's enrollment ends on this date. You can then issue a transfer certificate.</p>
          <div class="form-actions"><button class="btn" type="button" (click)="panel.set('')">Cancel</button><button class="btn danger" type="submit">Confirm withdrawal</button></div>
        </form>
      }
      </app-modal>

      <div class="grid cols-2">
        <form class="card" (ngSubmit)="save()">
          <h2>Profile</h2>
          <div class="grid cols-2">
            <label class="field">First name<input type="text" name="fn" [(ngModel)]="form.firstName" [disabled]="!canWrite" required /></label>
            <label class="field">Last name<input type="text" name="ln" [(ngModel)]="form.lastName" [disabled]="!canWrite" /></label>
            <label class="field">Native-script name<input type="text" name="nn" dir="auto" [(ngModel)]="form.nativeName" [disabled]="!canWrite" /></label>
            <label class="field">Date of birth<input type="date" name="dob" [(ngModel)]="form.dateOfBirth" [disabled]="!canWrite" /></label>
            <label class="field">Gender<select name="g" [(ngModel)]="form.gender" [disabled]="!canWrite"><option [ngValue]="null">–</option><option value="F">Female</option><option value="M">Male</option><option value="X">Other</option></select></label>
            <label class="field">Fee category<input type="text" name="cat" [(ngModel)]="form.category" [disabled]="!canWrite" /></label>
            @for (f of customFields(); track f.key) {
              <label class="field">{{ f.label }}<input [type]="f.fieldType === 'checkbox' ? 'text' : f.fieldType" [name]="'cf_' + f.key" [(ngModel)]="custom[f.key]" [disabled]="!canWrite" /></label>
            }
          </div>
          <h3 style="margin-top:16px">Medical and identity <span class="badge warn">restricted</span></h3>
          @if (d.student.canSeeMedical) {
            <div class="stack">
              <label class="field">National ID<input type="text" name="nid" [(ngModel)]="form.nationalId" [disabled]="!canWrite" /></label>
              <label class="field">Medical notes<textarea name="med" [(ngModel)]="form.medicalNotes" [disabled]="!canWrite"></textarea></label>
              <label class="field">Special educational needs<textarea name="sen" [(ngModel)]="form.specialNeeds" [disabled]="!canWrite"></textarea></label>
            </div>
          } @else { <p class="muted small">Your role cannot view medical, special-needs or ID data.</p> }
          @if (canWrite) { <div class="row" style="margin-top:12px"><button class="btn primary" type="submit">Save profile</button></div> }
        </form>

        <div>
          <div class="card">
            <h2>Guardians</h2>
            <table><tbody>@for (g of d.guardians; track g.id) {
              <tr><td>{{ g.firstName }} {{ g.lastName }} @if (g.isPrimary) { <span class="badge info">primary</span> }<div class="small muted">{{ g.relationship }}</div></td><td class="small">{{ g.email }}<br />{{ g.phone }}</td></tr>
            } @empty { <tr><td class="empty">No guardian linked.</td></tr> }</tbody></table>
            @if (canWrite) { <button class="btn small" style="margin-top:12px" (click)="panel.set('guardian')">Add guardian</button> }
            <app-modal [open]="panel() === 'guardian'" title="Add guardian" (closed)="panel.set('')">
            @if (panel() === 'guardian') {
              <form (ngSubmit)="addGuardian()"><div class="grid cols-2">
                <label class="field">Name<input type="text" name="n" [(ngModel)]="g.name" required /></label>
                <label class="field">Relationship<input type="text" name="r" [(ngModel)]="g.relationship" /></label>
                <label class="field">Email<input type="email" name="e" [(ngModel)]="g.email" /></label>
                <label class="field">Phone<input type="tel" name="p" [(ngModel)]="g.phone" /></label>
                <label class="check"><input type="checkbox" name="pr" [(ngModel)]="g.isPrimary" /> Primary (billed for fees)</label>
                </div>
                <p class="small muted" style="margin-top:12px">A guardian with an email address gets a parent portal login (one-time code sign-in).</p>
                <div class="form-actions"><button class="btn" type="button" (click)="panel.set('')">Cancel</button><button class="btn primary" type="submit">Add guardian</button></div>
              </form>
            }
            </app-modal>
            @if (d.siblings.length) { <h3 style="margin-top:14px">Siblings</h3>@for (s of d.siblings; track s.id) { <a class="btn small" [routerLink]="['/students', s.id]">{{ s.firstName }} {{ s.lastName }}</a> } }
          </div>

          <div class="card">
            <h2>Enrollment history</h2>
            <table><thead><tr><th>Year</th><th>Class</th><th>Roll</th><th>From</th><th>To</th><th>Status</th></tr></thead>
              <tbody>@for (e of d.enrollments; track e.id) { <tr><td>{{ e.year }}</td><td>{{ e.section }}</td><td>{{ e.rollNumber }}</td><td>{{ e.startDate | day }}</td><td>{{ e.endDate | day }}</td><td><span class="badge" [class.ok]="e.status === 'active'">{{ e.status }}</span></td></tr> }
              @empty { <tr><td colspan="6" class="empty">Not enrolled.</td></tr> }</tbody></table>
            @if (canWrite && d.student.status === 'active') {
              <div class="row" style="margin-top:12px">
                <label class="sr-only" for="mv">Move to section</label>
                <select id="mv" style="width:auto" [(ngModel)]="moveTo"><option [ngValue]="null">Move to section…</option>@for (s of sections(); track s.id) { <option [ngValue]="s.id">{{ s.label }}</option> }</select>
                <button class="btn small" [disabled]="!moveTo" (click)="move()">Move</button>
              </div>
            }
          </div>

          <div class="card">
            <h2>Documents</h2>
            @for (doc of d.documents; track doc.id) { <div class="row"><button class="btn link" (click)="openDoc(doc)">{{ doc.fileName }}</button><span class="muted small">{{ doc.kind }} · {{ doc.createdAt | day }}</span></div> }
            @empty { <p class="muted small">No documents.</p> }
            @if (canWrite) { <label class="field" style="margin-top:8px">Upload (PDF, image or Word, up to 10 MB)<input type="file" (change)="upload($event)" /></label> }
          </div>

          @if (auth.can('privacy.manage')) {
            <div class="card">
              <h2>Privacy</h2>
              <p class="small muted">Export everything held about this student, or erase their personal data after they have left. Both actions are recorded in the audit log.</p>
              <div class="row">
                <button class="btn" (click)="exportData()">Export data (JSON)</button>
                <input type="text" style="max-width:190px" placeholder="Type admission no. to erase" [(ngModel)]="confirmErase" aria-label="Admission number to confirm erasure" />
                <button class="btn danger" [disabled]="confirmErase !== d.student.admissionNumber" (click)="erase()">Erase personal data</button>
              </div>
            </div>
          }
        </div>
      </div>
    }
  `,
})
export class StudentDetailPage {
  auth = inject(Auth);
  private ctx = inject(Ctx);
  private api = inject(Api);
  private toast = inject(Toast);
  private id = inject(ActivatedRoute).snapshot.paramMap.get('id')!;
  d = signal<any>(null); sections = signal<any[]>([]); customFields = signal<any[]>([]); panel = signal('');
  form: any = {}; custom: Record<string, any> = {}; g: any = { relationship: 'parent', isPrimary: false }; wd: any = { reason: '', date: '', transfer: false };
  moveTo: string | null = null; confirmErase = '';
  canWrite = this.auth.can('students.write');

  constructor() {
    inject(ActivatedRoute).paramMap.subscribe((p) => { this.id = p.get('id')!; this.load(); });
  }
  async load() {
    try {
      const d = await this.api.get(`/students/${this.id}`);
      this.form = { ...d.student };
      try { this.custom = JSON.parse(d.student.customFieldsJson || '{}'); } catch { this.custom = {}; }
      this.d.set(d);
      this.sections.set(await loadSections(this.api, this.ctx));
      this.customFields.set((await this.api.get<any[]>('/custom-fields')).filter((f) => f.entityType === 'student'));
    } catch (e) { this.toast.error(e); }
  }
  private async act(fn: () => Promise<any>, ok: string) { try { await fn(); this.toast.ok(ok); await this.load(); } catch (e) { this.toast.error(e); } }
  save() { return this.act(() => this.api.put(`/students/${this.id}`, { ...this.form, dateOfBirth: this.form.dateOfBirth || null, customFieldsJson: JSON.stringify(this.custom) }), 'Profile saved'); }
  addGuardian() { return this.act(async () => { await this.api.post(`/students/${this.id}/guardians`, this.g); this.g = { relationship: 'parent', isPrimary: false }; this.panel.set(''); }, 'Guardian added'); }
  move() { return this.act(() => this.api.post(`/students/${this.id}/transfer-section`, { sectionId: this.moveTo }), 'Moved'); }
  withdraw() { return this.act(async () => { await this.api.post(`/students/${this.id}/withdraw`, { ...this.wd, date: this.wd.date || null }); this.panel.set(''); }, 'Recorded'); }
  tc() { this.api.open(`/students/${this.id}/transfer-certificate`).catch((e) => this.toast.error(e)); }
  openDoc(doc: any) { this.api.open(`/students/documents/${doc.id}`).catch((e) => this.toast.error(e)); }
  upload(e: Event) {
    const file = (e.target as HTMLInputElement).files?.[0];
    if (!file) return;
    const form = new FormData(); form.append('file', file);
    return this.act(() => this.api.upload(`/students/${this.id}/documents`, form), 'Uploaded');
  }
  exportData() { this.api.open(`/privacy/students/${this.id}/export`, undefined, `student-${this.d().student.admissionNumber}.json`).catch((e) => this.toast.error(e)); }
  erase() {
    if (!confirm('Erase this student\'s personal data? This cannot be undone.')) return;
    return this.act(() => this.api.post(`/privacy/students/${this.id}/erase`, { confirmAdmissionNumber: this.confirmErase }), 'Personal data erased');
  }
}

@Component({
  imports: [FormsModule, DayPipe, TPipe, Modal],
  template: `
    <div class="page-head">
      <div><h1>{{ 'Admissions' | t }}</h1><p class="muted">Public form: <a [href]="publicUrl()" target="_blank" rel="noopener">{{ publicUrl() }}</a></p></div>
      <div class="row"><button class="btn" (click)="tab.set(tab() === 'form' ? 'board' : 'form')">{{ tab() === 'form' ? 'Back to pipeline' : 'Application form settings' }}</button><button class="btn primary" (click)="walkIn.set(true)">New walk-in</button></div>
    </div>

    <app-modal [open]="walkIn()" title="New walk-in enquiry or application" (closed)="walkIn.set(false)">
    @if (walkIn()) {
      <form (ngSubmit)="createWalkIn()"><div class="grid cols-2">
        <label class="field">Type<select name="k" [(ngModel)]="w.kind"><option value="enquiry">Enquiry</option><option value="application">Application</option></select></label>
        <label class="field">Child's first name<input type="text" name="fn" [(ngModel)]="w.firstName" required /></label>
        <label class="field">Last name<input type="text" name="ln" [(ngModel)]="w.lastName" /></label>
        <label class="field">Date of birth<input type="date" name="dob" [(ngModel)]="w.dateOfBirth" /></label>
        <label class="field">Applying for<select name="gr" [(ngModel)]="w.gradeLevelId"><option [ngValue]="null">–</option>@for (g of grades(); track g.id) { <option [ngValue]="g.id">{{ g.name }}</option> }</select></label>
        <label class="field">Guardian name<input type="text" name="gn" [(ngModel)]="w.guardianName" required /></label>
        <label class="field">Guardian email<input type="email" name="ge" [(ngModel)]="w.guardianEmail" /></label>
        <label class="field">Guardian phone<input type="tel" name="gp" [(ngModel)]="w.guardianPhone" /></label>
        </div>
        <div class="form-actions"><button class="btn" type="button" (click)="walkIn.set(false)">Cancel</button><button class="btn primary" type="submit">Save</button></div>
      </form>
    }
    </app-modal>

    @if (tab() === 'form') {
      <div class="card stack">
        <h2>Online application form for {{ ctx.campus()?.name }}</h2>
        <label class="field">Introduction shown at the top<textarea [(ngModel)]="formCfg.intro"></textarea></label>
        <p class="small muted">The form always asks for the child's name, date of birth, grade and guardian contact. Add your own questions below.</p>
        <table><thead><tr><th>Key</th><th>Question</th><th>Type</th><th>Required</th><th></th></tr></thead><tbody>
          @for (f of fields; track $index) {
            <tr><td><input type="text" [(ngModel)]="f.key" aria-label="Key" /></td><td><input type="text" [(ngModel)]="f.label" aria-label="Question" /></td>
              <td><select [(ngModel)]="f.type" aria-label="Type"><option value="text">Text</option><option value="number">Number</option><option value="date">Date</option><option value="checkbox">Yes / no</option></select></td>
              <td><input type="checkbox" [(ngModel)]="f.required" aria-label="Required" /></td><td><button class="btn small danger" (click)="fields.splice($index, 1)">Remove</button></td></tr>
          }
        </tbody></table>
        <div class="row"><button class="btn" (click)="fields.push({ key: '', label: '', type: 'text', required: false })">Add question</button><button class="btn primary" (click)="saveForm()">Save form</button></div>
      </div>
    } @else {
      <div class="board">
        @for (stage of stages; track stage) {
          <section class="lane" [attr.aria-label]="stage">
            <h3>{{ stage }} <span class="badge">{{ lane(stage).length }}</span></h3>
            @for (a of lane(stage); track a.id) {
              <button class="ticket" (click)="open(a)"><strong>{{ a.firstName }} {{ a.lastName }}</strong><div class="small muted">{{ a.grade ?? 'No grade' }} · {{ a.source }}</div><div class="small muted">{{ a.createdAt | day }}</div></button>
            }
          </section>
        }
      </div>
    }

    @if (sel(); as s) {
      <div class="drawer-backdrop" (click)="sel.set(null)"></div>
      <aside class="drawer" aria-label="Application">
        <div class="page-head"><h2>{{ s.application.firstName }} {{ s.application.lastName }}</h2><button class="btn small" (click)="sel.set(null)">{{ 'Close' | t }}</button></div>
        <dl class="kv">
          <dt>Stage</dt><dd><span class="badge info">{{ s.application.stage }}</span></dd>
          <dt>Date of birth</dt><dd>{{ s.application.dateOfBirth | day }}</dd>
          <dt>Guardian</dt><dd>{{ s.application.guardianName }}<br /><span class="small muted">{{ s.application.guardianEmail }} {{ s.application.guardianPhone }}</span></dd>
          @for (kv of formData(s.application); track kv[0]) { <dt>{{ kv[0] }}</dt><dd>{{ kv[1] }}</dd> }
          <dt>Documents</dt><dd>@for (doc of s.documents; track doc.id) { <button class="btn link" (click)="openDoc(doc)">{{ doc.fileName }}</button><br /> } @empty { – }</dd>
          <dt>Tests and interviews</dt><dd>@for (e of s.events; track e.id) { <div>{{ e.kind }} · {{ e.scheduledAt | day: true }} · {{ e.location }}</div> } @empty { – }</dd>
        </dl>
        @if (s.application.stage !== 'enrolled') {
          <h3 style="margin-top:16px">Move to</h3>
          <div class="row">@for (st of stages; track st) { @if (st !== 'enrolled' && st !== s.application.stage) { <button class="btn small" (click)="move(st)">{{ st }}</button> } }</div>
          <h3 style="margin-top:16px">Schedule a test or interview</h3>
          <form class="grid cols-3" (ngSubmit)="schedule()">
            <label class="field">Type<select name="kind" [(ngModel)]="ev.kind"><option value="interview">Interview</option><option value="entrance test">Entrance test</option></select></label>
            <label class="field">When<input type="datetime-local" name="when" [(ngModel)]="ev.scheduledAt" required /></label>
            <label class="field">Where<input type="text" name="loc" [(ngModel)]="ev.location" /></label>
            <div><button class="btn small" type="submit">Schedule and notify</button></div>
          </form>
          <h3 style="margin-top:16px">Enrol</h3>
          <p class="small muted">Creates the student record, guardian portal login, fee account and class enrollment in one step.</p>
          <div class="row">
            <label class="sr-only" for="ensec">Section</label>
            <select id="ensec" style="width:auto" [(ngModel)]="enrollSection"><option [ngValue]="null">Choose section…</option>@for (x of sections(); track x.id) { <option [ngValue]="x.id">{{ x.label }}</option> }</select>
            <button class="btn primary" [disabled]="!enrollSection" (click)="enroll()">Enrol student</button>
          </div>
        } @else { <div class="alert ok" style="margin-top:16px">Enrolled. The student record has been created.</div> }
      </aside>
    }
  `,
})
export class AdmissionsPage {
  ctx = inject(Ctx);
  private api = inject(Api);
  private toast = inject(Toast);
  stages = STAGES;
  tab = signal<'board' | 'form'>('board'); walkIn = signal(false); apps = signal<any[]>([]); sel = signal<any>(null);
  grades = signal<any[]>([]); sections = signal<any[]>([]); slug = signal('');
  byStage = computed(() => { const m: Record<string, any[]> = {}; for (const a of this.apps()) (m[a.stage] ??= []).push(a); return m; });
  lane(stage: string): any[] { return this.byStage()[stage] ?? []; }
  publicUrl = computed(() => `${location.origin}/apply/${this.slug()}`);
  w: any = { kind: 'enquiry', gradeLevelId: null }; ev: any = { kind: 'interview' }; enrollSection: string | null = null;
  formCfg: any = {}; fields: any[] = [];

  constructor() {
    this.api.get('/admin/tenant').then((t) => this.slug.set(t.slug)).catch(() => {});
    effect(() => { this.ctx.campusId(); this.ctx.yearId(); untracked(() => this.load()); });
  }
  async load() {
    const campusId = this.ctx.campusId();
    if (!campusId) return;
    try {
      this.apps.set(await this.api.get('/admissions/applications', { campusId }));
      this.grades.set((await this.api.get<any[]>('/grade-levels', { campusId })).sort((a, b) => a.order - b.order));
      this.sections.set(await loadSections(this.api, this.ctx));
      this.formCfg = await this.api.get('/admissions/form', { campusId });
      try { this.fields = JSON.parse(this.formCfg.fieldsJson || '[]'); } catch { this.fields = []; }
    } catch (e) { this.toast.error(e); }
  }
  formData(a: any): [string, string][] { try { return Object.entries(JSON.parse(a.formDataJson || '{}')).map(([k, v]) => [k, String(v)]); } catch { return []; } }
  async open(a: any) { try { this.sel.set(await this.api.get(`/admissions/applications/${a.id}`)); this.enrollSection = null; } catch (e) { this.toast.error(e); } }
  private async act(fn: () => Promise<any>, ok: string) {
    try { await fn(); this.toast.ok(ok); await this.load(); const id = this.sel()?.application.id; if (id) this.sel.set(await this.api.get(`/admissions/applications/${id}`)); } catch (e) { this.toast.error(e); }
  }
  move(stage: string) { return this.act(() => this.api.post(`/admissions/applications/${this.sel().application.id}/stage`, { stage }), `Moved to ${stage}`); }
  schedule() { return this.act(() => this.api.post(`/admissions/applications/${this.sel().application.id}/events`, { ...this.ev, scheduledAt: new Date(this.ev.scheduledAt).toISOString() }), 'Scheduled; applicant notified'); }
  enroll() { return this.act(() => this.api.post(`/admissions/applications/${this.sel().application.id}/enroll`, { sectionId: this.enrollSection }), 'Student enrolled'); }
  openDoc(doc: any) { this.api.open(`/admissions/documents/${doc.id}`).catch((e) => this.toast.error(e)); }
  createWalkIn() { return this.act(async () => { await this.api.post('/admissions/applications', { ...this.w, campusId: this.ctx.campusId(), dateOfBirth: this.w.dateOfBirth || null }); this.walkIn.set(false); this.w = { kind: 'enquiry', gradeLevelId: null }; }, 'Saved'); }
  saveForm() { return this.act(() => this.api.put('/admissions/form', { campusId: this.ctx.campusId(), fieldsJson: JSON.stringify(this.fields.filter((f) => f.key && f.label)), intro: this.formCfg.intro }), 'Form saved'); }
}

@Component({
  imports: [FormsModule],
  template: `
    <div class="center-page wide">
      <div class="card">
        @if (done()) {
          <h1>Thank you</h1>
          <div class="alert ok">We have received your {{ form.kind }}. Your reference is <strong>{{ done() }}</strong>. The admissions office will contact you.</div>
        } @else if (school(); as s) {
          <h1>{{ s.school }}</h1>
          <p class="muted">Admissions</p>
          @if (error()) { <div class="alert danger" role="alert">{{ error() }}</div> }
          <form class="stack" (ngSubmit)="submit()">
            <div class="grid cols-2">
              <label class="field">Campus<select name="campus" [(ngModel)]="form.campusId" (ngModelChange)="pickCampus()" required>@for (c of s.campuses; track c.id) { <option [ngValue]="c.id">{{ c.name }}</option> }</select></label>
              <label class="field">I would like to<select name="kind" [(ngModel)]="form.kind"><option value="application">Apply for a place</option><option value="enquiry">Ask a question first</option></select></label>
            </div>
            @if (campus()?.intro) { <p>{{ campus().intro }}</p> }
            <div class="grid cols-2">
              <label class="field">Child's first name<input type="text" name="fn" [(ngModel)]="form.firstName" required autocomplete="off" /></label>
              <label class="field">Child's last name<input type="text" name="ln" [(ngModel)]="form.lastName" required autocomplete="off" /></label>
              <label class="field">Date of birth<input type="date" name="dob" [(ngModel)]="form.dateOfBirth" /></label>
              <label class="field">Applying for<select name="grade" [(ngModel)]="form.gradeLevelId"><option [ngValue]="null">Not sure yet</option>@for (g of campus()?.grades ?? []; track g.id) { <option [ngValue]="g.id">{{ g.name }}</option> }</select></label>
              <label class="field">Your name<input type="text" name="gn" [(ngModel)]="form.guardianName" required autocomplete="name" /></label>
              <label class="field">Your email<input type="email" name="ge" [(ngModel)]="form.guardianEmail" autocomplete="email" /></label>
              <label class="field">Your phone<input type="tel" name="gp" [(ngModel)]="form.guardianPhone" autocomplete="tel" /></label>
              @if (form.kind === 'application') {
                @for (f of fields(); track f.key) {
                  @if (f.type === 'checkbox') { <label class="check" style="align-self:end"><input type="checkbox" [name]="f.key" [(ngModel)]="extra[f.key]" /> {{ f.label }}</label> }
                  @else { <label class="field">{{ f.label }}<input [type]="f.type" [name]="f.key" [(ngModel)]="extra[f.key]" [required]="f.required" /></label> }
                }
              }
            </div>
            @if (form.kind === 'application') { <label class="field">Supporting documents (birth certificate, last report; PDF or image, up to 5 files)<input type="file" multiple accept=".pdf,.png,.jpg,.jpeg" (change)="pick($event)" /></label> }
            <label class="field">Anything else we should know?<textarea name="notes" [(ngModel)]="form.notes"></textarea></label>
            <p class="small muted">We use these details only to process this admission request.</p>
            <button class="btn primary" type="submit" [disabled]="busy()">Send</button>
          </form>
        } @else { <p class="muted">{{ error() || 'Loading…' }}</p> }
      </div>
    </div>
  `,
})
export class ApplyPage {
  private slug = inject(ActivatedRoute).snapshot.paramMap.get('slug')!;
  school = signal<any>(null); campus = signal<any>(null); fields = signal<any[]>([]); done = signal(''); error = signal(''); busy = signal(false);
  form: any = { kind: 'application', gradeLevelId: null }; extra: Record<string, any> = {}; files: File[] = [];

  constructor() {
    fetch(`/api/public/${encodeURIComponent(this.slug)}`).then(async (r) => {
      if (!r.ok) { this.error.set('This school link is not valid.'); return; }
      const s = await r.json(); this.school.set(s); this.form.campusId = s.campuses[0]?.id; this.pickCampus();
    }).catch(() => this.error.set('Could not reach the school. Try again later.'));
  }
  pickCampus() {
    const c = this.school()?.campuses.find((x: any) => x.id === this.form.campusId);
    this.campus.set(c ?? null); this.form.gradeLevelId = null;
    try { this.fields.set(JSON.parse(c?.fieldsJson || '[]')); } catch { this.fields.set([]); }
  }
  pick(e: Event) { this.files = [...((e.target as HTMLInputElement).files ?? [])]; }
  async submit() {
    this.busy.set(true); this.error.set('');
    try {
      const fd = new FormData();
      const formData = Object.fromEntries(Object.entries(this.extra).filter(([, v]) => v !== '' && v !== null).map(([k, v]) => [k, String(v)]));
      fd.append('data', JSON.stringify({ ...this.form, dateOfBirth: this.form.dateOfBirth || null, formData }));
      for (const f of this.files) fd.append('files', f);
      const r = await fetch(`/api/public/${encodeURIComponent(this.slug)}/applications`, { method: 'POST', body: fd });
      const body = await r.json().catch(() => ({}));
      if (!r.ok) throw new Error(body.detail ?? 'Something went wrong. Please try again.');
      this.done.set(body.reference);
    } catch (e: any) { this.error.set(e.message); } finally { this.busy.set(false); }
  }
}
