import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Api, Auth, Ctx, DayPipe, MoneyPipe, TPipe, Toast } from '../core';
import { Col, CrudTable } from '../crud-table';
import { Modal } from '../modal';

const INVOICE_BADGE: Record<string, string> = { open: 'warn', partial: 'info', paid: 'ok', void: '', carried: '' };

@Component({
  imports: [FormsModule, CrudTable, MoneyPipe, DayPipe, TPipe, Modal],
  template: `
    <div class="page-head"><div><h1>{{ 'Fees' | t }}</h1><p class="muted">Every invoice, payment, fine, refund and credit note posts to a double-entry ledger.</p></div></div>
    <div class="tabs" role="tablist">@for (t of tabs; track t) { <button role="tab" [attr.aria-selected]="tab() === t" (click)="tab.set(t); load()">{{ t }}</button> }</div>

    @switch (tab()) {
      @case ('Invoices') {
        <div class="card row">
          <label class="field">{{ 'Status' | t }}<select [(ngModel)]="status" (ngModelChange)="load()"><option value="">All</option>@for (s of ['open', 'partial', 'overdue', 'paid', 'carried']; track s) { <option [value]="s">{{ s }}</option> }</select></label>
          <span style="flex:1"></span>
          @if (canManage) {
            <button class="btn primary" (click)="generate()">Generate invoices for {{ ctx.year()?.name }}</button>
            <button class="btn" (click)="run('fines')">Apply late fines</button>
            <button class="btn" (click)="run('reminders')">Send due reminders</button>
          }
        </div>
        @if (notice()) { <div class="alert">{{ notice() }}</div> }
        <div class="card table-wrap"><table>
          <thead><tr><th>{{ 'Invoice' | t }}</th><th>Student</th><th>For</th><th>{{ 'Due' | t }}</th><th class="num">{{ 'Total' | t }}</th><th class="num">{{ 'Paid' | t }}</th><th class="num">{{ 'Balance' | t }}</th><th>{{ 'Status' | t }}</th></tr></thead>
          <tbody>@for (i of invoices(); track i.id) {
            <tr class="clickable" (click)="open(i.id)" tabindex="0" (keydown.enter)="open(i.id)">
              <td>{{ i.number }}</td><td>{{ i.student }}</td><td>{{ i.title }}</td><td>{{ i.dueDate | day }}</td>
              <td class="num">{{ i.total | money: i.currency }}</td><td class="num">{{ i.paidTotal | money: i.currency }}</td><td class="num">{{ i.balance | money: i.currency }}</td>
              <td><span [class]="'badge ' + badge[i.status]">{{ i.status }}</span></td></tr>
          } @empty { <tr><td colspan="8" class="empty">No invoices. Set up fee structures, then generate invoices.</td></tr> }</tbody>
        </table></div>
      }
      @case ('Structures') {
        @if (canManage) { <div class="row end" style="margin-bottom:12px"><button class="btn primary" (click)="structureOpen.set(true)">New fee structure</button></div> }
        @for (s of structures(); track s.id) {
          <div class="card">
            <div class="page-head"><h2>{{ s.name }} <span class="badge">{{ s.grade }}</span> @if (s.category) { <span class="badge info">{{ s.category }}</span> }</h2>
              <div class="row"><strong>{{ s.total | money: s.currency }}</strong>@if (canManage) { <button class="btn small danger" (click)="deleteStructure(s)">{{ 'Delete' | t }}</button> }</div></div>
            <div class="grid cols-2">
              <table><thead><tr><th>Fee head</th><th class="num">Annual amount</th></tr></thead><tbody>@for (i of s.items; track $index) { <tr><td>{{ i.head }}</td><td class="num">{{ i.amount | money: s.currency }}</td></tr> }</tbody></table>
              <table><thead><tr><th>Installment</th><th>{{ 'Due' | t }}</th><th class="num">Share</th></tr></thead><tbody>@for (i of s.installments; track i.id) { <tr><td>{{ i.name }}</td><td>{{ i.dueDate | day }}</td><td class="num">{{ i.percent }}%</td></tr> }</tbody></table>
            </div>
          </div>
        } @empty { <p class="empty">No fee structures for {{ ctx.year()?.name }}.</p> }
        @if (canManage) {
          <app-modal [open]="structureOpen()" title="New fee structure" width="min(900px, 94vw)" (closed)="structureOpen.set(false)">
          @if (structureOpen()) {
          <form class="stack" (ngSubmit)="createStructure()">
            <div class="grid cols-2">
              <label class="field">Grade<select name="g" [(ngModel)]="fs.gradeLevelId" required>@for (g of grades(); track g.id) { <option [ngValue]="g.id">{{ g.name }}</option> }</select></label>
              <label class="field">{{ 'Name' | t }}<input type="text" name="n" [(ngModel)]="fs.name" required /></label>
              <label class="field">Student category (optional)<input type="text" name="c" [(ngModel)]="fs.category" placeholder="e.g. staff ward" /></label>
              <label class="field">Currency<input type="text" name="cur" [(ngModel)]="fs.currency" required /></label>
            </div>
            <div class="grid cols-2">
              <div><h3>Fee heads</h3>
                @for (i of fs.items; track $index) { <div class="row" style="margin-bottom:6px"><select [name]="'ih' + $index" [(ngModel)]="i.feeHeadId" aria-label="Fee head" style="width:auto;flex:1">@for (h of heads(); track h.id) { <option [ngValue]="h.id">{{ h.name }}</option> }</select><input type="number" [name]="'ia' + $index" [(ngModel)]="i.amount" aria-label="Amount" style="width:130px" min="0" /></div> }
                <button type="button" class="btn small" (click)="fs.items.push({ feeHeadId: heads()[0]?.id, amount: 0 })">Add head</button></div>
              <div><h3>Installments (must total 100%)</h3>
                @for (i of fs.installments; track $index) { <div class="row" style="margin-bottom:6px"><input type="text" [name]="'nn' + $index" [(ngModel)]="i.name" aria-label="Installment name" style="flex:1;width:auto" /><input type="date" [name]="'nd' + $index" [(ngModel)]="i.dueDate" aria-label="Due date" style="width:150px" /><input type="number" [name]="'np' + $index" [(ngModel)]="i.percent" aria-label="Percent" style="width:80px" /></div> }
                <button type="button" class="btn small" (click)="fs.installments.push({ name: 'Installment ' + (fs.installments.length + 1), dueDate: '', percent: 0 })">Add installment</button></div>
            </div>
            <div class="form-actions"><button class="btn" type="button" (click)="structureOpen.set(false)">Cancel</button><button class="btn primary" type="submit">Create structure</button></div>
          </form>
          }
          </app-modal>
          <crud-table title="Fee heads" path="/fee-heads" [columns]="headCols" [scope]="{ campusId: ctx.campusId() }" (changed)="load()" />
        }
      }
      @case ('Discounts') {
        <crud-table title="Discounts, scholarships and sibling concessions" path="/discounts" [columns]="discountCols" [canEdit]="canManage" (changed)="load()" />
        <crud-table [title]="'Awarded to students in ' + (ctx.year()?.name ?? '')" path="/student-discounts" [columns]="awardCols()" [scope]="{ academicYearId: ctx.yearId() }" [canEdit]="canManage" />
        <p class="small muted">Discounts apply to invoices generated after they are awarded. Sibling concessions marked automatic apply to every child except the eldest enrolled sibling.</p>
      }
      @case ('Aging') {
        @if (aging(); as a) {
          <div class="card table-wrap"><table>
            <thead><tr><th>Student</th>@for (b of a.buckets; track b) { <th class="num">{{ b === 'current' ? 'Not yet due' : b + ' days' }}</th> }<th class="num">{{ 'Total' | t }}</th></tr></thead>
            <tbody>@for (r of a.rows; track r.studentId) { <tr><td>{{ r.student }}</td>@for (b of a.buckets; track b) { <td class="num">{{ r.buckets[b] ? (r.buckets[b] | money) : '' }}</td> }<td class="num"><strong>{{ r.total | money }}</strong></td></tr> }
              @empty { <tr><td colspan="7" class="empty">Nothing outstanding.</td></tr> }</tbody>
            <tfoot><tr><th>Total</th>@for (b of a.buckets; track b) { <th class="num">{{ a.totals[b] | money }}</th> }<th class="num">{{ a.total | money }}</th></tr></tfoot>
          </table></div>
        }
      }
      @case ('Ledger') {
        @if (ledger(); as l) {
          <div class="card">
            <div class="page-head"><h2>Trial balance</h2>@if (canManage) { <button class="btn" (click)="exportLedger()">Export journal (CSV)</button> }</div>
            <div class="table-wrap"><table>
              <thead><tr><th>Account</th><th class="num">Debit</th><th class="num">Credit</th></tr></thead>
              <tbody>@for (t of l.trialBalance; track t.account) { <tr><td>{{ t.account }}</td><td class="num">{{ t.debit | money }}</td><td class="num">{{ t.credit | money }}</td></tr> }</tbody>
              <tfoot><tr><th>Total @if (l.totalDebit === l.totalCredit) { <span class="badge ok">balanced</span> } @else { <span class="badge danger">out of balance</span> }</th><th class="num">{{ l.totalDebit | money }}</th><th class="num">{{ l.totalCredit | money }}</th></tr></tfoot>
            </table></div>
          </div>
          <div class="card table-wrap"><h2>Latest postings</h2><table>
            <thead><tr><th>{{ 'Date' | t }}</th><th>Account</th><th>Description</th><th class="num">Debit</th><th class="num">Credit</th></tr></thead>
            <tbody>@for (e of l.entries; track e.id) { <tr><td>{{ e.date | day }}</td><td>{{ e.account }}</td><td class="small">{{ e.description }}</td><td class="num">{{ e.debit ? (e.debit | money) : '' }}</td><td class="num">{{ e.credit ? (e.credit | money) : '' }}</td></tr> }</tbody>
          </table></div>
        }
      }
    }

    @if (sel(); as s) {
      <div class="drawer-backdrop" (click)="sel.set(null)"></div>
      <aside class="drawer" aria-label="Invoice">
        <div class="page-head"><div><h2>{{ s.invoice.number }}</h2><p class="muted">{{ s.invoice.student }} · {{ s.invoice.title }}</p></div><button class="btn small" (click)="sel.set(null)">{{ 'Close' | t }}</button></div>
        <table><tbody>
          @for (l of s.lines; track $index) { <tr><td>{{ l.description }} @if (l.kind !== 'fee') { <span class="badge">{{ l.kind }}</span> }</td><td class="num">{{ l.amount | money: s.invoice.currency }}</td></tr> }
          @if (s.invoice.creditTotal) { <tr><td>Credit notes</td><td class="num">{{ -s.invoice.creditTotal | money: s.invoice.currency }}</td></tr> }
          <tr><th>{{ 'Total' | t }}</th><th class="num">{{ s.invoice.total | money: s.invoice.currency }}</th></tr>
          <tr><td>{{ 'Paid' | t }}</td><td class="num">{{ s.invoice.paidTotal | money: s.invoice.currency }}</td></tr>
          <tr><th>{{ 'Balance' | t }} <span [class]="'badge ' + badge[s.invoice.status]">{{ s.invoice.status }}</span></th><th class="num">{{ s.invoice.balance | money: s.invoice.currency }}</th></tr>
        </tbody></table>
        <p class="small muted">Due {{ s.invoice.dueDate | day }}</p>

        <h3 style="margin-top:16px">Payments</h3>
        @for (p of s.payments; track p.id) {
          <div class="row" style="margin-bottom:6px">
            <span>{{ p.amount | money: s.invoice.currency }} · {{ p.method }} · <span class="badge" [class.ok]="p.status === 'succeeded'" [class.danger]="p.status === 'failed'">{{ p.status }}</span> @if (p.refundedAmount) { <span class="small muted">refunded {{ p.refundedAmount | money: s.invoice.currency }}</span> }</span>
            @if (p.receiptNumber) { <button class="btn small" (click)="receipt(p)">{{ p.receiptNumber }} (PDF)</button> }
            @if (canManage && p.receiptNumber && p.refundedAmount < p.amount) { <button class="btn small danger" (click)="refund(p)">Refund</button> }
          </div>
        } @empty { <p class="small muted">No payments yet.</p> }
        @for (c of s.creditNotes; track c.number) { <div class="small">{{ c.number }}: {{ c.amount | money: s.invoice.currency }} · {{ c.reason }}</div> }

        @if (canManage && s.invoice.balance > 0 && s.invoice.status !== 'carried') {
          <h3 style="margin-top:16px">Record a payment</h3>
          <form class="grid cols-3" (ngSubmit)="pay()">
            <label class="field">{{ 'Amount' | t }}<input type="number" name="a" [(ngModel)]="payment.amount" min="0" step="0.01" required /></label>
            <label class="field">Method<select name="m" [(ngModel)]="payment.method"><option value="cash">Cash</option><option value="cheque">Cheque</option><option value="bank">Bank transfer</option></select></label>
            <label class="field">Reference<input type="text" name="r" [(ngModel)]="payment.reference" /></label>
            <div class="row"><button class="btn primary" type="submit">Record</button><button class="btn" type="button" (click)="payOnline()">Take online payment</button></div>
          </form>
          <h3 style="margin-top:16px">Credit note</h3>
          <form class="grid cols-3" (ngSubmit)="credit()">
            <label class="field">{{ 'Amount' | t }}<input type="number" name="ca" [(ngModel)]="cn.amount" min="0" step="0.01" required /></label>
            <label class="field">Reason<input type="text" name="cr" [(ngModel)]="cn.reason" required /></label>
            <div style="align-self:end"><button class="btn" type="submit">Issue credit note</button></div>
          </form>
        }
      </aside>
    }
  `,
})
export class FeesPage {
  ctx = inject(Ctx);
  private auth = inject(Auth);
  private api = inject(Api);
  private toast = inject(Toast);
  private router = inject(Router);
  badge = INVOICE_BADGE;
  canManage = this.auth.can('fees.manage');
  tabs = ['Invoices', 'Structures', 'Discounts', 'Aging', 'Ledger'];
  tab = signal('Invoices');
  invoices = signal<any[]>([]); structures = signal<any[]>([]); heads = signal<any[]>([]); grades = signal<any[]>([]); discounts = signal<any[]>([]); students = signal<any[]>([]);
  aging = signal<any>(null); structureOpen = signal(false); ledger = signal<any>(null); sel = signal<any>(null); notice = signal('');
  status = ''; payment: any = { method: 'cash' }; cn: any = {};
  fs: any = this.blankStructure();

  headCols: Col[] = [{ key: 'code', label: 'Code', required: true }, { key: 'name', label: 'Name', required: true }];
  discountCols: Col[] = [
    { key: 'name', label: 'Name', required: true },
    { key: 'kind', label: 'Kind', type: 'select', required: true, options: ['discount', 'scholarship', 'sibling'].map((v) => ({ value: v, label: v })) },
    { key: 'type', label: 'Type', type: 'select', required: true, options: [{ value: 'percent', label: 'Percent of fees' }, { value: 'fixed', label: 'Fixed amount per year' }] },
    { key: 'value', label: 'Value', type: 'number' }, { key: 'autoApplySibling', label: 'Automatic for younger siblings', type: 'checkbox' },
  ];
  awardCols = computed<Col[]>(() => [
    { key: 'studentId', label: 'Student', type: 'select', required: true, options: this.students().map((s) => ({ value: s.id, label: `${s.firstName} ${s.lastName} (${s.admissionNumber})` })) },
    { key: 'discountId', label: 'Discount', type: 'select', required: true, options: this.discounts().map((d) => ({ value: d.id, label: d.name })) },
  ]);

  constructor() { effect(() => { this.ctx.campusId(); this.ctx.yearId(); untracked(() => this.load()); }); }
  blankStructure() { return { gradeLevelId: null, name: '', category: '', currency: this.ctx.campus()?.currency ?? '', items: [{ feeHeadId: null, amount: 0 }], installments: [{ name: 'Installment 1', dueDate: '', percent: 100 }] }; }

  async load() {
    const campusId = this.ctx.campusId(), yearId = this.ctx.yearId();
    if (!campusId) return;
    try {
      switch (this.tab()) {
        case 'Invoices': this.invoices.set(await this.api.get('/fees/invoices', { campusId, status: this.status })); break;
        case 'Structures': {
          const [structures, heads, grades] = await Promise.all([yearId ? this.api.get<any[]>('/fees/structures', { academicYearId: yearId }) : [], this.api.get<any[]>('/fee-heads', { campusId }), this.api.get<any[]>('/grade-levels', { campusId })]);
          this.structures.set(structures); this.heads.set(heads); this.grades.set(grades.sort((a, b) => a.order - b.order));
          if (!this.fs.currency) this.fs.currency = this.ctx.campus()?.currency ?? '';
          break;
        }
        case 'Discounts': { const [d, s] = await Promise.all([this.api.get<any[]>('/discounts'), this.api.get<any[]>('/students', { campusId, status: 'active' })]); this.discounts.set(d); this.students.set(s); break; }
        case 'Aging': this.aging.set(await this.api.get('/fees/aging', { campusId })); break;
        case 'Ledger': this.ledger.set(await this.api.get('/fees/ledger', { campusId })); break;
      }
    } catch (e) { this.toast.error(e); }
  }
  async generate() {
    try {
      const r = await this.api.post('/fees/invoices/generate', { academicYearId: this.ctx.yearId() });
      this.notice.set(`${r.created} invoice(s) created, ${r.skipped} already existed.` + (r.warnings.length ? ` Not billed: ${r.warnings.join(' ')}` : ''));
      await this.load();
    } catch (e) { this.toast.error(e); }
  }
  async run(job: 'fines' | 'reminders') {
    try {
      const r = await this.api.post(`/fees/${job}/run`, { campusId: this.ctx.campusId() });
      this.notice.set(job === 'fines' ? `Late fine added to ${r.finesApplied} overdue invoice(s).` : `${r.remindersSent} reminder(s) sent to guardians.`);
      await this.load();
    } catch (e) { this.toast.error(e); }
  }
  async open(id: string) { try { const d = await this.api.get(`/fees/invoices/${id}`); this.payment = { method: 'cash', amount: d.invoice.balance }; this.cn = {}; this.sel.set(d); } catch (e) { this.toast.error(e); } }
  private async act(fn: () => Promise<any>, ok: string) { try { await fn(); this.toast.ok(ok); await this.open(this.sel().invoice.id); await this.load(); } catch (e) { this.toast.error(e); } }
  pay() { return this.act(() => this.api.post(`/fees/invoices/${this.sel().invoice.id}/payments`, this.payment), 'Payment recorded'); }
  credit() { return this.act(() => this.api.post(`/fees/invoices/${this.sel().invoice.id}/credit-notes`, this.cn), 'Credit note issued'); }
  refund(p: any) {
    const amount = Number(prompt('Refund amount', String(p.amount - p.refundedAmount)));
    if (!amount) return;
    const reason = prompt('Reason for refund') ?? '';
    return this.act(() => this.api.post(`/fees/payments/${p.id}/refund`, { amount, reason }), 'Refund recorded');
  }
  async payOnline() { try { const r = await this.api.post(`/fees/invoices/${this.sel().invoice.id}/pay-online`); this.router.navigateByUrl(r.url); } catch (e) { this.toast.error(e); } }
  receipt(p: any) { this.api.open(`/fees/payments/${p.id}/receipt`).catch((e) => this.toast.error(e)); }
  exportLedger() { this.api.open('/fees/ledger/export', { campusId: this.ctx.campusId() }, 'ledger.csv').catch((e) => this.toast.error(e)); }
  async createStructure() {
    try {
      await this.api.post('/fees/structures', { ...this.fs, academicYearId: this.ctx.yearId() });
      this.fs = this.blankStructure(); this.structureOpen.set(false); this.toast.ok('Fee structure created'); await this.load();
    } catch (e) { this.toast.error(e); }
  }
  async deleteStructure(s: any) { if (!confirm(`Delete "${s.name}"?`)) return; try { await this.api.del(`/fees/structures/${s.id}`); await this.load(); } catch (e) { this.toast.error(e); } }
}
