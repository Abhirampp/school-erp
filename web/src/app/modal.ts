import { Component, ElementRef, effect, input, output, viewChild } from '@angular/core';

/**
 * A modal dialog built on the native <dialog> element, so focus is trapped, Escape closes it and
 * the page behind is inert. The parent controls `open` and clears its own state on `closed`.
 */
@Component({
  selector: 'app-modal',
  template: `
    <dialog #dlg class="modal" [style.width]="width()" [attr.aria-labelledby]="'modal-title'" (close)="closed.emit()" (click)="onBackdrop($event)">
      <header class="modal-head">
        <h2 id="modal-title">{{ title() }}</h2>
        <button type="button" class="btn small" (click)="dlg.close()" aria-label="Close">✕</button>
      </header>
      <div class="modal-body"><ng-content /></div>
    </dialog>
  `,
})
export class Modal {
  open = input(false);
  title = input('');
  width = input('min(760px, 94vw)');
  closed = output<void>();
  private dlg = viewChild.required<ElementRef<HTMLDialogElement>>('dlg');

  constructor() {
    effect(() => {
      const d = this.dlg().nativeElement;
      if (this.open() && !d.open) d.showModal();
      if (!this.open() && d.open) d.close();
    });
  }

  /** A click on the dialog element itself (not its contents) is a click on the backdrop. */
  onBackdrop(e: MouseEvent) {
    if (e.target === this.dlg().nativeElement) this.dlg().nativeElement.close();
  }
}
