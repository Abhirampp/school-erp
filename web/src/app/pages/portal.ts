import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Api, Auth, DAYS, DayPipe, I18n, MoneyPipe, TPipe, Toast } from '../core';

@Component({
  imports: [FormsModule, DayPipe, MoneyPipe, TPipe],
  template: `
    <div class="page-head"><div><h1>{{ (isParent ? 'My children' : 'Overview') | t }}</h1></div></div>
    @if (children().length > 1) {
      <div class="row" style="margin-bottom:16px" role="group" aria-label="Child">
        @for (c of children(); track c.id) { <button class="btn" [class.primary]="c.id === child()?.id" (click)="pick(c)">{{ c.name }}</button> }
      </div>
    }
    @if (child(); as c) {
      <p class="muted">{{ c.name }} @if (c.nativeName) { <span dir="auto">· {{ c.nativeName }}</span> } · {{ c.admissionNumber }} · {{ c.section ?? c.status }} · {{ c.campus }}</p>
      <div class="tabs" role="tablist">@for (t of tabs; track t) { <button role="tab" [attr.aria-selected]="tab() === t" (click)="setTab(t)">{{ t | t }}</button> }</div>

      @if (o(); as o) {
        @switch (tab()) {
          @case ('Overview') {
            <div class="stats">
              <div class="stat"><div class="label">{{ 'Attendance rate' | t }}</div><div class="value">{{ o.attendance.percent !== null ? o.attendance.percent + '%' : '–' }}</div></div>
              <div class="stat"><div class="label">{{ 'Present' | t }}</div><div class="value">{{ o.attendance.present }}</div></div>
              <div class="stat"><div class="label">{{ 'Late' | t }}</div><div class="value">{{ o.attendance.late }}</div></div>
              <div class="stat"><div class="label">{{ 'Absent' | t }}</div><div class="value">{{ o.attendance.authorized + o.attendance.unauthorized }}</div></div>
            </div>
            <div class="card">
              <h2>{{ 'Recent absences' | t }}</h2>
              @for (a of o.recentAbsences; track $index) { <div class="row"><span>{{ a.date | day }}</span><span class="badge" [class.danger]="a.category === 'unauthorized'" [class.warn]="a.category === 'late'">{{ a.category }}</span><span class="muted small">{{ a.remarks }}</span></div> }
              @empty { <p class="muted">{{ 'No absences recorded.' | t }}</p> }
            </div>
            <div class="card table-wrap">
              <h2>{{ 'Timetable' | t }}</h2>
              <table class="tt">
                <thead><tr><th style="width:100px">{{ 'Period' | t }}</th>@for (d of days(); track d) { <th>{{ dayNames[d - 1] | t }}</th> }</tr></thead>
                <tbody>@for (p of o.periods; track p.id) {
                  <tr><th scope="row">{{ p.name }}<div class="small muted">{{ p.startTime.slice(0, 5) }}</div></th>
                    @if (p.isBreak) { <td class="break" [attr.colspan]="days().length"></td> }
                    @else { @for (d of days(); track d) { @let s = slot(d, p.id); <td>@if (s) { <strong>{{ s.subject }}</strong><div class="small muted">{{ s.teacher }}</div> }</td> } }
                  </tr>
                }</tbody>
              </table>
            </div>
          }
          @case ('Results') {
            <div class="card row">
              <label class="field">{{ 'Term' | t }}<select [(ngModel)]="termId" (ngModelChange)="loadCard()">@for (t of o.terms; track t.id) { <option [ngValue]="t.id">{{ t.name }}</option> }</select></label>
              @if (card()) { <button class="btn" style="align-self:end" (click)="cardPdf()">{{ 'Download PDF' | t }}</button> }
            </div>
            @if (card(); as rc) {
              <div class="card table-wrap">
                <table>
                  <thead><tr><th>{{ 'Subject' | t }}</th><th>{{ 'Teacher' | t }}</th><th class="num">%</th><th>{{ 'Grade' | t }}</th><th></th></tr></thead>
                  <tbody>@for (s of rc.subjects; track s.subject) { <tr><td>{{ s.subject }}</td><td class="muted">{{ s.teacher }}</td><td class="num">{{ s.percent ?? '–' }}</td><td><strong>{{ s.grade ?? '–' }}</strong></td><td class="small muted">{{ s.descriptor }}</td></tr> }</tbody>
                  <tfoot><tr><th colspan="2">{{ 'Overall' | t }}</th><th class="num">{{ rc.overallPercent ?? '–' }}</th><th colspan="2">@if (rc.gpa !== null) { GPA {{ rc.gpa }} }</th></tr></tfoot>
                </table>
              </div>
            } @else { <p class="empty">{{ 'Results have not been published for this term.' | t }}</p> }
          }
          @case ('Fees') {
            <div class="card table-wrap"><table>
              <thead><tr><th>{{ 'Invoice' | t }}</th><th>{{ 'Due' | t }}</th><th class="num">{{ 'Total' | t }}</th><th class="num">{{ 'Balance' | t }}</th><th></th></tr></thead>
              <tbody>@for (i of o.invoices; track i.id) {
                <tr><td>{{ i.title }}<div class="small muted">{{ i.number }}</div></td><td>{{ i.dueDate | day }}</td><td class="num">{{ i.total | money: i.currency }}</td><td class="num">{{ i.balance | money: i.currency }}</td>
                  <td class="nowrap" style="text-align:end">
                    @if (i.balance > 0) { <button class="btn small primary" (click)="pay(i)">{{ 'Pay now' | t }}</button> } @else { <span class="badge ok">{{ 'Paid' | t }}</span> }
                    <button class="btn small" (click)="receipts(i)">{{ 'Receipt' | t }}</button>
                  </td></tr>
              } @empty { <tr><td colspan="5" class="empty">{{ 'Fees are up to date.' | t }}</td></tr> }</tbody>
            </table></div>
            @if (detail(); as d) {
              <div class="card"><h2>{{ d.invoice.number }}</h2>
                @for (l of d.lines; track $index) { <div class="row" style="justify-content:space-between"><span>{{ l.description }}</span><span class="num">{{ l.amount | money: d.invoice.currency }}</span></div> }
                <h3 style="margin-top:12px">{{ 'Receipt' | t }}</h3>
                @for (p of d.payments; track p.id) { @if (p.receiptNumber) { <button class="btn small" (click)="receiptPdf(p)">{{ p.receiptNumber }} · {{ p.amount | money: d.invoice.currency }}</button> } } @empty { <p class="muted small">–</p> }
              </div>
            }
          }
          @case ('Consent') {
            @for (f of myConsents(); track f.formId) {
              <div class="card">
                <div class="page-head"><div><h2>{{ f.title }}</h2><p class="small muted">{{ f.purpose }}@if (f.dueDate) { · {{ f.dueDate | day }} }</p></div>
                  <span class="badge" [class.ok]="f.decision === 'granted'" [class.danger]="f.decision === 'declined'" [class.warn]="!f.decision">{{ (f.decision === 'granted' ? 'Granted' : f.decision === 'declined' ? 'Declined' : f.decision === 'withdrawn' ? 'Withdrawn' : 'Awaiting your response') | t }}</span></div>
                <p>{{ f.body }}</p>
                @if (f.decidedAt) { <p class="small muted">{{ f.decidedAt | day: true }}</p> }
                <div class="row">
                  @if (f.decision !== 'granted') { <button class="btn primary" (click)="decide(f, 'granted')">{{ 'I give consent' | t }}</button> }
                  @if (!f.decision || f.decision === 'withdrawn') { <button class="btn" (click)="decide(f, 'declined')">{{ 'I do not consent' | t }}</button> }
                  @if (f.decision === 'granted') { <button class="btn danger" (click)="withdraw(f)">{{ 'Withdraw consent' | t }}</button> }
                </div>
              </div>
            } @empty { <p class="empty">{{ 'Nothing here yet.' | t }}</p> }
          }
        }
      }
    } @else { <p class="empty">{{ 'Nothing here yet.' | t }}</p> }
  `,
})
export class PortalPage {
  private api = inject(Api);
  private auth = inject(Auth);
  private i18n = inject(I18n);
  private toast = inject(Toast);
  private router = inject(Router);
  isParent = this.auth.can('portal.parent');
  tabs = this.isParent ? ['Overview', 'Results', 'Fees', 'Consent'] : ['Overview', 'Results'];
  dayNames = DAYS;
  tab = signal('Overview');
  children = signal<any[]>([]); child = signal<any>(null); o = signal<any>(null); card = signal<any>(null); detail = signal<any>(null); consents = signal<any[]>([]);
  termId: string | null = null;
  myConsents = () => this.consents().filter((c) => c.studentId === this.child()?.id);
  days = () => { const used = new Set<number>((this.o()?.timetable ?? []).map((s: any) => s.day)); return used.has(7) ? [7, 1, 2, 3, 4] : used.has(6) ? [1, 2, 3, 4, 5, 6] : [1, 2, 3, 4, 5]; };
  slot(day: number, periodId: string) { return this.o()?.timetable.find((s: any) => s.day === day && s.periodId === periodId); }

  constructor() { this.init(); }
  async init() {
    try {
      const kids = await this.api.get<any[]>('/portal/children');
      this.children.set(kids);
      if (kids.length) await this.pick(kids[0]);
      if (this.isParent) this.consents.set(await this.api.get('/portal/consents'));
    } catch (e) { this.toast.error(e); }
  }
  async pick(c: any) {
    this.child.set(c); this.card.set(null); this.detail.set(null);
    try {
      const o = await this.api.get(`/portal/students/${c.id}/overview`);
      this.o.set(o);
      this.termId = (o.terms.filter((t: any) => t.published).pop() ?? o.terms[0])?.id ?? null;
      if (this.tab() === 'Results') await this.loadCard();
    } catch (e) { this.toast.error(e); }
  }
  setTab(t: string) { this.tab.set(t); if (t === 'Results') this.loadCard(); }
  async loadCard() {
    this.card.set(null);
    if (!this.termId) return;
    try { this.card.set(await this.api.get(`/portal/students/${this.child().id}/report-card`, { termId: this.termId })); } catch { /* not published yet */ }
  }
  cardPdf() { this.api.open(`/portal/students/${this.child().id}/report-card`, { termId: this.termId, format: 'pdf', lang: this.i18n.lang() }).catch((e) => this.toast.error(e)); }
  async pay(i: any) { try { const r = await this.api.post(`/portal/invoices/${i.id}/pay`); this.router.navigateByUrl(r.url); } catch (e) { this.toast.error(e); } }
  async receipts(i: any) { try { this.detail.set(await this.api.get(`/portal/invoices/${i.id}`)); } catch (e) { this.toast.error(e); } }
  receiptPdf(p: any) { this.api.open(`/portal/payments/${p.id}/receipt`).catch((e) => this.toast.error(e)); }
  async decide(f: any, decision: string) {
    try { await this.api.post('/portal/consents', { consentFormId: f.formId, studentId: f.studentId, decision }); this.consents.set(await this.api.get('/portal/consents')); } catch (e) { this.toast.error(e); }
  }
  async withdraw(f: any) { try { await this.api.post(`/portal/consents/${f.consentId}/withdraw`); this.consents.set(await this.api.get('/portal/consents')); } catch (e) { this.toast.error(e); } }
}
