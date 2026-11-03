import { Component, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AssistantPanel } from './assistant';
import { Api, Auth, Ctx, DayPipe, I18n, TPipe, Toast } from './core';

const NAV = [
  { path: '/dashboard', label: 'Dashboard', staff: true },
  { path: '/admissions', label: 'Admissions', perms: ['admissions.manage'] },
  { path: '/students', label: 'Students', perms: ['students.read'] },
  { path: '/staff', label: 'Staff', staff: true },
  { path: '/classes', label: 'Classes', staff: true },
  { path: '/timetable', label: 'Timetable', perms: ['timetable.read', 'academics.manage'] },
  { path: '/attendance', label: 'Attendance', perms: ['attendance.mark', 'attendance.read', 'staff.leave.approve'] },
  { path: '/exams', label: 'Exams', perms: ['exams.manage', 'exams.marks', 'exams.approve'] },
  { path: '/fees', label: 'Fees', perms: ['fees.read', 'fees.manage'] },
  { path: '/payroll', label: 'Payroll', payroll: true },
  { path: '/communication', label: 'Communication', perms: ['comms.announce', 'consent.manage'] },
  { path: '/portal', label: 'My children', perms: ['portal.parent', 'portal.student'] },
  { path: '/messages', label: 'Messages', perms: ['comms.message'] },
  { path: '/setup', label: 'School setup', perms: ['admin.settings'] },
  { path: '/users', label: 'Users & roles', perms: ['admin.users'] },
  { path: '/import', label: 'Import', perms: ['admin.import'] },
  { path: '/security', label: 'Security & audit', staff: true },
  { path: '/rollover', label: 'Year-end rollover', perms: ['rollover.run'] },
  { path: '/platform', label: 'Platform', platform: true },
];

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, TPipe, DayPipe, AssistantPanel],
  template: `
    @if (bare()) {
      <router-outlet />
    } @else {
      <a class="skip" href="#main">{{ 'Skip to content' | t }}</a>
      <div class="shell">
        <nav class="sidebar" [class.open]="menuOpen()" aria-label="Main">
          <div class="brand">Global School ERP<small>{{ auth.user()?.role }}</small></div>
          <div class="nav">
            @for (item of nav(); track item.path) {
              <a [routerLink]="item.path" routerLinkActive="active" (click)="menuOpen.set(false)">{{ item.label | t }}</a>
            }
          </div>
        </nav>
        <div class="main">
          @if (auth.support(); as s) {
            <div class="banner" role="status">
              <span><strong>{{ 'Read-only support session' | t }}</strong> · {{ s.tenant }} · expires {{ s.expiresAt | day: true }}</span>
              <button class="btn small" (click)="endSupport()">{{ 'End session' | t }}</button>
            </div>
          }
          <header class="topbar">
            <button class="btn menu-toggle" (click)="menuOpen.set(!menuOpen())" [attr.aria-expanded]="menuOpen()">{{ 'Menu' | t }}</button>
            @if (ctx.campuses().length) {
              <label class="sr-only" for="campus">{{ 'Campus' | t }}</label>
              <select id="campus" style="width:auto" [value]="ctx.campusId()" (change)="ctx.setCampus($any($event.target).value)">
                @for (c of ctx.campuses(); track c.id) { <option [value]="c.id" [selected]="c.id === ctx.campusId()">{{ c.name }}</option> }
              </select>
              <label class="sr-only" for="year">{{ 'Academic year' | t }}</label>
              <select id="year" style="width:auto" [value]="ctx.yearId()" (change)="ctx.setYear($any($event.target).value)">
                @for (y of ctx.years(); track y.id) { <option [value]="y.id" [selected]="y.id === ctx.yearId()">{{ y.name }} ({{ y.status }})</option> }
              </select>
            }
            <span class="spacer"></span>
            <label class="sr-only" for="lang">{{ 'Language' | t }}</label>
            <select id="lang" style="width:auto" (change)="i18n.set($any($event.target).value)">
              @for (l of i18n.languages; track l.code) { <option [value]="l.code" [selected]="l.code === i18n.lang()">{{ l.name }}</option> }
            </select>
            <button class="btn" (click)="toggleBell()" [attr.aria-expanded]="bellOpen()">
              {{ 'Notifications' | t }} @if (unread()) { <span class="badge danger">{{ unread() }}</span> }
            </button>
            <span class="muted small">{{ auth.user()?.displayName }}</span>
            <button class="btn" (click)="signOut()">{{ 'Sign out' | t }}</button>
          </header>
          @if (bellOpen()) {
            <div class="drawer-backdrop" (click)="bellOpen.set(false)"></div>
            <aside class="drawer" aria-label="Notifications">
              <div class="page-head"><h2>{{ 'Notifications' | t }}</h2><button class="btn small" (click)="bellOpen.set(false)">{{ 'Close' | t }}</button></div>
              @for (n of notifications(); track n.id) {
                <div class="card"><strong>{{ n.subject }}</strong><div>{{ n.body }}</div><div class="muted small">{{ n.createdAt | day: true }}</div></div>
              } @empty { <p class="empty">{{ 'No notifications yet.' | t }}</p> }
            </aside>
          }
          <main class="content" id="main" tabindex="-1"><router-outlet /></main>
          @if (!auth.isPortal() && !auth.isPlatform() && !auth.support()) { <app-assistant /> }
        </div>
      </div>
    }
    @if (toast.message(); as m) { <div class="toast" [class.error]="m.kind === 'error'" role="alert">{{ m.text }}</div> }
  `,
})
export class App {
  auth = inject(Auth);
  ctx = inject(Ctx);
  i18n = inject(I18n);
  toast = inject(Toast);
  private api = inject(Api);
  private router = inject(Router);

  menuOpen = signal(false);
  bellOpen = signal(false);
  notifications = signal<any[]>([]);
  unread = computed(() => this.notifications().filter((n) => !n.readAt).length);
  private url = signal('/');
  /** Sign-in, the public application form and the payment page have no app chrome. */
  bare = computed(() => !this.auth.token() || ['/login', '/apply/', '/pay/'].some((p) => this.url().startsWith(p)));
  nav = computed(() => {
    const a = this.auth;
    if (a.isPlatform()) return NAV.filter((n) => n.platform);
    return NAV.filter((n) => (n.staff ? !a.isPortal() : n.platform ? false : n.payroll ? !a.support() && (a.can('payroll.manage') || !!a.user()?.staffId) : a.can(...(n.perms ?? []))));
  });

  constructor() {
    this.router.events.subscribe((e) => { if (e instanceof NavigationEnd) this.url.set(e.urlAfterRedirects); });
    this.refresh();
  }

  async refresh() {
    if (!this.auth.token()) return;
    try {
      await this.ctx.load();
      if (!this.auth.isPlatform() && !this.auth.support()) this.notifications.set(await this.api.get('/notifications'));
    } catch (e) { this.toast.error(e); }
  }
  async toggleBell() {
    this.bellOpen.set(!this.bellOpen());
    if (!this.bellOpen() || this.auth.support()) return;
    try {
      this.notifications.set(await this.api.get('/notifications'));
      if (this.unread()) { await this.api.post('/notifications/read'); }
    } catch (e) { this.toast.error(e); }
  }
  endSupport() { this.auth.endSupport(); this.ctx.campuses.set([]); this.router.navigateByUrl('/platform'); }
  signOut() { this.auth.signOut(); this.ctx.campuses.set([]); this.router.navigateByUrl('/login'); }
}
