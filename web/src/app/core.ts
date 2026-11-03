import { Injectable, Pipe, PipeTransform, computed, effect, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { TRANSLATIONS } from './i18n';

function stored<T>(key: string, fallback: T): T {
  try {
    const raw = sessionStorage.getItem(key) ?? localStorage.getItem(key);
    return raw === null ? fallback : (JSON.parse(raw) as T);
  } catch {
    return fallback;
  }
}

@Injectable({ providedIn: 'root' })
export class Toast {
  readonly message = signal<{ text: string; kind: 'ok' | 'error' } | null>(null);
  private timer: any;
  show(text: string, kind: 'ok' | 'error' = 'ok') {
    this.message.set({ text, kind });
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.message.set(null), kind === 'error' ? 7000 : 3500);
  }
  ok(text: string) { this.show(text, 'ok'); }
  error(e: unknown) { this.show(e instanceof Error ? e.message : String(e), 'error'); }
}

@Injectable({ providedIn: 'root' })
export class Auth {
  // The token lives in sessionStorage: it is gone when the tab closes and is never sent automatically by the browser.
  readonly token = signal<string | null>(stored('erp.token', null));
  readonly user = signal<any>(stored('erp.user', null));
  /** Set while a platform operator is inside a school on an approved, read-only support session. */
  readonly support = signal<any>(stored('erp.support', null));
  readonly isPortal = computed(() => ['children', 'self'].includes(this.user()?.scope));
  readonly isPlatform = computed(() => !!this.user()?.isPlatformAdmin && !this.support());

  can(...perms: string[]) {
    const mine: string[] = this.support()?.permissions ?? this.user()?.permissions ?? [];
    return perms.some((p) => mine.includes(p));
  }
  signIn(session: any) {
    sessionStorage.setItem('erp.token', JSON.stringify(session.token));
    sessionStorage.setItem('erp.user', JSON.stringify(session.user));
    this.token.set(session.token);
    this.user.set(session.user);
  }
  startSupport(session: any, platformToken: string) {
    const s = { ...session, platformToken };
    sessionStorage.setItem('erp.support', JSON.stringify(s));
    sessionStorage.setItem('erp.token', JSON.stringify(session.token));
    this.support.set(s);
    this.token.set(session.token);
  }
  endSupport() {
    const s = this.support();
    if (!s) return;
    sessionStorage.removeItem('erp.support');
    sessionStorage.setItem('erp.token', JSON.stringify(s.platformToken));
    this.support.set(null);
    this.token.set(s.platformToken);
  }
  signOut() {
    ['erp.token', 'erp.user', 'erp.support'].forEach((k) => sessionStorage.removeItem(k));
    this.token.set(null);
    this.user.set(null);
    this.support.set(null);
  }
}

@Injectable({ providedIn: 'root' })
export class Api {
  private auth = inject(Auth);
  private router = inject(Router);

  private async send(method: string, path: string, body?: BodyInit | null, contentType?: string): Promise<Response> {
    const headers: Record<string, string> = {};
    if (contentType) headers['Content-Type'] = contentType;
    const token = this.auth.token();
    if (token) headers['Authorization'] = `Bearer ${token}`;
    let res: Response;
    try {
      res = await fetch('/api' + path, { method, headers, body });
    } catch {
      throw new NetworkError();
    }
    if (res.status === 401 && token) {
      this.auth.signOut();
      this.router.navigateByUrl('/login');
      throw new Error('Your session has ended. Please sign in again.');
    }
    if (!res.ok) {
      let detail = res.status === 403 ? 'You do not have permission to do this.' : res.status === 429 ? 'Too many requests. Wait a minute and try again.' : `Request failed (${res.status}).`;
      try {
        const problem = await res.json();
        detail = problem.detail ?? problem.title ?? detail;
      } catch { /* no body */ }
      throw new Error(detail);
    }
    return res;
  }

  private async json<T>(res: Response): Promise<T> {
    const text = await res.text();
    return (text ? JSON.parse(text) : null) as T;
  }
  private qs(params?: Record<string, any>) {
    const entries = Object.entries(params ?? {}).filter(([, v]) => v !== null && v !== undefined && v !== '');
    return entries.length ? '?' + entries.map(([k, v]) => `${k}=${encodeURIComponent(v)}`).join('&') : '';
  }

  async get<T = any>(path: string, params?: Record<string, any>) { return this.json<T>(await this.send('GET', path + this.qs(params))); }
  async post<T = any>(path: string, body?: any) { return this.json<T>(await this.send('POST', path, JSON.stringify(body ?? {}), 'application/json')); }
  async put<T = any>(path: string, body?: any) { return this.json<T>(await this.send('PUT', path, JSON.stringify(body ?? {}), 'application/json')); }
  async del(path: string) { await this.send('DELETE', path); }
  async postText<T = any>(path: string, text: string, type = 'text/csv') { return this.json<T>(await this.send('POST', path, text, type)); }
  async upload<T = any>(path: string, form: FormData) { return this.json<T>(await this.send('POST', path, form)); }

  /** Downloads a protected file (PDF, CSV) with the bearer token and opens or saves it. */
  async open(path: string, params?: Record<string, any>, saveAs?: string) {
    const res = await this.send('GET', path + this.qs(params));
    const url = URL.createObjectURL(await res.blob());
    const a = document.createElement('a');
    a.href = url;
    if (saveAs) a.download = saveAs; else a.target = '_blank';
    a.rel = 'noopener';
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 60_000);
  }
}

export class NetworkError extends Error {
  constructor() { super('You appear to be offline. Check your connection and try again.'); }
}

/** The campus and academic year the staff user is working in. */
@Injectable({ providedIn: 'root' })
export class Ctx {
  private api = inject(Api);
  private auth = inject(Auth);
  readonly campuses = signal<any[]>([]);
  readonly years = signal<any[]>([]);
  readonly campusId = signal<string | null>(stored('erp.campus', null));
  readonly yearId = signal<string | null>(stored('erp.year', null));
  readonly campus = computed(() => this.campuses().find((c) => c.id === this.campusId()) ?? null);
  readonly year = computed(() => this.years().find((y) => y.id === this.yearId()) ?? null);
  readonly ready = signal(false);

  async load() {
    if (!this.auth.token() || this.auth.isPortal() || this.auth.isPlatform()) return;
    const mine = this.auth.support() ? null : this.auth.user()?.campusId;
    const campuses = (await this.api.get<any[]>('/campuses')).filter((c) => !mine || c.id === mine);
    this.campuses.set(campuses);
    if (!campuses.some((c) => c.id === this.campusId())) this.campusId.set(campuses[0]?.id ?? null);
    await this.loadYears();
    this.ready.set(true);
  }
  async loadYears() {
    const id = this.campusId();
    const years = id ? await this.api.get<any[]>('/academic-years', { campusId: id }) : [];
    years.sort((a, b) => b.startDate.localeCompare(a.startDate));
    this.years.set(years);
    if (!years.some((y) => y.id === this.yearId())) this.yearId.set((years.find((y) => y.status === 'active') ?? years[0])?.id ?? null);
  }
  async setCampus(id: string) {
    this.campusId.set(id);
    localStorage.setItem('erp.campus', JSON.stringify(id));
    await this.loadYears();
  }
  setYear(id: string) {
    this.yearId.set(id);
    localStorage.setItem('erp.year', JSON.stringify(id));
  }
}

@Injectable({ providedIn: 'root' })
export class I18n {
  readonly languages = [{ code: 'en', name: 'English' }, { code: 'ar', name: 'العربية' }, { code: 'hi', name: 'हिन्दी' }];
  readonly lang = signal<string>(stored('erp.lang', 'en'));
  readonly dir = computed(() => (this.lang() === 'ar' ? 'rtl' : 'ltr'));

  constructor() {
    effect(() => {
      document.documentElement.lang = this.lang();
      document.documentElement.dir = this.dir();
    });
  }
  set(code: string) {
    this.lang.set(code);
    localStorage.setItem('erp.lang', JSON.stringify(code));
  }
  /** English text is the key; a missing translation falls back to English. */
  t(key: string): string {
    return TRANSLATIONS[this.lang()]?.[key] ?? key;
  }
}

@Pipe({ name: 't', pure: false })
export class TPipe implements PipeTransform {
  private i18n = inject(I18n);
  transform(key: string) { return this.i18n.t(key); }
}

/** Numbers, money and dates follow the campus locale; the interface language is separate. */
@Pipe({ name: 'money', pure: false })
export class MoneyPipe implements PipeTransform {
  private ctx = inject(Ctx);
  transform(value: number | null | undefined, currency?: string | null) {
    if (value === null || value === undefined) return '–';
    const c = currency ?? this.ctx.campus()?.currency;
    const locale = this.ctx.campus()?.locale ?? navigator.language;
    try {
      return c ? new Intl.NumberFormat(locale, { style: 'currency', currency: c, numberingSystem: 'latn' } as any).format(value) : new Intl.NumberFormat(locale).format(value);
    } catch {
      return `${value.toFixed(2)} ${c ?? ''}`;
    }
  }
}

@Pipe({ name: 'day', pure: false })
export class DayPipe implements PipeTransform {
  private ctx = inject(Ctx);
  transform(value: string | null | undefined, withTime = false) {
    if (!value) return '–';
    // A plain date ("2026-04-15") must not shift across time zones; a timestamp is UTC and shown in the campus time zone.
    const dateOnly = /^\d{4}-\d{2}-\d{2}$/.test(value);
    const locale = this.ctx.campus()?.locale ?? navigator.language;
    const opts: Intl.DateTimeFormatOptions = { day: 'numeric', month: 'short', year: 'numeric', numberingSystem: 'latn' } as any;
    if (dateOnly) opts.timeZone = 'UTC';
    else {
      if (withTime) { opts.hour = '2-digit'; opts.minute = '2-digit'; }
      const tz = this.ctx.campus()?.timeZone;
      if (tz) opts.timeZone = tz;
    }
    try {
      return new Intl.DateTimeFormat(locale, opts).format(new Date(dateOnly ? value + 'T00:00:00Z' : value));
    } catch {
      return value;
    }
  }
}

/** Today's date (YYYY-MM-DD) in the given IANA time zone, or the device's zone. */
export const today = (timeZone?: string | null) => {
  try { return new Intl.DateTimeFormat('en-CA', { timeZone: timeZone ?? undefined, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date()); }
  catch { return new Intl.DateTimeFormat('en-CA', { year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date()); }
};
export const DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
