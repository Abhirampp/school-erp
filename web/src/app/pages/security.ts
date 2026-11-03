import { Component, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Api, Auth, Ctx, DayPipe, MoneyPipe, TPipe, Toast } from '../core';
import { Modal } from '../modal';

@Component({
  imports: [FormsModule, DayPipe, TPipe],
  template: `
    <div class="page-head"><div><h1>{{ 'Security & audit' | t }}</h1></div></div>
    <div class="tabs" role="tablist">@for (t of tabs; track t) { <button role="tab" [attr.aria-selected]="tab() === t" (click)="tab.set(t); load()">{{ t }}</button> }</div>

    @switch (tab()) {
      @case ('My sign-in') {
        <div class="grid cols-2">
          <div class="card">
            <h2>Two-step sign-in</h2>
            @if (auth.user()?.mfaEnabled || mfaDone()) { <div class="alert ok">Two-step sign-in is on. You will be asked for an authenticator code when you sign in.</div> }
            @else if (mfa(); as m) {
              <p>Add this key to an authenticator app (Google Authenticator, Microsoft Authenticator, 1Password), then enter the 6-digit code it shows.</p>
              <p><code style="font-size:15px;word-break:break-all">{{ m.secret }}</code></p>
              <form class="row" (ngSubmit)="enableMfa()"><label class="sr-only" for="code">Code</label><input id="code" type="text" name="code" style="width:140px" inputmode="numeric" maxlength="6" [(ngModel)]="code" required /><button class="btn primary" type="submit">Turn on</button></form>
            } @else {
              <p class="muted">Protect your account with a code from an authenticator app in addition to your password.</p>
              <button class="btn primary" (click)="setupMfa()">Set up two-step sign-in</button>
            }
          </div>
          <form class="card stack" (ngSubmit)="changePassword()">
            <h2>Change password</h2>
            <label class="field">Current password<input type="password" name="cur" [(ngModel)]="pw.current" required autocomplete="current-password" /></label>
            <label class="field">New password (10+ characters)<input type="password" name="new" [(ngModel)]="pw.next" required minlength="10" autocomplete="new-password" /></label>
            <div><button class="btn" type="submit">Change password</button></div>
          </form>
        </div>
      }
      @case ('Audit log') {
        <div class="card row">
          <label class="field">Action<select [(ngModel)]="f.action" (ngModelChange)="load()"><option value="">Any</option>@for (a of ['create', 'update', 'delete', 'export', 'import', 'login', 'erase', 'rollover', 'support-access']; track a) { <option [value]="a">{{ a }}</option> }</select></label>
          <label class="field">Record type<input type="text" [(ngModel)]="f.entityType" (change)="load()" placeholder="e.g. Student, Invoice" /></label>
          <label class="field">Who or record ID<input type="search" [(ngModel)]="f.search" (change)="load()" /></label>
        </div>
        <div class="card table-wrap"><table>
          <thead><tr><th>When</th><th>Who</th><th>Action</th><th>Record</th><th>Change</th></tr></thead>
          <tbody>@for (a of audit(); track a.id) {
            <tr><td class="nowrap">{{ a.at | day: true }}</td><td>{{ a.actorName }} @if (a.viaSupportAccess) { <span class="badge warn">support</span> }<div class="small muted">{{ a.ip }}</div></td>
              <td><span class="badge" [class.danger]="a.action === 'delete' || a.action === 'erase'" [class.info]="a.action === 'export' || a.action === 'support-access'">{{ a.action }}</span></td>
              <td>{{ a.entityType }}<div class="small muted">{{ a.entityId }}</div></td>
              <td>@if (a.beforeJson || a.afterJson) { <details><summary class="small">View</summary>@if (a.beforeJson) { <div class="small muted">Before</div><pre class="json">{{ a.beforeJson }}</pre> }@if (a.afterJson) { <div class="small muted">After</div><pre class="json">{{ a.afterJson }}</pre> }</details> }</td></tr>
          } @empty { <tr><td colspan="5" class="empty">No audit events match.</td></tr> }</tbody>
        </table></div>
        <p class="small muted">The audit log is append-only: there is no way to edit or delete entries from the application.</p>
      }
      @case ('Support access') {
        <p class="muted">The software provider's support staff can only look at your school's data after you approve a request here. Access is read-only, expires automatically and is recorded in the audit log.</p>
        <div class="card table-wrap"><table>
          <thead><tr><th>Requested</th><th>By</th><th>Reason</th><th>For</th><th>Status</th><th></th></tr></thead>
          <tbody>@for (r of support(); track r.id) {
            <tr><td>{{ r.requestedAt | day: true }}</td><td>{{ r.requestedByName }}</td><td>{{ r.reason }}</td><td>{{ r.hours }} h</td>
              <td><span class="badge" [class.ok]="r.status === 'approved'" [class.warn]="r.status === 'pending'" [class.danger]="r.status === 'denied' || r.status === 'revoked'">{{ r.status }}</span>@if (r.status === 'approved') { <div class="small muted">until {{ r.expiresAt | day: true }}</div> }</td>
              <td class="nowrap" style="text-align:end">
                @if (r.status === 'pending') { <button class="btn small primary" (click)="decide(r, true)">Approve</button> <button class="btn small danger" (click)="decide(r, false)">Deny</button> }
                @if (r.status === 'approved') { <button class="btn small danger" (click)="revoke(r)">Revoke now</button> }
              </td></tr>
          } @empty { <tr><td colspan="6" class="empty">No support access has been requested.</td></tr> }</tbody>
        </table></div>
      }
    }
  `,
})
export class SecurityPage {
  auth = inject(Auth);
  private api = inject(Api);
  private toast = inject(Toast);
  tabs = [...(this.auth.support() ? [] : ['My sign-in']), ...(this.auth.can('audit.read') ? ['Audit log'] : []), ...(this.auth.can('admin.settings') ? ['Support access'] : [])];
  tab = signal(this.tabs[0]);
  audit = signal<any[]>([]); support = signal<any[]>([]); mfa = signal<any>(null); mfaDone = signal(false);
  f: any = { action: '', entityType: '', search: '' }; code = ''; pw: any = {};

  constructor() { this.load(); }
  async load() {
    try {
      if (this.tab() === 'Audit log') this.audit.set(await this.api.get('/audit', this.f));
      if (this.tab() === 'Support access') this.support.set(await this.api.get('/security/support-requests'));
    } catch (e) { this.toast.error(e); }
  }
  async setupMfa() { try { this.mfa.set(await this.api.post('/auth/mfa/setup')); } catch (e) { this.toast.error(e); } }
  async enableMfa() { try { await this.api.post('/auth/mfa/enable', { code: this.code }); this.mfaDone.set(true); this.toast.ok('Two-step sign-in is on'); } catch (e) { this.toast.error(e); } }
  async changePassword() { try { await this.api.post('/auth/password', this.pw); this.pw = {}; this.toast.ok('Password changed'); } catch (e) { this.toast.error(e); } }
  async decide(r: any, approve: boolean) { try { await this.api.post(`/security/support-requests/${r.id}/decision`, { approve }); await this.load(); } catch (e) { this.toast.error(e); } }
  async revoke(r: any) { try { await this.api.post(`/security/support-requests/${r.id}/revoke`); await this.load(); } catch (e) { this.toast.error(e); } }
}

@Component({
  imports: [FormsModule, MoneyPipe, TPipe],
  template: `
    <div class="page-head"><div><h1>{{ 'Year-end rollover' | t }}</h1><p class="muted">Closes {{ ctx.year()?.name }} for {{ ctx.campus()?.name }} and opens the next year. Review the preview before you run it: a rollover cannot be undone from the application.</p></div></div>
    @if (result(); as r) {
      <div class="alert ok">Rollover complete: {{ r.promoted }} students promoted, {{ r.graduated }} graduated, {{ r.sectionsCreated }} sections created, {{ r.balancesCarried }} unpaid balance(s) totalling {{ r.amountCarried | money }} carried forward.</div>
    }
    @if (p(); as p) {
      @for (b of p.blockers; track b) { <div class="alert danger">{{ b }}</div> }
      @for (w of p.warnings; track w) { <div class="alert warn">{{ w }}</div> }
      <div class="grid cols-2">
        <div class="card">
          <h2>1. Promote students</h2>
          <table><thead><tr><th>From</th><th>To</th><th class="num">Students</th></tr></thead>
            <tbody>@for (l of p.promotions; track l.fromGrade) { <tr><td>{{ l.fromGrade }}</td><td>{{ l.toGrade ?? 'Graduate (leave the school)' }}</td><td class="num">{{ l.students }}</td></tr> }</tbody></table>
          <p class="small muted" style="margin-top:8px">Students stay with their section letter. Withdraw anyone who is not returning before you run the rollover.</p>
        </div>
        <div class="card">
          <h2>2. Clone configuration</h2>
          <dl class="kv"><dt>Sections</dt><dd>{{ p.sections }}</dd><dt>Subject and teacher mappings</dt><dd>{{ p.classSubjects }} (with the timetable)</dd><dt>Grading schemes</dt><dd>{{ p.gradingSchemes }} (latest versions)</dd><dt>Fee structures</dt><dd>{{ p.feeStructures }} (due dates moved to the new year)</dd></dl>
          <h2 style="margin-top:16px">3. Carry balances forward</h2>
          <p>{{ p.outstandingBalance | money }} unpaid across {{ p.studentsWithBalance }} student(s) becomes an opening invoice in the new year.</p>
          <h2 style="margin-top:16px">4. Archive {{ p.fromYear }}</h2>
          <p class="muted">Attendance and marks for the archived year become read-only.</p>
        </div>
      </div>
      <form class="card grid cols-4" (ngSubmit)="run()">
        <label class="field">New year name<input type="text" name="n" [(ngModel)]="form.name" required /></label>
        <label class="field">Starts<input type="date" name="s" [(ngModel)]="form.startDate" required /></label>
        <label class="field">Ends<input type="date" name="e" [(ngModel)]="form.endDate" required /></label>
        <div style="align-self:end"><button class="btn primary" type="submit" [disabled]="p.blockers.length > 0 || busy()">Run rollover</button></div>
      </form>
    }
  `,
})
export class RolloverPage {
  ctx = inject(Ctx);
  private api = inject(Api);
  private toast = inject(Toast);
  p = signal<any>(null); result = signal<any>(null); busy = signal(false);
  form: any = {};

  constructor() { effect(() => { this.ctx.campusId(); const y = this.ctx.yearId(); untracked(() => this.load(y)); }); }
  async load(yearId: string | null) {
    if (!yearId) { this.p.set(null); return; }
    try {
      this.p.set(await this.api.get('/rollover/preview', { fromYearId: yearId }));
      const y = this.ctx.year();
      if (y) {
        const shift = (d: string) => `${Number(d.slice(0, 4)) + 1}${d.slice(4)}`;
        const startYear = Number(y.startDate.slice(0, 4)) + 1;
        this.form = { name: y.startDate.slice(0, 4) === y.endDate.slice(0, 4) ? `${startYear}` : `${startYear}-${String(startYear + 1).slice(2)}`, startDate: shift(y.startDate), endDate: shift(y.endDate) };
      }
    } catch (e) { this.toast.error(e); }
  }
  async run() {
    if (!confirm(`Run the year-end rollover from ${this.ctx.year()?.name} to ${this.form.name}? This archives the current year.`)) return;
    this.busy.set(true);
    try {
      const r = await this.api.post('/rollover/run', { ...this.form, fromYearId: this.ctx.yearId() });
      this.result.set(r);
      await this.ctx.loadYears();
      this.ctx.setYear(r.newYearId);
    } catch (e) { this.toast.error(e); } finally { this.busy.set(false); }
  }
}

@Component({
  imports: [FormsModule, DayPipe, Modal],
  template: `
    <div class="page-head"><div><h1>Platform</h1><p class="muted">Tenant directory and support access. Student data is never visible here.</p></div><button class="btn primary" (click)="provisionOpen = true">Provision a tenant</button></div>
    <div class="card table-wrap"><h2>Tenants</h2><table>
      <thead><tr><th>Name</th><th>Slug</th><th>Region</th><th>Plan</th><th>Status</th><th></th></tr></thead>
      <tbody>@for (t of tenants(); track t.id) {
        <tr><td>{{ t.name }}</td><td>{{ t.slug }}</td><td>{{ t.region }}</td><td>{{ t.plan }}</td><td><span class="badge ok">{{ t.status }}</span></td>
          <td style="text-align:end"><button class="btn small" (click)="req = { tenantId: t.id, tenant: t.name, hours: 4, reason: '' }">Request support access</button></td></tr>
      }</tbody>
    </table></div>
    <app-modal [open]="!!req" [title]="'Request support access to ' + (req?.tenant ?? '')" width="min(560px, 94vw)" (closed)="req = null">
    @if (req; as r) {
      <form (ngSubmit)="request()"><div class="grid cols-2">
        <label class="field">Reason shown to {{ r.tenant }}<input type="text" name="r" [(ngModel)]="r.reason" required /></label>
        <label class="field">Hours<input type="number" name="h" [(ngModel)]="r.hours" min="1" max="72" /></label>
        </div>
        <p class="small muted" style="margin-top:12px">The school must approve this before you can open a read-only session.</p>
        <div class="form-actions"><button class="btn" type="button" (click)="req = null">Cancel</button><button class="btn primary" type="submit">Send request</button></div>
      </form>
    }
    </app-modal>
    <div class="card table-wrap"><h2>Support access requests</h2><table>
      <thead><tr><th>Requested</th><th>Tenant</th><th>Reason</th><th>Status</th><th></th></tr></thead>
      <tbody>@for (r of requests(); track r.id) {
        <tr><td>{{ r.requestedAt | day: true }}</td><td>{{ name(r.tenantId) }}</td><td>{{ r.reason }}</td>
          <td><span class="badge" [class.ok]="r.status === 'approved'" [class.warn]="r.status === 'pending'">{{ r.status }}</span>@if (r.expiresAt) { <span class="small muted"> until {{ r.expiresAt | day: true }}</span> }</td>
          <td style="text-align:end">@if (r.status === 'approved') { <button class="btn small primary" (click)="enter(r)">Open read-only session</button> }</td></tr>
      } @empty { <tr><td colspan="5" class="empty">No requests.</td></tr> }</tbody>
    </table></div>
    <app-modal [open]="provisionOpen" title="Provision a tenant" (closed)="provisionOpen = false">
    @if (provisionOpen) {
    <form class="stack" (ngSubmit)="provision()">
      <div class="grid cols-2">
        <label class="field">School group name<input type="text" name="n" [(ngModel)]="t.name" required /></label>
        <label class="field">Slug (used in the public form link)<input type="text" name="s" [(ngModel)]="t.slug" required /></label>
        <label class="field">Hosting region<select name="rg" [(ngModel)]="t.region"><option value="india">India</option><option value="gcc">UAE / KSA</option><option value="eu">EU</option><option value="sea">Southeast Asia</option></select></label>
        <label class="field">Plan<select name="pl" [(ngModel)]="t.plan"><option value="standard">Standard</option><option value="premium">Premium</option></select></label>
        <label class="field">First admin's name<input type="text" name="an" [(ngModel)]="t.adminName" required /></label>
        <label class="field">First admin's email<input type="email" name="ae" [(ngModel)]="t.adminEmail" required /></label>
        <label class="field">Temporary password (10+ characters)<input type="password" name="ap" [(ngModel)]="t.adminPassword" required autocomplete="new-password" /></label>
      </div>
      <div class="form-actions"><button class="btn" type="button" (click)="provisionOpen = false">Cancel</button><button class="btn primary" type="submit">Create tenant</button></div>
    </form>
    }
    </app-modal>
  `,
})
export class PlatformPage {
  private api = inject(Api);
  private auth = inject(Auth);
  private ctx = inject(Ctx);
  private toast = inject(Toast);
  private router = inject(Router);
  tenants = signal<any[]>([]); requests = signal<any[]>([]);
  req: any = null; t: any = { region: 'india', plan: 'standard' }; provisionOpen = false;

  constructor() { this.load(); }
  name(id: string) { return this.tenants().find((t) => t.id === id)?.name ?? id; }
  async load() { try { this.tenants.set(await this.api.get('/platform/tenants')); this.requests.set(await this.api.get('/platform/support-requests')); } catch (e) { this.toast.error(e); } }
  async request() { try { await this.api.post('/platform/support-requests', this.req); this.req = null; this.toast.ok('Request sent to the school'); await this.load(); } catch (e) { this.toast.error(e); } }
  async provision() { try { await this.api.post('/platform/tenants', this.t); this.t = { region: 'india', plan: 'standard' }; this.provisionOpen = false; this.toast.ok('Tenant created'); await this.load(); } catch (e) { this.toast.error(e); } }
  async enter(r: any) {
    try {
      const platformToken = this.auth.token()!;
      this.auth.startSupport(await this.api.post(`/platform/support-requests/${r.id}/session`), platformToken);
      await this.ctx.load();
      this.router.navigateByUrl('/students');
    } catch (e) { this.toast.error(e); }
  }
}
