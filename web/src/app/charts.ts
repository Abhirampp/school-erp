import { Component, computed, input, signal } from '@angular/core';

/**
 * Small dependency-free charts for the dashboard. Colours are roles defined once on .viz
 * (one blue for magnitude, a neutral track, the reserved "critical" red); text always uses ink colours.
 */
const VIZ_STYLES = `
  :host { display: block; }
  .viz { --series: #2a78d6; --track: #e1e0d9; --critical: #d03b3b; --grid: #e1e0d9; --axis: #c3c2b7; --ink: #1c2430; --ink-2: #52514e; --ink-3: #898781; position: relative; }
  .tip { position: absolute; background: #12202b; color: #fff; padding: 6px 10px; border-radius: 6px; font-size: 12.5px; pointer-events: none; white-space: nowrap; z-index: 2; transform: translate(-50%, -100%); }
  .tip strong { font-weight: 600; }
  .legend { display: flex; gap: 16px; flex-wrap: wrap; font-size: 12.5px; color: var(--ink-2); margin-top: 10px; }
  .key { display: inline-block; width: 10px; height: 10px; border-radius: 2px; margin-inline-end: 6px; }
  details { margin-top: 8px; font-size: 12.5px; color: var(--ink-2); }
  summary { cursor: pointer; }
`;

export interface Point { label: string; value: number; detail?: string }

/** Single-series trend line with a crosshair and tooltip. */
@Component({
  selector: 'line-chart',
  styles: [VIZ_STYLES, `svg { display: block; width: 100%; height: auto; overflow: visible; } text { font: 11px system-ui, sans-serif; fill: var(--ink-3); }`],
  template: `
    <div class="viz" (mouseleave)="hover.set(null)">
      <svg [attr.viewBox]="'0 0 ' + W + ' ' + H" role="img" [attr.aria-label]="title()">
        @for (t of ticks(); track t) {
          <line [attr.x1]="L" [attr.x2]="W - R" [attr.y1]="y(t)" [attr.y2]="y(t)" stroke="var(--grid)" stroke-width="1" />
          <text [attr.x]="L - 8" [attr.y]="y(t) + 4" text-anchor="end">{{ t }}{{ unit() }}</text>
        }
        <line [attr.x1]="L" [attr.x2]="W - R" [attr.y1]="H - B" [attr.y2]="H - B" stroke="var(--axis)" stroke-width="1" />
        @if (points().length > 1) {
          <path [attr.d]="area()" fill="var(--series)" opacity="0.1" />
          <path [attr.d]="line()" fill="none" stroke="var(--series)" stroke-width="2" stroke-linejoin="round" stroke-linecap="round" />
        }
        @if (hover() !== null) { <line [attr.x1]="x(hover()!)" [attr.x2]="x(hover()!)" [attr.y1]="T" [attr.y2]="H - B" stroke="var(--axis)" stroke-width="1" /> }
        @for (p of points(); track $index) {
          <circle [attr.cx]="x($index)" [attr.cy]="y(p.value)" [attr.r]="hover() === $index ? 5 : 4" fill="var(--series)" stroke="#fff" stroke-width="2" />
          @if ($index === 0 || $index === points().length - 1 || $index === mid()) { <text [attr.x]="x($index)" [attr.y]="H - 6" [attr.text-anchor]="$index === 0 ? 'start' : $index === points().length - 1 ? 'end' : 'middle'">{{ p.label }}</text> }
          <rect [attr.x]="x($index) - step() / 2" [attr.y]="T" [attr.width]="step()" [attr.height]="H - B - T" fill="transparent" (mouseenter)="hover.set($index)" (focus)="hover.set($index)" (blur)="hover.set(null)" tabindex="0" [attr.aria-label]="p.label + ': ' + p.value + unit()" />
        }
      </svg>
      @if (hover() !== null) {
        @let p = points()[hover()!];
        <div class="tip" [style.left.%]="(x(hover()!) / W) * 100" [style.top.%]="((y(p.value) - 10) / H) * 100"><strong>{{ p.value }}{{ unit() }}</strong> · {{ p.label }}@if (p.detail) { <div>{{ p.detail }}</div> }</div>
      }
      <details><summary>View as table</summary>
        <table><tbody>@for (p of points(); track $index) { <tr><td>{{ p.label }}</td><td class="num">{{ p.value }}{{ unit() }}</td><td>{{ p.detail }}</td></tr> }</tbody></table>
      </details>
    </div>
  `,
})
export class LineChart {
  points = input.required<Point[]>();
  title = input('');
  unit = input('');
  /** The axis never starts above this value, so small dips are not exaggerated. */
  floor = input(0);
  ceiling = input<number | null>(null);
  hover = signal<number | null>(null);
  W = 640; H = 220; L = 44; R = 12; T = 12; B = 26;

  private domain = computed(() => {
    const values = this.points().map((p) => p.value);
    const max = this.ceiling() ?? Math.max(...values, 1);
    const min = Math.min(this.floor(), Math.floor(Math.min(...values, max) / 10) * 10);
    return { min, max: max === min ? min + 1 : max };
  });
  ticks = computed(() => { const { min, max } = this.domain(); return [min, Math.round((min + max) / 2), max]; });
  mid = computed(() => Math.floor((this.points().length - 1) / 2));
  step = computed(() => (this.W - this.L - this.R) / Math.max(this.points().length - 1, 1));
  x = (i: number) => this.L + i * this.step();
  y = (v: number) => { const { min, max } = this.domain(); return this.T + (1 - (v - min) / (max - min)) * (this.H - this.T - this.B); };
  line = computed(() => this.points().map((p, i) => `${i ? 'L' : 'M'}${this.x(i)} ${this.y(p.value)}`).join(' '));
  area = computed(() => `${this.line()} L${this.x(this.points().length - 1)} ${this.H - this.B} L${this.L} ${this.H - this.B} Z`);
}

export interface Bar { label: string; value: number; max?: number; note?: string }

/** Horizontal bars for comparing a few named things. Every bar is labelled, so nothing depends on colour. */
@Component({
  selector: 'bar-list',
  styles: [VIZ_STYLES, `
    .row { display: grid; grid-template-columns: minmax(84px, 26%) 1fr auto; gap: 10px; align-items: center; padding: 5px 0; border-radius: 4px; }
    .row:hover, .row:focus { background: #f5f6f8; outline: none; }
    .name { font-size: 13px; color: var(--ink-2); text-transform: capitalize; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .track { height: 14px; background: transparent; position: relative; }
    .track.cap { background: var(--track); border-radius: 0 4px 4px 0; }
    .fill { position: absolute; inset-block: 0; inset-inline-start: 0; background: var(--series); border-radius: 0 4px 4px 0; min-width: 2px; }
    :host-context([dir=rtl]) .fill, :host-context([dir=rtl]) .track.cap { border-radius: 4px 0 0 4px; }
    .val { font-size: 13px; color: var(--ink); font-variant-numeric: tabular-nums; min-width: 2.5em; text-align: end; }
    .val small { color: var(--ink-3); font-size: 12px; }
  `],
  template: `
    <div class="viz" role="list" [attr.aria-label]="title()">
      @for (b of bars(); track b.label) {
        <div class="row" role="listitem" tabindex="0" [title]="b.note ?? (b.label + ': ' + b.value)">
          <span class="name">{{ b.label }}</span>
          <span class="track" [class.cap]="b.max !== undefined"><span class="fill" [style.width.%]="width(b)" [style.opacity]="b.value ? 1 : 0"></span></span>
          <span class="val">{{ b.value }}@if (b.max !== undefined) { <small> / {{ b.max }}</small> }</span>
        </div>
      } @empty { <p class="muted">No data yet.</p> }
    </div>
  `,
})
export class BarList {
  bars = input.required<Bar[]>();
  title = input('');
  private scale = computed(() => Math.max(...this.bars().map((b) => b.max ?? b.value), 1));
  width(b: Bar) { return (b.value / (b.max ?? this.scale())) * 100; }
}

export interface Segment { label: string; value: number; kind: 'series' | 'track' | 'critical'; display: string }

/** One part-to-whole bar: how the whole splits into a few labelled parts. */
@Component({
  selector: 'split-bar',
  styles: [VIZ_STYLES, `
    .bar { display: flex; gap: 2px; height: 22px; }
    .seg { min-width: 3px; }
    .seg:first-child { border-start-start-radius: 4px; border-end-start-radius: 4px; }
    .seg:last-child { border-start-end-radius: 4px; border-end-end-radius: 4px; }
    .seg:hover, .seg:focus { filter: brightness(0.92); outline: none; }
    .series { background: var(--series); } .track { background: var(--axis); } .critical { background: var(--critical); }
    .legend strong { color: var(--ink); font-weight: 600; }
    .hero { font-size: 30px; font-weight: 650; color: var(--ink); line-height: 1.2; }
    .hero small { font-size: 13px; font-weight: 400; color: var(--ink-2); }
  `],
  template: `
    <div class="viz" (mouseleave)="hover.set(null)">
      <div class="hero">{{ percent() }}% <small>{{ headline() }}</small></div>
      <div class="bar" role="img" [attr.aria-label]="summary()" style="margin-top:10px">
        @for (s of visible(); track s.label) {
          <div [class]="'seg ' + s.kind" [style.flex-grow]="s.value" tabindex="0" (mouseenter)="hover.set(s)" (focus)="hover.set(s)" (blur)="hover.set(null)" [attr.aria-label]="s.label + ': ' + s.display"></div>
        }
      </div>
      @if (hover(); as h) { <div class="tip" style="left:50%;top:38px"><strong>{{ h.display }}</strong> · {{ h.label }} ({{ share(h) }}%)</div> }
      <div class="legend">
        @for (s of segments(); track s.label) { <span><span [class]="'key ' + s.kind"></span>{{ s.kind === 'critical' ? '⚠ ' : '' }}{{ s.label }} <strong>{{ s.display }}</strong></span> }
      </div>
    </div>
  `,
})
export class SplitBar {
  segments = input.required<Segment[]>();
  headline = input('');
  hover = signal<Segment | null>(null);
  private total = computed(() => this.segments().reduce((sum, s) => sum + s.value, 0) || 1);
  visible = computed(() => this.segments().filter((s) => s.value > 0));
  percent = computed(() => Math.round(((this.segments()[0]?.value ?? 0) / this.total()) * 100));
  share(s: Segment) { return Math.round((s.value / this.total()) * 100); }
  summary = computed(() => this.segments().map((s) => `${s.label} ${s.display}`).join(', '));
}
