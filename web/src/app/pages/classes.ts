import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Modal } from '../modal';
import { Bar, BarList } from '../charts';
import { Api, Auth, Ctx, DayPipe, Toast } from '../core';

@Component({
  imports: [RouterLink, FormsModule, Modal],
  styles: [`
    .classes { display: grid; grid-template-columns: repeat(auto-fill, minmax(250px, 1fr)); gap: 12px; }
    .klass { display: block; text-decoration: none; color: inherit; }
    .klass:hover { border-color: var(--accent); }
    .meter { height: 8px; background: #e1e0d9; border-radius: 4px; margin: 8px 0 4px; overflow: hidden; }
    .meter span { display: block; height: 100%; background: #2a78d6; border-radius: 0 4px 4px 0; }
    .card-actions { display: flex; gap: 6px; margin-top: 10px; }
  `],
  template: `
    <div class="page-head">
      <div><h1>Classes</h1><p class="muted">{{ rows().length }} classes in {{ ctx.year()?.name }}</p></div>
      @if (canEdit) { <button class="btn primary" (click)="add()">Add class</button> }
    </div>
    <div class="classes">
      @for (c of rows(); track c.id) {
        <a class="card klass" [routerLink]="['/classes', c.id]">
          <h2 style="margin:0">{{ c.grade }} {{ c.name }}</h2>
          <div class="small muted">Class teacher: {{ c.classTeacher ?? 'not assigned' }}</div>
          <div class="meter" role="img" [attr.aria-label]="c.enrolled + ' of ' + c.capacity + ' places filled'"><span [style.width.%]="(c.enrolled / (c.capacity || 1)) * 100"></span></div>
          <div class="row" style="justify-content:space-between">
            <span class="small"><strong>{{ c.enrolled }}</strong> / {{ c.capacity }} students</span>
            <span class="small">{{ c.subjects }} subjects</span>
          </div>
          <div class="small" style="margin-top:6px">Attendance (30 days):
            @if (c.attendancePercent !== null) { <span class="badge" [class.ok]="c.attendancePercent >= 90" [class.warn]="c.attendancePercent < 90">{{ c.attendancePercent }}%</span> } @else { – }
          </div>
          @if (canEdit) {
            <div class="card-actions">
              <button class="btn small" (click)="edit(c, $event)">Edit</button>
              <button class="btn small danger" (click)="remove(c, $event)">Delete</button>
            </div>
          }
        </a>
      } @empty { <p class="empty">No classes in {{ ctx.year()?.name }} yet.@if (canEdit) { Choose Add class to create one. }</p> }
    </div>

    <app-modal [open]="!!form()" [title]="form()?.id ? 'Edit class' : 'Add class to ' + (ctx.year()?.name ?? '')" width="min(560px, 94vw)" (closed)="form.set(null)">
      @if (form(); as f) {
        <form (ngSubmit)="save()">
          <div class="grid cols-2">
            <label class="field">Grade
              <select name="grade" [(ngModel)]="f.gradeLevelId" required>
                @for (g of grades(); track g.id) { <option [ngValue]="g.id">{{ g.name }}</option> }
                @if (!f.id) { <option [ngValue]="NEW">+ New grade…</option> }
              </select>
            </label>
            <label class="field">Section<input type="text" name="name" [(ngModel)]="f.name" required placeholder="e.g. A" /></label>
            @if (f.gradeLevelId === NEW) {
              <label class="field">New grade name<input type="text" name="gname" [(ngModel)]="f.newGrade" required placeholder="e.g. Grade 7" /></label>
              <label class="field">Promotion order<input type="number" name="gorder" [(ngModel)]="f.newGradeOrder" required /></label>
            }
            <label class="field">Capacity<input type="number" name="cap" [(ngModel)]="f.capacity" min="1" required /></label>
            <label class="field">Class teacher
              <select name="ct" [(ngModel)]="f.classTeacherId"><option [ngValue]="null">Not assigned</option>
                @for (s of staff(); track s.id) { <option [ngValue]="s.id">{{ s.firstName }} {{ s.lastName }}</option> }
              </select>
            </label>
          </div>
          <p class="small muted" style="margin-top:12px">Map subjects and teachers to the class on the Timetable screen. Students are placed in it from Admissions, Students or Import.</p>
          @if (error()) { <div class="alert danger">{{ error() }}</div> }
          <div class="form-actions"><button class="btn" type="button" (click)="form.set(null)">Cancel</button><button class="btn primary" type="submit">Save class</button></div>
        </form>
      }
    </app-modal>
  `,
})
export class ClassesPage {
  ctx = inject(Ctx);
  private api = inject(Api);
  private toast = inject(Toast);
  private auth = inject(Auth);
  canEdit = this.auth.can('admin.settings');
  readonly NEW = '__new__';
  rows = signal<any[]>([]); grades = signal<any[]>([]); staff = signal<any[]>([]);
  form = signal<any>(null); error = signal('');

  constructor() {
    effect(() => { this.ctx.campusId(); this.ctx.yearId(); untracked(() => this.load()); });
  }
  async load() {
    const y = this.ctx.yearId(), campusId = this.ctx.campusId();
    if (!y || !campusId) { this.rows.set([]); return; }
    try {
      this.rows.set(await this.api.get('/classes', { academicYearId: y }));
      if (this.canEdit) {
        this.grades.set((await this.api.get<any[]>('/grade-levels', { campusId })).sort((a, b) => a.order - b.order));
        this.staff.set((await this.api.get<any[]>('/staff', { campusId })).filter((s) => s.status === 'active'));
      }
    } catch (e) { this.toast.error(e); }
  }
  add() {
    const top = Math.max(0, ...this.grades().map((g) => g.order));
    this.error.set('');
    this.form.set({ gradeLevelId: this.grades()[0]?.id ?? this.NEW, name: '', capacity: 30, classTeacherId: null, newGrade: '', newGradeOrder: top + 1 });
  }
  edit(c: any, e: Event) {
    e.preventDefault(); e.stopPropagation();
    this.error.set('');
    this.form.set({ id: c.id, gradeLevelId: c.gradeLevelId, name: c.name, capacity: c.capacity, classTeacherId: c.classTeacherId ?? null });
  }
  async save() {
    const f = this.form();
    try {
      let gradeLevelId = f.gradeLevelId;
      if (gradeLevelId === this.NEW) gradeLevelId = (await this.api.post('/grade-levels', { campusId: this.ctx.campusId(), name: f.newGrade.trim(), order: f.newGradeOrder })).id;
      const body = { academicYearId: this.ctx.yearId(), gradeLevelId, name: f.name.trim(), capacity: f.capacity, classTeacherId: f.classTeacherId };
      if (f.id) await this.api.put(`/sections/${f.id}`, body); else await this.api.post('/sections', body);
      this.form.set(null);
      this.toast.ok(f.id ? 'Class updated' : 'Class added');
      await this.load();
    } catch (e: any) { this.error.set(e.message); }
  }
  async remove(c: any, e: Event) {
    e.preventDefault(); e.stopPropagation();
    if (c.enrolled > 0) { this.toast.error(`${c.grade} ${c.name} has ${c.enrolled} students. Move them to another class first.`); return; }
    if (!confirm(`Delete ${c.grade} ${c.name}?`)) return;
    try { await this.api.del(`/sections/${c.id}`); this.toast.ok('Class deleted'); await this.load(); } catch (err) { this.toast.error(err); }
  }
}

@Component({
  imports: [RouterLink, DayPipe, BarList],
  template: `
    @if (d(); as d) {
      <div class="page-head">
        <div>
          <a routerLink="/classes" class="small">← Classes</a>
          <h1>{{ d.section.grade }} {{ d.section.name }}</h1>
          <p class="muted">{{ d.section.year }} · Class teacher:
            @if (d.section.classTeacherId) { <a [routerLink]="['/staff', d.section.classTeacherId]">{{ d.section.classTeacher }}</a> } @else { not assigned }</p>
        </div>
        <div class="row"><a class="btn" routerLink="/attendance">Attendance</a><a class="btn" routerLink="/exams">Exams</a><a class="btn" routerLink="/timetable">Timetable</a></div>
      </div>

      <div class="stats">
        <div class="stat"><div class="label">Students</div><div class="value">{{ d.enrolled }} <span class="muted small">/ {{ d.section.capacity }}</span></div></div>
        <div class="stat"><div class="label">Girls / boys</div><div class="value">{{ d.gender.female }} / {{ d.gender.male }}</div></div>
        <div class="stat"><div class="label">Attendance (30 days)</div><div class="value">{{ d.attendance.percent !== null ? d.attendance.percent + '%' : '–' }}</div></div>
        <div class="stat"><div class="label">Results published</div><div class="value">{{ d.results.published }} <span class="muted small">/ {{ d.results.plans }}</span></div></div>
      </div>

      <div class="grid cols-2">
        <div class="card">
          <h2>Subjects and teachers</h2>
          <table><thead><tr><th>Subject</th><th>Teacher</th><th class="num">Periods / week</th></tr></thead>
            <tbody>@for (s of d.subjects; track s.id) {
              <tr><td>{{ s.subject }}</td><td>@if (s.teacherId) { <a [routerLink]="['/staff', s.teacherId]">{{ s.teacher }}</a> } @else { <span class="muted">not assigned</span> }</td><td class="num">{{ s.periods }}</td></tr>
            } @empty { <tr><td colspan="3" class="empty">No subjects mapped. Add them on the Timetable screen.</td></tr> }</tbody></table>
        </div>
        <div class="card">
          <h2>Attendance by student <span class="muted small">last 30 days</span></h2>
          <bar-list [bars]="attendanceBars()" title="Attendance rate per student over the last 30 days" />
        </div>
      </div>

      <div class="card table-wrap">
        <h2>Students</h2>
        <table><thead><tr><th>Roll</th><th>Name</th><th>Admission no.</th><th>Date of birth</th><th class="num">Attendance</th></tr></thead>
          <tbody>@for (s of d.students; track s.id) {
            <tr class="clickable" [routerLink]="['/students', s.id]"><td>{{ s.rollNumber }}</td><td><a [routerLink]="['/students', s.id]">{{ s.name }}</a></td><td>{{ s.admissionNumber }}</td><td>{{ s.dateOfBirth | day }}</td>
              <td class="num">@if (s.attendancePercent !== null) { <span class="badge" [class.ok]="s.attendancePercent >= 90" [class.warn]="s.attendancePercent < 90 && s.attendancePercent >= 75" [class.danger]="s.attendancePercent < 75">{{ s.attendancePercent }}%</span> } @else { – }</td></tr>
          } @empty { <tr><td colspan="5" class="empty">No students enrolled.</td></tr> }</tbody></table>
      </div>
    }
  `,
})
export class ClassDetailPage {
  auth = inject(Auth);
  private api = inject(Api);
  private toast = inject(Toast);
  d = signal<any>(null);
  attendanceBars = computed<Bar[]>(() => (this.d()?.students ?? []).filter((s: any) => s.attendancePercent !== null)
    .map((s: any) => ({ label: s.name, value: s.attendancePercent, max: 100, note: `${s.name}: ${s.attendancePercent}% attendance` })));
  constructor() {
    inject(ActivatedRoute).paramMap.subscribe(async (p) => {
      try { this.d.set(await this.api.get(`/classes/${p.get('id')}`)); } catch (e) { this.toast.error(e); }
    });
  }
}
