import { Component, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Api, Auth, Ctx, DAYS, DayPipe, MoneyPipe, TPipe, Toast } from '../core';
import { Col, CrudTable } from '../crud-table';
import { Modal } from '../modal';

const blankStaff = () => ({ employeeNumber: '', firstName: '', lastName: '', designation: '', department: '', qualification: '', email: '', phone: '', joinedOn: null, status: 'active' });

/** Shared add/edit staff form, used by the list and the profile page. */
@Component({
  selector: 'staff-form',
  imports: [FormsModule],
  template: `
    <form (ngSubmit)="save()">
      <div class="grid cols-2">
        <label class="field">Employee number<input type="text" name="no" [(ngModel)]="s.employeeNumber" required /></label>
        <label class="field">Status<select name="st" [(ngModel)]="s.status"><option value="active">Active</option><option value="left">Left the school</option></select></label>
        <label class="field">First name<input type="text" name="fn" [(ngModel)]="s.firstName" required /></label>
        <label class="field">Last name<input type="text" name="ln" [(ngModel)]="s.lastName" /></label>
        <label class="field">Designation<input type="text" name="ds" [(ngModel)]="s.designation" placeholder="e.g. Mathematics teacher" /></label>
        <label class="field">Department<input type="text" name="dp" [(ngModel)]="s.department" /></label>
        <label class="field">Qualification<input type="text" name="ql" [(ngModel)]="s.qualification" /></label>
        <label class="field">Joined on<input type="date" name="jn" [(ngModel)]="s.joinedOn" /></label>
        <label class="field">Email<input type="email" name="em" [(ngModel)]="s.email" /></label>
        <label class="field">Phone<input type="tel" name="ph" [(ngModel)]="s.phone" /></label>
      </div>
      @if (error()) { <div class="alert danger" style="margin-top:12px">{{ error() }}</div> }
      <div class="form-actions"><ng-content /><button class="btn primary" type="submit">Save</button></div>
    </form>
  `,
})
export class StaffForm {
  private api = inject(Api);
  private ctx = inject(Ctx);
  s: any = blankStaff();
  error = signal('');
  onSaved: (s: any) => void = () => {};
  edit(row: any | null, onSaved: (s: any) => void) { this.s = row ? { ...row } : blankStaff(); this.error.set(''); this.onSaved = onSaved; }
  async save() {
    const body = { ...this.s, campusId: this.s.campusId ?? this.ctx.campusId(), joinedOn: this.s.joinedOn || null };
    try { this.onSaved(this.s.id ? await this.api.put(`/staff/${this.s.id}`, body) : await this.api.post('/staff', body)); } catch (e: any) { this.error.set(e.message); }
  }
}

@Component({
  imports: [FormsModule, RouterLink, Modal, StaffForm],
  template: `
    <div class="page-head">
      <div><h1>Staff</h1><p class="muted">{{ filtered().length }} of {{ rows().length }} · teaching load for {{ ctx.year()?.name }}</p></div>
      @if (auth.can('admin.users')) { <button class="btn primary" (click)="add()">Add staff member</button> }
    </div>
    <div class="card">
      <div class="row" style="margin-bottom:12px">
        <input type="search" style="max-width:280px" placeholder="Name, subject or department" [ngModel]="q()" (ngModelChange)="q.set($event)" aria-label="Search staff" />
        <select style="width:auto" [ngModel]="status()" (ngModelChange)="status.set($event)" aria-label="Status"><option value="active">Active</option><option value="left">Left</option><option value="">All</option></select>
      </div>
      <div class="table-wrap"><table>
        <thead><tr><th>Name</th><th>Designation</th><th>Class teacher of</th><th>Subjects</th><th class="num">Periods / week</th><th>Login</th></tr></thead>
        <tbody>
          @for (r of filtered(); track r.staff.id) {
            <tr class="clickable" [routerLink]="['/staff', r.staff.id]">
              <td><a [routerLink]="['/staff', r.staff.id]">{{ r.staff.firstName }} {{ r.staff.lastName }}</a><div class="small muted">{{ r.staff.employeeNumber }}</div></td>
              <td>{{ r.staff.designation ?? '–' }}<div class="small muted">{{ r.staff.department }}</div></td>
              <td>@for (c of r.classTeacherOf; track c) { <span class="badge info">{{ c }}</span> } @empty { <span class="muted">–</span> }</td>
              <td>@for (s of r.subjects; track s) { <span class="badge">{{ s }}</span> } @empty { <span class="muted">–</span> }</td>
              <td class="num">{{ r.periodsPerWeek }}</td>
              <td>@if (r.hasLogin) { <span class="badge ok">yes</span> } @else { <span class="badge">no</span> }</td>
            </tr>
          } @empty { <tr><td colspan="6" class="empty">No staff match.</td></tr> }
        </tbody>
      </table></div>
    </div>
    <app-modal [open]="adding()" title="Add staff member" (closed)="adding.set(false)">
      <staff-form><button class="btn" type="button" (click)="adding.set(false)">Cancel</button></staff-form>
    </app-modal>
  `,
})
export class StaffPage {
  auth = inject(Auth);
  ctx = inject(Ctx);
  private api = inject(Api);
  private toast = inject(Toast);
  rows = signal<any[]>([]); adding = signal(false);
  q = signal(''); status = signal('active');
  private form = viewChild(StaffForm);
  filtered = computed(() => {
    const q = this.q().trim().toLowerCase();
    return this.rows().filter((r) => (!this.status() || r.staff.status === this.status()) &&
      (!q || [r.staff.firstName, r.staff.lastName, r.staff.designation, r.staff.department, ...r.subjects].join(' ').toLowerCase().includes(q)));
  });
  constructor() {
    effect(() => { this.ctx.campusId(); this.ctx.yearId(); untracked(() => this.load()); });
  }
  async load() {
    if (!this.ctx.campusId()) return;
    try { this.rows.set(await this.api.get('/staff/directory', { campusId: this.ctx.campusId(), academicYearId: this.ctx.yearId() })); } catch (e) { this.toast.error(e); }
  }
  add() {
    this.form()?.edit(null, () => { this.adding.set(false); this.toast.ok('Staff member added'); this.load(); });
    this.adding.set(true);
  }
}

@Component({
  imports: [RouterLink, DayPipe, MoneyPipe, Modal, StaffForm, CrudTable, FormsModule, TPipe],
  template: `
    @if (p(); as p) {
      <div class="page-head">
        <div>
          <a routerLink="/staff" class="small">← Staff</a>
          <h1>{{ p.staff.firstName }} {{ p.staff.lastName }}</h1>
          <p class="muted">{{ p.staff.designation }} · {{ p.staff.employeeNumber }} · <span class="badge" [class.ok]="p.staff.status === 'active'">{{ p.staff.status }}</span></p>
        </div>
        @if (auth.can('admin.users')) { <button class="btn" (click)="edit()">Edit details</button> }
      </div>

      <div class="stats">
        <div class="stat"><div class="label">Periods per week</div><div class="value">{{ p.periodsPerWeek }}</div></div>
        <div class="stat"><div class="label">Classes taught</div><div class="value">{{ p.teaching.length }}</div></div>
        <div class="stat"><div class="label">Class teacher of</div><div class="value">{{ p.classTeacherOf.length ? p.classTeacherOf[0].name : '–' }}</div></div>
        @if (p.payslips?.length) { <div class="stat"><div class="label">Last net pay ({{ p.payslips[0].month }})</div><div class="value">{{ p.payslips[0].net | money }}</div></div> }
      </div>

      <div class="grid cols-2">
        <div class="card">
          <h2>Details</h2>
          <dl class="kv">
            <dt>Department</dt><dd>{{ p.staff.department ?? '–' }}</dd>
            <dt>Qualification</dt><dd>{{ p.staff.qualification ?? '–' }}</dd>
            <dt>Joined</dt><dd>{{ p.staff.joinedOn | day }}</dd>
            <dt>Email</dt><dd>{{ p.staff.email ?? '–' }}</dd>
            <dt>Phone</dt><dd>{{ p.staff.phone ?? '–' }}</dd>
            <dt>Sign-in</dt><dd>@if (p.login; as l) { {{ l.email }} · {{ l.role }} · two-step {{ l.mfaEnabled ? 'on' : 'off' }}<div class="small muted">last sign-in {{ l.lastLoginAt | day: true }}</div> } @else { No login. Create one in Users & roles. }</dd>
          </dl>
        </div>
        <div class="card">
          <h2>Teaching in {{ ctx.year()?.name }}</h2>
          <table style="margin-top:8px"><thead><tr><th>Class</th><th>Subject</th><th class="num">Periods / week</th></tr></thead>
            <tbody>@for (t of p.teaching; track t.classSubjectId) { <tr><td><a [routerLink]="['/classes', t.sectionId]">{{ t.section }}</a></td><td>{{ t.subject }}</td><td class="num">{{ t.periods }}</td></tr> }
            @empty { <tr><td colspan="3" class="empty">Not assigned to any class this year.</td></tr> }</tbody></table>
        </div>
      </div>

      <div class="card table-wrap">
        <h2>Weekly timetable</h2>
        <table class="tt">
          <thead><tr><th style="width:110px">Period</th>@for (d of [1, 2, 3, 4, 5]; track d) { <th>{{ days[d - 1] | t }}</th> }</tr></thead>
          <tbody>@for (per of periods(); track per.id) {
            <tr><th scope="row">{{ per.name }}<div class="small muted">{{ per.startTime.slice(0, 5) }}</div></th>
              @if (per.isBreak) { <td class="break" colspan="5"></td> }
              @else { @for (d of [1, 2, 3, 4, 5]; track d) { @let s = slot(d, per.id); <td>@if (s) { <strong>{{ s.subject }}</strong><div class="small muted">{{ s.section }}@if (s.room) { · {{ s.room }} }</div> }</td> } }
            </tr>
          }</tbody>
        </table>
      </div>

      @if (p.leave) {
        <div class="card table-wrap">
          <h2>Leave</h2>
          <table><thead><tr><th>From</th><th>To</th><th>Reason</th><th>Type</th><th>Status</th></tr></thead>
            <tbody>@for (l of p.leave; track l.id) { <tr><td>{{ l.fromDate | day }}</td><td>{{ l.toDate | day }}</td><td>{{ l.reason }}</td><td>{{ l.unpaid ? 'Unpaid' : 'Paid' }}</td><td><span class="badge" [class.ok]="l.status === 'approved'" [class.warn]="l.status === 'pending'" [class.danger]="l.status === 'rejected'">{{ l.status }}</span></td></tr> }
            @empty { <tr><td colspan="5" class="empty">No leave requests.</td></tr> }</tbody></table>
        </div>
      }

      @if (p.bank) {
        <h2 style="margin-top:8px">Payroll</h2>
        <div class="grid cols-2">
          <div class="card">
            <div class="page-head"><h2>Bank and tax</h2><button class="btn small" (click)="openBank()">Edit</button></div>
            <dl class="kv"><dt>Bank</dt><dd>{{ p.bank.bankName ?? '–' }}</dd><dt>Account</dt><dd>{{ p.bank.bankAccount ? mask(p.bank.bankAccount) : '–' }}</dd><dt>Tax ID</dt><dd>{{ p.bank.taxId ?? '–' }}</dd></dl>
            <p class="small muted" style="margin-top:8px">Stored encrypted. Only payroll staff can see these.</p>
          </div>
          <div class="card">
            <h2>Recent payslips</h2>
            <table><thead><tr><th>Month</th><th class="num">Gross</th><th class="num">Deductions</th><th class="num">Net</th><th></th></tr></thead>
              <tbody>@for (s of p.payslips; track s.id) { <tr><td>{{ s.month }}</td><td class="num">{{ s.gross | money }}</td><td class="num">{{ s.deductions | money }}</td><td class="num"><strong>{{ s.net | money }}</strong></td><td><button class="btn small" (click)="pdf(s.id)">PDF</button></td></tr> }
              @empty { <tr><td colspan="5" class="empty">No payslips yet.</td></tr> }</tbody></table>
          </div>
        </div>
        <crud-table title="Salary structure (per month)" path="/pay-components" [columns]="payCols" [scope]="{ staffId: p.staff.id }" sortBy="order" />
        <p class="small muted">Percent components are a percentage of the earning named "Basic". Unpaid leave is deducted pro rata in each payroll run.</p>
      }

      <app-modal [open]="editing()" title="Edit staff member" (closed)="editing.set(false)">
        <staff-form><button class="btn" type="button" (click)="editing.set(false)">Cancel</button></staff-form>
      </app-modal>
      <app-modal [open]="bankOpen()" title="Bank and tax details" width="min(520px, 94vw)" (closed)="bankOpen.set(false)">
        @if (bankOpen()) {
          <form (ngSubmit)="saveBank()">
            <div class="stack">
              <label class="field">Bank name<input type="text" name="bn" [(ngModel)]="bank.bankName" /></label>
              <label class="field">Account number<input type="text" name="ba" [(ngModel)]="bank.bankAccount" autocomplete="off" /></label>
              <label class="field">Tax ID (e.g. PAN)<input type="text" name="tx" [(ngModel)]="bank.taxId" autocomplete="off" /></label>
            </div>
            <div class="form-actions"><button class="btn" type="button" (click)="bankOpen.set(false)">Cancel</button><button class="btn primary" type="submit">Save</button></div>
          </form>
        }
      </app-modal>
    }
  `,
})
export class StaffDetailPage {
  auth = inject(Auth);
  ctx = inject(Ctx);
  private api = inject(Api);
  private toast = inject(Toast);
  private route = inject(ActivatedRoute);
  days = DAYS;
  p = signal<any>(null); slots = signal<any[]>([]); periods = signal<any[]>([]);
  editing = signal(false); bankOpen = signal(false); bank: any = {};
  payCols: Col[] = [
    { key: 'name', label: 'Component', required: true },
    { key: 'kind', label: 'Kind', type: 'select', required: true, options: [{ value: 'earning', label: 'Earning' }, { value: 'deduction', label: 'Deduction' }] },
    { key: 'type', label: 'Calculated as', type: 'select', required: true, options: [{ value: 'fixed', label: 'Fixed amount' }, { value: 'percent', label: '% of Basic' }] },
    { key: 'amount', label: 'Amount or %', type: 'number' }, { key: 'order', label: 'Order', type: 'number' },
  ];
  private form = viewChild(StaffForm);

  constructor() {
    effect(() => { this.ctx.yearId(); untracked(() => this.reload()); });
  }
  private get id() { return this.route.snapshot.paramMap.get('id')!; }
  async reload() {
    const yearId = this.ctx.yearId();
    try {
      this.p.set(await this.api.get(`/staff/${this.id}/profile`, { academicYearId: yearId }));
      if (yearId && this.auth.can('timetable.read', 'academics.manage')) this.slots.set(await this.api.get('/timetable', { academicYearId: yearId, teacherId: this.id }));
      if (this.ctx.campusId()) this.periods.set((await this.api.get<any[]>('/periods', { campusId: this.ctx.campusId() })).sort((a, b) => a.order - b.order));
    } catch (e) { this.toast.error(e); }
  }
  slot(day: number, periodId: string) { return this.slots().find((s) => s.day === day && s.periodId === periodId); }
  mask(v: string) { return v.length > 4 ? '•••• ' + v.slice(-4) : v; }
  edit() {
    this.form()?.edit(this.p().staff, () => { this.editing.set(false); this.toast.ok('Saved'); this.reload(); });
    this.editing.set(true);
  }
  pdf(id: string) { this.api.open(`/payroll/payslips/${id}/pdf`).catch((e) => this.toast.error(e)); }
  openBank() { this.bank = { ...this.p().bank }; this.bankOpen.set(true); }
  async saveBank() {
    try { await this.api.put(`/staff/${this.id}/bank`, this.bank); this.bankOpen.set(false); this.toast.ok('Bank details saved'); await this.reload(); } catch (e) { this.toast.error(e); }
  }
}
