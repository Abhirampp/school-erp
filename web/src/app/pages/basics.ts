import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, Auth, Ctx, DayPipe, MoneyPipe, TPipe, Toast } from '../core';
import { Bar, BarList, LineChart, Point, Segment, SplitBar } from '../charts';
import { Col, CrudTable } from '../crud-table';
import { Modal } from '../modal';

@Component({
  imports: [FormsModule, TPipe],
  template: `
    <div class="center-page">
      <div class="card">
        <h1>Global School ERP</h1>
        <p class="muted">{{ 'Sign in' | t }}</p>
        <div class="tabs" role="tablist">
          <button role="tab" [attr.aria-selected]="mode() === 'staff'" (click)="mode.set('staff')">{{ 'Staff' | t }}</button>
          <button role="tab" [attr.aria-selected]="mode() === 'parent'" (click)="mode.set('parent')">{{ 'Parent' | t }}</button>
        </div>
        @if (error()) { <div class="alert danger" role="alert">{{ error() }}</div> }
        @if (mode() === 'staff') {
          <form class="stack" (ngSubmit)="login()">
            <label class="field">{{ 'Email' | t }}<input type="email" name="email" [(ngModel)]="email" required autocomplete="username" /></label>
            <label class="field">{{ 'Password' | t }}<input type="password" name="password" [(ngModel)]="password" required autocomplete="current-password" /></label>
            @if (mfa()) { <label class="field">{{ 'Authenticator code' | t }}<input type="text" name="mfa" [(ngModel)]="mfaCode" inputmode="numeric" autocomplete="one-time-code" maxlength="6" /></label> }
            <button class="btn primary" type="submit" [disabled]="busy()">{{ 'Sign in' | t }}</button>
          </form>
        } @else {
          <form class="stack" (ngSubmit)="sent() ? verify() : requestCode()">
            <label class="field">{{ 'Email or phone' | t }}<input type="text" name="identifier" [(ngModel)]="identifier" required autocomplete="username" /></label>
            @if (sent()) {
              <label class="field">{{ 'One-time code' | t }}<input type="text" name="otp" [(ngModel)]="otp" inputmode="numeric" autocomplete="one-time-code" maxlength="6" /></label>
              @if (devCode()) { <div class="alert">Development mode: your code is <strong>{{ devCode() }}</strong>. In production it is sent by email or SMS.</div> }
            }
            <button class="btn primary" type="submit" [disabled]="busy()">{{ (sent() ? 'Verify' : 'Send code') | t }}</button>
          </form>
        }
      </div>
    </div>
  `,
})
export class LoginPage {
  private api = inject(Api);
  private auth = inject(Auth);
  private ctx = inject(Ctx);
  private router = inject(Router);
  mode = signal<'staff' | 'parent'>('staff');
  email = ''; password = ''; mfaCode = ''; identifier = ''; otp = '';
  mfa = signal(false); sent = signal(false); devCode = signal<string | null>(null); error = signal(''); busy = signal(false);

  private async run(fn: () => Promise<void>) {
    this.busy.set(true); this.error.set('');
    try { await fn(); } catch (e: any) { this.error.set(e.message); } finally { this.busy.set(false); }
  }
  private async enter(session: any) {
    this.auth.signIn(session);
    await this.ctx.load();
    this.router.navigateByUrl(this.auth.isPlatform() ? '/platform' : this.auth.isPortal() ? '/portal' : '/dashboard');
  }
  login() {
    return this.run(async () => {
      const r = await this.api.post('/auth/login', { email: this.email, password: this.password, mfaCode: this.mfaCode || null });
      if (r.mfaRequired) { this.mfa.set(true); return; }
      await this.enter(r);
    });
  }
  requestCode() {
    return this.run(async () => {
      const r = await this.api.post('/auth/otp/request', { identifier: this.identifier });
      this.sent.set(true); this.devCode.set(r.devCode ?? null);
    });
  }
  verify() { return this.run(async () => this.enter(await this.api.post('/auth/otp/verify', { identifier: this.identifier, code: this.otp }))); }
}

@Component({
  imports: [TPipe, MoneyPipe, RouterLink, LineChart, BarList, SplitBar],
  template: `
    <div class="page-head"><div><h1>{{ 'Welcome back' | t }}, {{ auth.user()?.displayName }}</h1><p class="muted">{{ ctx.campus()?.name }} · {{ d()?.year ?? 'No active year' }}</p></div></div>
    @if (d(); as d) {
      <div class="stats">
        <div class="stat"><div class="label">{{ 'Enrolled students' | t }}</div><div class="value">{{ d.students }}</div></div>
        <div class="stat"><div class="label">{{ 'Staff' | t }}</div><div class="value">{{ d.staff }}</div></div>
        <div class="stat"><div class="label">{{ 'Sections' | t }}</div><div class="value">{{ d.sections }}</div></div>
        @if (d.applicationsOpen !== null) { <div class="stat"><div class="label">{{ 'Open applications' | t }}</div><div class="value">{{ d.applicationsOpen }}</div></div> }
        <div class="stat"><div class="label">{{ 'Absent today' | t }}</div><div class="value">{{ d.absentToday }}<span class="muted small"> / {{ d.attendanceMarked }} marked</span></div></div>
        <div class="stat"><div class="label">{{ 'Late today' | t }}</div><div class="value">{{ d.lateToday }}</div></div>
      </div>
      <div class="grid cols-2">
        <div class="card">
          <h2>{{ 'Attendance' | t }} <span class="muted small">last {{ trend().length }} school days</span></h2>
          @if (trend().length) { <line-chart [points]="trend()" title="Attendance rate by day" unit="%" [floor]="80" [ceiling]="100" /> }
          @else { <p class="empty">No attendance has been marked yet.</p> }
        </div>
        @if (d.fees; as f) {
          <div class="card">
            <h2>{{ 'Fees' | t }} <span class="muted small">{{ f.invoiced | money: d.currency }} invoiced this year</span></h2>
            <split-bar [segments]="feeSplit()" headline="of invoiced fees collected" />
          </div>
        }
        <div class="card">
          <h2>Class sizes <span class="muted small">enrolled / capacity</span></h2>
          <bar-list [bars]="classes()" title="Students enrolled in each class against capacity" />
        </div>
        @if (d.pipeline) {
          <div class="card">
            <h2>{{ 'Admissions' | t }} <span class="muted small">applicants at each stage</span></h2>
            <bar-list [bars]="pipeline()" title="Applicants at each admissions stage" />
          </div>
        }
      </div>
      <div class="card">
        <h2>Quick links</h2>
        <div class="row">
          @if (auth.can('attendance.mark')) { <a class="btn" routerLink="/attendance">{{ 'Mark attendance' | t }}</a> }
          @if (auth.can('exams.marks', 'exams.manage')) { <a class="btn" routerLink="/exams">Enter marks</a> }
          @if (auth.can('admissions.manage')) { <a class="btn" routerLink="/admissions">Review applications</a> }
          @if (auth.can('fees.manage')) { <a class="btn" routerLink="/fees">Invoices and payments</a> }
          <a class="btn" routerLink="/security">Set up two-step sign-in</a>
        </div>
      </div>
    } @else { <p class="empty">Choose a campus to see its dashboard.</p> }
  `,
})
export class DashboardPage {
  auth = inject(Auth);
  ctx = inject(Ctx);
  private api = inject(Api);
  private toast = inject(Toast);
  d = signal<any>(null);
  private day = new DayPipe();
  private money = new MoneyPipe();
  trend = computed<Point[]>(() => (this.d()?.attendanceTrend ?? []).map((t: any) => ({ label: this.day.transform(t.date).replace(/,? \d{4}$/, ''), value: t.percent, detail: `${t.present} present, ${t.late} late, ${t.absent} absent` })));
  classes = computed<Bar[]>(() => (this.d()?.classSizes ?? []).map((c: any) => ({ label: c.label, value: c.count, max: Math.max(c.capacity, c.count), note: `${c.label}: ${c.count} of ${c.capacity} places filled` })));
  pipeline = computed<Bar[]>(() => (this.d()?.pipeline ?? []).map((p: any) => ({ label: p.stage, value: p.count })));
  feeSplit = computed<Segment[]>(() => {
    const d = this.d(), f = d?.fees;
    if (!f) return [];
    const fmt = (v: number) => this.money.transform(v, d.currency);
    return [
      { label: 'Collected', value: f.collected, kind: 'series', display: fmt(f.collected) },
      { label: 'Not yet due', value: f.outstanding - f.overdue, kind: 'track', display: fmt(f.outstanding - f.overdue) },
      { label: 'Overdue', value: f.overdue, kind: 'critical', display: fmt(f.overdue) },
    ];
  });
  constructor() {
    effect(() => {
      const campusId = this.ctx.campusId(); this.ctx.yearId();
      untracked(async () => {
        if (!campusId) return;
        try { this.d.set(await this.api.get('/dashboard', { campusId })); } catch (e) { this.toast.error(e); }
      });
    });
  }
}

const opt = (rows: any[], label: (r: any) => string) => rows.map((r) => ({ value: r.id, label: label(r) }));

@Component({
  imports: [CrudTable, FormsModule],
  template: `
    <div class="page-head"><div><h1>School setup</h1><p class="muted">Campuses, the academic calendar and class structure. Everything here is configuration: no code changes.</p></div></div>
    <div class="tabs" role="tablist">
      @for (t of tabs; track t) { <button role="tab" [attr.aria-selected]="tab() === t" (click)="tab.set(t)">{{ t }}</button> }
    </div>
    @switch (tab()) {
      @case ('Campuses') { <crud-table title="Campuses" path="/campuses" [columns]="campusCols" (changed)="ctx.load()" /> }
      @case ('Years & terms') {
        <crud-table title="Academic years" path="/academic-years" [columns]="yearCols" [scope]="{ campusId: ctx.campusId() }" (changed)="ctx.loadYears()" />
        <crud-table [title]="'Terms in ' + (ctx.year()?.name ?? '')" path="/terms" [columns]="termCols" [scope]="{ academicYearId: ctx.yearId() }" sortBy="order" />
      }
      @case ('Grades & sections') {
        <crud-table title="Grade levels" path="/grade-levels" [columns]="gradeCols" [scope]="{ campusId: ctx.campusId() }" sortBy="order" (changed)="loadLookups()" />
        <crud-table [title]="'Sections in ' + (ctx.year()?.name ?? '')" path="/sections" [columns]="sectionCols()" [scope]="{ academicYearId: ctx.yearId() }" />
      }
      @case ('Subjects') { <crud-table title="Subjects" path="/subjects" [columns]="subjectCols" [scope]="{ campusId: ctx.campusId() }" /> }
      @case ('Periods') { <crud-table title="Period structure" path="/periods" [columns]="periodCols" [scope]="{ campusId: ctx.campusId() }" sortBy="order" /> }
      @case ('Attendance codes') { <crud-table title="Attendance codes" path="/absence-codes" [columns]="codeCols" [scope]="{ campusId: ctx.campusId() }" /> }
      @case ('Staff') { <crud-table title="Staff" path="/staff" [columns]="staffCols" [scope]="{ campusId: ctx.campusId() }" (changed)="loadLookups()" /> }
      @case ('Custom fields') { <crud-table title="Custom fields" path="/custom-fields" [columns]="customCols" /> }
    }
  `,
})
export class SetupPage {
  ctx = inject(Ctx);
  private api = inject(Api);
  tabs = ['Campuses', 'Years & terms', 'Grades & sections', 'Subjects', 'Periods', 'Attendance codes', 'Staff', 'Custom fields'];
  tab = signal('Campuses');
  grades = signal<any[]>([]);
  staff = signal<any[]>([]);

  campusCols: Col[] = [
    { key: 'name', label: 'Name', required: true }, { key: 'country', label: 'Country code', default: 'IN' }, { key: 'currency', label: 'Currency', default: 'INR' },
    { key: 'timeZone', label: 'Time zone', default: 'Asia/Kolkata' }, { key: 'locale', label: 'Locale', default: 'en-IN' }, { key: 'curriculum', label: 'Curriculum', default: 'CBSE' },
    { key: 'attendanceMode', label: 'Attendance', type: 'select', required: true, options: [{ value: 'daily', label: 'Daily' }, { value: 'period', label: 'Per period' }] },
    { key: 'lateFinePercent', label: 'Late fine %', type: 'number' }, { key: 'lateFineFixed', label: 'Late fine (fixed)', type: 'number' }, { key: 'lateFineGraceDays', label: 'Grace days', type: 'number', default: 7 },
  ];
  yearCols: Col[] = [
    { key: 'name', label: 'Name', required: true }, { key: 'startDate', label: 'Starts', type: 'date', required: true }, { key: 'endDate', label: 'Ends', type: 'date', required: true },
    { key: 'status', label: 'Status', type: 'select', required: true, options: ['draft', 'active', 'archived'].map((s) => ({ value: s, label: s })) },
  ];
  termCols: Col[] = [{ key: 'name', label: 'Name', required: true }, { key: 'order', label: 'Order', type: 'number', default: 1 }, { key: 'startDate', label: 'Starts', type: 'date', required: true }, { key: 'endDate', label: 'Ends', type: 'date', required: true }];
  gradeCols: Col[] = [{ key: 'name', label: 'Name', required: true }, { key: 'order', label: 'Order (promotion sequence)', type: 'number', default: 1 }];
  sectionCols = computed<Col[]>(() => [
    { key: 'gradeLevelId', label: 'Grade', type: 'select', required: true, options: opt(this.grades(), (g) => g.name) }, { key: 'name', label: 'Section', required: true },
    { key: 'capacity', label: 'Capacity', type: 'number', default: 30 }, { key: 'classTeacherId', label: 'Class teacher', type: 'select', options: opt(this.staff(), (s) => `${s.firstName} ${s.lastName}`) },
  ]);
  subjectCols: Col[] = [{ key: 'code', label: 'Code', required: true }, { key: 'name', label: 'Name', required: true }, { key: 'credits', label: 'Credits', type: 'number', default: 1 }];
  periodCols: Col[] = [{ key: 'name', label: 'Name', required: true }, { key: 'order', label: 'Order', type: 'number' }, { key: 'startTime', label: 'Starts', type: 'time', required: true }, { key: 'endTime', label: 'Ends', type: 'time', required: true }, { key: 'isBreak', label: 'Break', type: 'checkbox' }];
  codeCols: Col[] = [
    { key: 'code', label: 'Code', required: true }, { key: 'name', label: 'Name', required: true },
    { key: 'category', label: 'Counts as', type: 'select', required: true, options: ['present', 'late', 'authorized', 'unauthorized'].map((s) => ({ value: s, label: s })) },
    { key: 'notifyParents', label: 'Alert parents', type: 'checkbox' },
  ];
  staffCols: Col[] = [{ key: 'employeeNumber', label: 'Employee no.', required: true }, { key: 'firstName', label: 'First name', required: true }, { key: 'lastName', label: 'Last name' }, { key: 'designation', label: 'Designation' },
    { key: 'department', label: 'Department' }, { key: 'qualification', label: 'Qualification' }, { key: 'joinedOn', label: 'Joined', type: 'date' }, { key: 'email', label: 'Email' }, { key: 'phone', label: 'Phone' },
    { key: 'status', label: 'Status', type: 'select', required: true, options: [{ value: 'active', label: 'Active' }, { value: 'left', label: 'Left' }] }];
  customCols: Col[] = [
    { key: 'entityType', label: 'Record', type: 'select', required: true, options: ['student', 'guardian', 'staff'].map((s) => ({ value: s, label: s })) },
    { key: 'key', label: 'Key', required: true }, { key: 'label', label: 'Label', required: true },
    { key: 'fieldType', label: 'Type', type: 'select', required: true, options: ['text', 'number', 'date', 'checkbox'].map((s) => ({ value: s, label: s })) }, { key: 'required', label: 'Required', type: 'checkbox' },
  ];

  constructor() { effect(() => { this.ctx.campusId(); untracked(() => this.loadLookups()); }); }
  async loadLookups() {
    const campusId = this.ctx.campusId();
    if (!campusId) return;
    this.grades.set((await this.api.get<any[]>('/grade-levels', { campusId })).sort((a, b) => a.order - b.order));
    this.staff.set(await this.api.get('/staff', { campusId }));
  }
}

@Component({
  imports: [FormsModule, DayPipe, Modal],
  template: `
    <div class="page-head"><div><h1>Users & roles</h1><p class="muted">A role is a set of permissions plus a scope. The built-in roles are templates you can copy and edit.</p></div></div>
    <div class="tabs" role="tablist">
      <button role="tab" [attr.aria-selected]="tab() === 'users'" (click)="tab.set('users')">Users</button>
      <button role="tab" [attr.aria-selected]="tab() === 'roles'" (click)="tab.set('roles')">Roles</button>
    </div>
    @if (tab() === 'users') {
      <div class="card">
        <div class="page-head"><h2>Users</h2><button class="btn primary small" (click)="startUser()">Add user</button></div>
        <app-modal [open]="!!editUser()" [title]="editUser()?.id ? 'Edit user' : 'Add user'" (closed)="editUser.set(null)">
        @if (editUser(); as u) {
          <form (ngSubmit)="saveUser()"><div class="grid cols-2">
            <label class="field">Email<input type="email" name="email" [(ngModel)]="u.email" required [disabled]="!!u.id" /></label>
            <label class="field">Name<input type="text" name="displayName" [(ngModel)]="u.displayName" required /></label>
            <label class="field">Role<select name="roleId" [(ngModel)]="u.roleId" required>@for (r of roles(); track r.id) { <option [ngValue]="r.id">{{ r.name }}</option> }</select></label>
            <label class="field">Campus (blank = all)<select name="campusId" [(ngModel)]="u.campusId"><option [ngValue]="null">All campuses</option>@for (c of ctx.campuses(); track c.id) { <option [ngValue]="c.id">{{ c.name }}</option> }</select></label>
            <label class="field">Linked staff record<select name="staffId" [(ngModel)]="u.staffId"><option [ngValue]="null">–</option>@for (s of staff(); track s.id) { <option [ngValue]="s.id">{{ s.firstName }} {{ s.lastName }}</option> }</select></label>
            <label class="field">{{ u.id ? 'New password (leave blank to keep)' : 'Password (10+ characters)' }}<input type="password" name="password" [(ngModel)]="u.password" autocomplete="new-password" /></label>
            <label class="check"><input type="checkbox" name="isActive" [(ngModel)]="u.isActive" /> Active</label>
            </div>
            <div class="form-actions"><button class="btn" type="button" (click)="editUser.set(null)">Cancel</button><button class="btn primary" type="submit">Save user</button></div>
          </form>
        }
        </app-modal>
        <div class="table-wrap"><table>
          <thead><tr><th>Name</th><th>Email</th><th>Role</th><th>Two-step</th><th>Last sign-in</th><th>Status</th><th></th></tr></thead>
          <tbody>@for (u of users(); track u.id) {
            <tr><td>{{ u.displayName }}</td><td>{{ u.email }}</td><td>{{ u.role }}</td><td>{{ u.mfaEnabled ? 'On' : 'Off' }}</td><td>{{ u.lastLoginAt | day: true }}</td>
              <td><span class="badge" [class.ok]="u.isActive">{{ u.isActive ? 'Active' : 'Disabled' }}</span></td>
              <td style="text-align:end"><button class="btn small" (click)="startUser(u)">Edit</button></td></tr>
          }</tbody>
        </table></div>
      </div>
    } @else {
      <div class="card">
        <div class="page-head"><h2>Roles</h2><button class="btn primary small" (click)="startRole()">Add role</button></div>
        <app-modal [open]="!!editRole()" [title]="editRole()?.id ? 'Edit role' : 'Add role'" width="min(900px, 94vw)" (closed)="editRole.set(null)">
        @if (editRole(); as r) {
          <form class="stack" (ngSubmit)="saveRole()">
            <div class="grid cols-2">
              <label class="field">Name<input type="text" name="name" [(ngModel)]="r.name" required /></label>
              <label class="field">Scope<select name="scope" [(ngModel)]="r.scope">@for (s of scopes(); track s) { <option [value]="s">{{ scopeLabel[s] }}</option> }</select></label>
            </div>
            <fieldset style="border:1px solid var(--border);border-radius:8px"><legend class="small muted">Permissions</legend>
              <div class="grid cols-3">@for (p of permissions(); track p) { <label class="check"><input type="checkbox" [checked]="r.set.has(p)" (change)="toggle(r, p)" /> {{ p }}</label> }</div>
            </fieldset>
            <div class="form-actions"><button class="btn" type="button" (click)="editRole.set(null)">Cancel</button><button class="btn primary" type="submit">Save role</button></div>
          </form>
        }
        </app-modal>
        <div class="table-wrap"><table>
          <thead><tr><th>Role</th><th>Scope</th><th>Permissions</th><th></th></tr></thead>
          <tbody>@for (r of roles(); track r.id) {
            <tr><td>{{ r.name }} @if (r.isTemplate) { <span class="badge">template</span> }</td><td>{{ scopeLabel[r.scope] }}</td><td class="small muted">{{ r.permissions.split(',').join(', ') }}</td>
              <td class="nowrap" style="text-align:end"><button class="btn small" (click)="startRole(r, true)">Copy</button> <button class="btn small" (click)="startRole(r)">Edit</button> <button class="btn small danger" (click)="deleteRole(r)">Delete</button></td></tr>
          }</tbody>
        </table></div>
      </div>
    }
  `,
})
export class UsersPage {
  ctx = inject(Ctx);
  private api = inject(Api);
  private toast = inject(Toast);
  tab = signal<'users' | 'roles'>('users');
  users = signal<any[]>([]); roles = signal<any[]>([]); staff = signal<any[]>([]); permissions = signal<string[]>([]); scopes = signal<string[]>([]);
  editUser = signal<any>(null); editRole = signal<any>(null);
  scopeLabel: Record<string, string> = { tenant: 'Whole school group', campus: 'Own campus', assigned: 'Assigned sections and subjects', children: 'Own children', self: 'Self' };

  constructor() { this.load(); }
  async load() {
    try {
      const [users, roles, meta, staff] = await Promise.all([this.api.get('/users'), this.api.get('/roles'), this.api.get('/admin/permissions'), this.api.get('/staff')]);
      this.users.set(users); this.roles.set(roles); this.permissions.set(meta.permissions); this.scopes.set(meta.scopes); this.staff.set(staff);
    } catch (e) { this.toast.error(e); }
  }
  startUser(u?: any) { this.editUser.set(u ? { ...u, password: '' } : { isActive: true, locale: 'en', roleId: this.roles()[0]?.id, campusId: null, staffId: null }); }
  async saveUser() {
    const u = this.editUser();
    try {
      if (u.id) await this.api.put(`/users/${u.id}`, u); else await this.api.post('/users', u);
      this.editUser.set(null); await this.load(); this.toast.ok('User saved');
    } catch (e) { this.toast.error(e); }
  }
  startRole(r?: any, copy = false) {
    this.editRole.set(r ? { id: copy ? null : r.id, name: copy ? r.name + ' (copy)' : r.name, scope: r.scope, set: new Set<string>(r.permissions.split(',').filter(Boolean)) } : { name: '', scope: 'campus', set: new Set<string>() });
  }
  toggle(r: any, p: string) { if (r.set.has(p)) r.set.delete(p); else r.set.add(p); }
  async saveRole() {
    const r = this.editRole();
    const body = { name: r.name, scope: r.scope, permissions: [...r.set].join(',') };
    try {
      if (r.id) await this.api.put(`/roles/${r.id}`, body); else await this.api.post('/roles', body);
      this.editRole.set(null); await this.load(); this.toast.ok('Role saved');
    } catch (e) { this.toast.error(e); }
  }
  async deleteRole(r: any) {
    if (!confirm(`Delete the role "${r.name}"?`)) return;
    try { await this.api.del(`/roles/${r.id}`); await this.load(); } catch (e) { this.toast.error(e); }
  }
}

@Component({
  imports: [FormsModule],
  template: `
    <div class="page-head"><div><h1>Bulk import</h1><p class="muted">Upload a CSV, check it with a dry run, then import. An import only commits when every row is valid.</p></div></div>
    <div class="card stack">
      <div class="grid cols-3">
        <label class="field">What to import<select [(ngModel)]="kind"><option value="students">Students and guardians</option><option value="staff">Staff</option></select></label>
        <label class="field">CSV file<input type="file" accept=".csv,text/csv" (change)="pick($event)" /></label>
        <div style="align-self:end"><button class="btn link" (click)="template()">Download template</button></div>
      </div>
      <p class="small muted">Importing into {{ ctx.campus()?.name }}@if (kind === 'students') {, academic year {{ ctx.year()?.name }}}. Excel users: save the sheet as CSV (UTF-8).</p>
      <div class="row">
        <button class="btn" [disabled]="!text()" (click)="run(true)">Dry run (validate only)</button>
        <button class="btn primary" [disabled]="!result()?.dryRun || result()?.errors.length" (click)="run(false)">Import {{ result()?.valid ?? '' }} rows</button>
      </div>
    </div>
    @if (result(); as r) {
      <div class="card">
        @if (r.committed) { <div class="alert ok">Imported {{ r.imported }} rows.</div> }
        @else if (r.errors.length) { <div class="alert danger">{{ r.errors.length }} problem(s) found in {{ r.total }} rows. Nothing was imported. Fix the file and run again.</div> }
        @else { <div class="alert ok">All {{ r.total }} rows are valid. Nothing has been written yet; choose Import to commit.</div> }
        @if (r.errors.length) {
          <table><thead><tr><th>Row</th><th>Problem</th></tr></thead><tbody>@for (e of r.errors; track $index) { <tr><td>{{ e.row }}</td><td>{{ e.message }}</td></tr> }</tbody></table>
        }
      </div>
    }
  `,
})
export class ImportPage {
  ctx = inject(Ctx);
  private api = inject(Api);
  private toast = inject(Toast);
  kind = 'students';
  text = signal(''); result = signal<any>(null);

  async pick(e: Event) {
    const file = (e.target as HTMLInputElement).files?.[0];
    this.text.set(file ? await file.text() : ''); this.result.set(null);
  }
  template() { this.api.open(`/admin/import/${this.kind}/template`, undefined, `${this.kind}-template.csv`).catch((e) => this.toast.error(e)); }
  async run(dryRun: boolean) {
    try {
      const q = `?campusId=${this.ctx.campusId()}&dryRun=${dryRun}` + (this.kind === 'students' ? `&academicYearId=${this.ctx.yearId()}` : '');
      this.result.set(await this.api.postText(`/admin/import/${this.kind}${q}`, this.text()));
    } catch (e) { this.toast.error(e); }
  }
}

@Component({
  imports: [FormsModule, MoneyPipe],
  template: `
    <div class="center-page">
      <div class="card">
        <span class="badge warn">Demo payment gateway</span>
        <h1 style="margin-top:8px">Secure payment</h1>
        @if (p(); as p) {
          @if (p.status === 'pending') {
            <p class="muted">You are paying</p>
            <p style="font-size:28px;font-weight:650">{{ p.amount | money: p.currency }}</p>
            <p class="small muted">This screen stands in for the payment provider's hosted page. In production, card details are entered on the provider's site and never reach the school's servers.</p>
            <div class="row"><button class="btn primary" (click)="finish('succeeded')">Pay</button><button class="btn" (click)="finish('failed')">Decline</button></div>
          } @else {
            <div class="alert" [class.ok]="p.status === 'succeeded'" [class.danger]="p.status !== 'succeeded'">Payment {{ p.status }}.</div>
            <button class="btn primary" (click)="back()">Return to the school portal</button>
          }
        } @else { <p class="muted">{{ error() || 'Loading…' }}</p> }
      </div>
    </div>
  `,
})
export class MockPayPage {
  private api = inject(Api);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private auth = inject(Auth);
  p = signal<any>(null); error = signal('');
  private ref = this.route.snapshot.paramMap.get('reference')!;
  constructor() { this.api.get(`/payments/mock/${this.ref}`).then((p) => this.p.set(p)).catch(() => this.error.set('This payment link is not valid.')); }
  async finish(status: string) {
    try { const r = await this.api.post(`/payments/mock/${this.ref}`, { status }); this.p.set({ ...this.p(), status: r.status }); } catch (e: any) { this.error.set(e.message); }
  }
  back() { this.router.navigateByUrl(this.auth.isPortal() ? '/portal' : '/fees'); }
}
