import { Component, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, Auth, Ctx, DayPipe, MoneyPipe, Toast } from '../core';
import { Modal } from '../modal';

const STATUS: Record<string, string> = { draft: 'warn', approved: 'info', paid: 'ok' };

@Component({
  imports: [FormsModule, RouterLink, DayPipe, MoneyPipe, Modal],
  template: `
    <div class="page-head">
      <div><h1>Payroll</h1><p class="muted">Monthly salary runs: calculate, approve (books the cost to the ledger), then mark paid.</p></div>
      @if (canManage && tab() === 'Payroll runs') { <button class="btn primary" (click)="newRun()">Run payroll</button> }
    </div>
    <div class="tabs" role="tablist">@for (t of tabs; track t) { <button role="tab" [attr.aria-selected]="tab() === t" (click)="tab.set(t); load()">{{ t }}</button> }</div>

    @switch (tab()) {
      @case ('Payroll runs') {
        @if (run(); as r) {
          <div class="card">
            <div class="page-head">
              <div><h2>{{ monthName(r.run.month) }} <span [class]="'badge ' + status[r.run.status]">{{ r.run.status }}</span></h2>
                <p class="small muted">{{ r.run.staffCount }} staff @if (r.run.approvedByName) { · approved by {{ r.run.approvedByName }} {{ r.run.approvedAt | day }} } @if (r.run.paidAt) { · paid {{ r.run.paidAt | day }} }</p></div>
              <div class="row">
                <button class="btn" (click)="run.set(null)">Back</button>
                @if (r.run.status === 'draft') {
                  <button class="btn" (click)="act('recalculate')">Recalculate</button>
                  <button class="btn danger" (click)="remove()">Delete</button>
                  <button class="btn primary" (click)="act('approve')">Approve</button>
                }
                @if (r.run.status === 'approved') { <button class="btn primary" (click)="act('pay')">Mark as paid</button> }
                <button class="btn" (click)="bankFile()">Bank file (CSV)</button>
              </div>
            </div>
            <div class="stats">
              <div class="stat"><div class="label">Gross pay</div><div class="value">{{ r.run.gross | money: r.run.currency }}</div></div>
              <div class="stat"><div class="label">Deductions</div><div class="value">{{ r.run.deductions | money: r.run.currency }}</div></div>
              <div class="stat"><div class="label">Net pay</div><div class="value">{{ r.run.net | money: r.run.currency }}</div></div>
            </div>
            @if (r.run.status === 'draft') { <div class="alert">Draft. Staff cannot see these payslips until the run is approved. Recalculate after changing a salary structure or approving unpaid leave.</div> }
            <div class="table-wrap"><table>
              <thead><tr><th>Employee</th><th class="num">Gross</th><th>Deductions</th><th class="num">Unpaid days</th><th class="num">Net pay</th><th></th></tr></thead>
              <tbody>@for (p of r.payslips; track p.id) {
                <tr><td><a [routerLink]="['/staff', p.staffId]">{{ p.staffName }}</a><div class="small muted">{{ p.employeeNumber }} · {{ p.designation }}</div></td>
                  <td class="num">{{ p.gross | money: r.run.currency }}</td>
                  <td class="small">@for (l of deductionLines(p); track l.name) { <div>{{ l.name }}: {{ l.amount | money: r.run.currency }}</div> }</td>
                  <td class="num">{{ p.unpaidLeaveDays || '–' }}</td>
                  <td class="num"><strong>{{ p.net | money: r.run.currency }}</strong></td>
                  <td style="text-align:end"><button class="btn small" (click)="pdf(p.id)">Payslip</button></td></tr>
              }</tbody>
            </table></div>
          </div>
        } @else {
          <div class="card table-wrap"><table>
            <thead><tr><th>Month</th><th>Status</th><th class="num">Staff</th><th class="num">Gross</th><th class="num">Deductions</th><th class="num">Net pay</th></tr></thead>
            <tbody>@for (r of runs(); track r.id) {
              <tr class="clickable" tabindex="0" (click)="open(r.id)" (keydown.enter)="open(r.id)"><td><strong>{{ monthName(r.month) }}</strong></td><td><span [class]="'badge ' + status[r.status]">{{ r.status }}</span></td>
                <td class="num">{{ r.staffCount }}</td><td class="num">{{ r.gross | money: r.currency }}</td><td class="num">{{ r.deductions | money: r.currency }}</td><td class="num"><strong>{{ r.net | money: r.currency }}</strong></td></tr>
            } @empty { <tr><td colspan="6" class="empty">No payroll runs yet. Choose Run payroll.</td></tr> }</tbody>
          </table></div>
          <p class="small muted">Set each person's salary structure and bank details on their page under Staff.</p>
        }
      }
      @case ('My payslips') {
        <div class="card table-wrap"><table>
          <thead><tr><th>Month</th><th class="num">Gross</th><th class="num">Deductions</th><th class="num">Net pay</th><th>Status</th><th></th></tr></thead>
          <tbody>@for (m of mine(); track m.slip.id) {
            <tr><td>{{ monthName(m.slip.month) }}</td><td class="num">{{ m.slip.gross | money: m.currency }}</td><td class="num">{{ m.slip.deductions | money: m.currency }}</td><td class="num"><strong>{{ m.slip.net | money: m.currency }}</strong></td>
              <td><span [class]="'badge ' + status[m.status]">{{ m.status }}</span></td><td style="text-align:end"><button class="btn small" (click)="pdf(m.slip.id)">Download PDF</button></td></tr>
          } @empty { <tr><td colspan="6" class="empty">No payslips released to you yet.</td></tr> }</tbody>
        </table></div>
      }
    }

    <app-modal [open]="creating()" title="Run payroll" width="min(460px, 94vw)" (closed)="creating.set(false)">
      @if (creating()) {
        <form (ngSubmit)="create()">
          <label class="field">Month<input type="month" name="m" [(ngModel)]="month" required /></label>
          <p class="small muted" style="margin-top:10px">Creates a draft payslip for every active staff member at {{ ctx.campus()?.name }} who has a salary structure, deducting approved unpaid leave in that month.</p>
          <div class="form-actions"><button class="btn" type="button" (click)="creating.set(false)">Cancel</button><button class="btn primary" type="submit">Calculate</button></div>
        </form>
      }
    </app-modal>
  `,
})
export class PayrollPage {
  ctx = inject(Ctx);
  private auth = inject(Auth);
  private api = inject(Api);
  private toast = inject(Toast);
  status = STATUS;
  canManage = this.auth.can('payroll.manage');
  tabs = [...(this.canManage ? ['Payroll runs'] : []), ...(this.auth.user()?.staffId ? ['My payslips'] : [])];
  tab = signal(this.tabs[0]);
  runs = signal<any[]>([]); run = signal<any>(null); mine = signal<any[]>([]); creating = signal(false);
  month = '';

  constructor() { effect(() => { this.ctx.campusId(); untracked(() => { this.run.set(null); this.load(); }); }); }
  monthName(m: string) { return new Date(m + '-01T00:00:00Z').toLocaleDateString(undefined, { month: 'long', year: 'numeric', timeZone: 'UTC' }); }
  deductionLines(p: any) { return (p.lines ?? []).filter((l: any) => l.kind === 'deduction'); }
  async load() {
    try {
      if (this.tab() === 'My payslips') this.mine.set(await this.api.get('/payroll/my-payslips'));
      else if (this.ctx.campusId()) this.runs.set(await this.api.get('/payroll/runs', { campusId: this.ctx.campusId() }));
    } catch (e) { this.toast.error(e); }
  }
  newRun() { const d = new Date(); this.month = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`; this.creating.set(true); }
  async create() {
    try { const r = await this.api.post('/payroll/runs', { campusId: this.ctx.campusId(), month: this.month }); this.creating.set(false); await this.load(); await this.open(r.id); } catch (e) { this.toast.error(e); }
  }
  async open(id: string) { try { this.run.set(await this.api.get(`/payroll/runs/${id}`)); } catch (e) { this.toast.error(e); } }
  async act(action: 'recalculate' | 'approve' | 'pay') {
    const msg = { approve: 'Approve this payroll? Salaries are booked to the ledger and staff can see their payslips.', pay: 'Mark every salary in this run as paid from the bank?', recalculate: '' }[action];
    if (msg && !confirm(msg)) return;
    try {
      await this.api.post(`/payroll/runs/${this.run().run.id}/${action}`);
      this.toast.ok({ recalculate: 'Recalculated', approve: 'Approved; staff notified', pay: 'Marked as paid' }[action]);
      await this.open(this.run().run.id); await this.load();
    } catch (e) { this.toast.error(e); }
  }
  async remove() {
    if (!confirm('Delete this draft payroll run?')) return;
    try { await this.api.del(`/payroll/runs/${this.run().run.id}`); this.run.set(null); await this.load(); } catch (e) { this.toast.error(e); }
  }
  pdf(id: string) { this.api.open(`/payroll/payslips/${id}/pdf`).catch((e) => this.toast.error(e)); }
  bankFile() { this.api.open(`/payroll/runs/${this.run().run.id}/bank-file`, undefined, `salaries-${this.run().run.month}.csv`).catch((e) => this.toast.error(e)); }
}
