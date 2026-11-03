import { Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, DayPipe, TPipe, Toast } from './core';
import { Modal } from './modal';

export interface Col {
  key: string;
  label: string;
  type?: 'text' | 'number' | 'date' | 'time' | 'select' | 'checkbox';
  options?: { value: any; label: string }[];
  required?: boolean;
  default?: any;
}

/** List, add, edit and delete for a simple configuration entity. One component serves every setup table. */
@Component({
  selector: 'crud-table',
  imports: [FormsModule, TPipe, DayPipe, Modal],
  template: `
    <div class="card">
      <div class="page-head">
        <h2>{{ title() }}</h2>
        @if (canEdit()) { <button class="btn primary small" (click)="start()">{{ 'Add' | t }}</button> }
      </div>
      <app-modal [open]="!!editing()" [title]="(editing()?.id ? 'Edit' : 'Add') + ' · ' + title()" (closed)="editing.set(null)">
      @if (editing(); as row) {
        <form (ngSubmit)="save()"><div class="grid cols-2">
          @for (c of columns(); track c.key) {
            @if (c.type === 'checkbox') {
              <label class="check" style="align-self:end"><input type="checkbox" [(ngModel)]="row[c.key]" [name]="c.key" /> {{ c.label }}</label>
            } @else {
              <label class="field">{{ c.label }}
                @if (c.type === 'select') {
                  <select [(ngModel)]="row[c.key]" [name]="c.key" [required]="!!c.required">
                    @if (!c.required) { <option [ngValue]="null">–</option> }
                    @for (o of c.options ?? []; track o.value) { <option [ngValue]="o.value">{{ o.label }}</option> }
                  </select>
                } @else {
                  <input [type]="c.type ?? 'text'" [(ngModel)]="row[c.key]" [name]="c.key" [required]="!!c.required" step="any" />
                }
              </label>
            }
          }
          </div>
          <div class="form-actions">
            <button class="btn" type="button" (click)="editing.set(null)">{{ 'Cancel' | t }}</button>
            <button class="btn primary" type="submit">{{ 'Save' | t }}</button>
          </div>
        </form>
      }
      </app-modal>
      <div class="table-wrap">
        <table>
          <thead><tr>@for (c of columns(); track c.key) { <th>{{ c.label }}</th> } @if (canEdit()) { <th><span class="sr-only">{{ 'Actions' | t }}</span></th> }</tr></thead>
          <tbody>
            @for (r of rows(); track r.id) {
              <tr>
                @for (c of columns(); track c.key) { <td>{{ c.type === 'date' ? (r[c.key] | day) : display(c, r[c.key]) }}</td> }
                @if (canEdit()) {
                  <td class="nowrap" style="text-align:end">
                    <button class="btn small" (click)="start(r)">{{ 'Edit' | t }}</button>
                    <button class="btn small danger" (click)="remove(r)">{{ 'Delete' | t }}</button>
                  </td>
                }
              </tr>
            } @empty { <tr><td class="empty" [attr.colspan]="columns().length + 1">{{ 'Nothing here yet.' | t }}</td></tr> }
          </tbody>
        </table>
      </div>
    </div>
  `,
})
export class CrudTable {
  private api = inject(Api);
  private toast = inject(Toast);
  title = input.required<string>();
  path = input.required<string>();
  columns = input.required<Col[]>();
  /** Filters the list and is merged into every new row (e.g. { campusId }). */
  scope = input<Record<string, any>>({});
  canEdit = input(true);
  sortBy = input<string | null>(null);
  changed = output<void>();
  rows = signal<any[]>([]);
  editing = signal<any | null>(null);

  constructor() {
    effect(() => { this.path(); this.scope(); untracked(() => this.load()); });
  }

  async load() {
    const scope = this.scope();
    if (Object.values(scope).some((v) => !v)) { this.rows.set([]); return; }
    try {
      const rows = await this.api.get<any[]>(this.path(), scope);
      const key = this.sortBy();
      if (key) rows.sort((a, b) => (a[key] > b[key] ? 1 : a[key] < b[key] ? -1 : 0));
      this.rows.set(rows);
    } catch (e) { this.toast.error(e); }
  }
  display(c: Col, v: any) {
    if (c.type === 'checkbox') return v ? '✓' : '';
    if (c.type === 'select') return c.options?.find((o) => o.value === v)?.label ?? '';
    if (c.type === 'time' && typeof v === 'string') return v.slice(0, 5);
    return v ?? '';
  }
  start(row?: any) {
    const blank: any = {};
    for (const c of this.columns()) blank[c.key] = c.default ?? (c.type === 'checkbox' ? false : c.type === 'number' ? 0 : c.type === 'select' ? (c.required ? c.options?.[0]?.value ?? null : null) : '');
    const r = row ? { ...row } : { ...blank, ...this.scope() };
    for (const c of this.columns()) if (c.type === 'time' && typeof r[c.key] === 'string') r[c.key] = r[c.key].slice(0, 5);
    this.editing.set(r);
  }
  async save() {
    const r = { ...this.editing() };
    for (const c of this.columns()) {
      if (c.type === 'time' && r[c.key]?.length === 5) r[c.key] += ':00';
      if ((c.type === 'date' || c.type === 'select') && r[c.key] === '') r[c.key] = null;
    }
    try {
      if (r.id) await this.api.put(`${this.path()}/${r.id}`, r); else await this.api.post(this.path(), r);
      this.editing.set(null);
      await this.load();
      this.changed.emit();
    } catch (e) { this.toast.error(e); }
  }
  async remove(r: any) {
    if (!confirm('Delete this item?')) return;
    try { await this.api.del(`${this.path()}/${r.id}`); await this.load(); this.changed.emit(); } catch (e) { this.toast.error(e); }
  }
}
