import {
  AfterViewInit,
  Component,
  ElementRef,
  EventEmitter,
  Input,
  OnDestroy,
  Output,
  ViewChild,
} from '@angular/core';

let nextDialogId = 0;

@Component({
  selector: 'app-modal',
  template: `
    <dialog
      #dialog
      class="app-dialog"
      [class.dialog-wide]="wide"
      [attr.aria-labelledby]="titleId"
      (cancel)="cancel($event)"
      (click)="backdropClick($event)"
    >
      <section class="dialog-card">
        <header class="panel-heading modal-heading">
          <h2 [id]="titleId">{{ title }}</h2>
          <button
            class="icon-button"
            type="button"
            [attr.aria-label]="'Close ' + title"
            (click)="closed.emit()"
          >
            ×
          </button>
        </header>
        <div class="modal-content"><ng-content /></div>
      </section>
    </dialog>
  `,
})
export class ModalComponent implements AfterViewInit, OnDestroy {
  @Input({ required: true }) title = '';
  @Input() wide = false;
  @Output() readonly closed = new EventEmitter<void>();
  @ViewChild('dialog', { static: true }) private dialog!: ElementRef<HTMLDialogElement>;
  readonly titleId = `meridian-dialog-${++nextDialogId}`;
  private previousFocus: HTMLElement | null = null;

  ngAfterViewInit(): void {
    this.previousFocus =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const dialog = this.dialog.nativeElement;
    // showModal provides native focus trapping, background isolation and Escape handling.
    // jsdom has no native modal implementation; its rendering tests use the open attribute.
    if (typeof dialog.showModal === 'function') dialog.showModal();
    else dialog.setAttribute('open', '');
  }
  ngOnDestroy(): void {
    const dialog = this.dialog.nativeElement;
    if (dialog.open && typeof dialog.close === 'function') dialog.close();
    if (this.previousFocus?.isConnected) this.previousFocus.focus();
  }
  cancel(event: Event): void {
    event.preventDefault();
    this.closed.emit();
  }
  backdropClick(event: MouseEvent): void {
    if (event.target === this.dialog.nativeElement) this.closed.emit();
  }
}
