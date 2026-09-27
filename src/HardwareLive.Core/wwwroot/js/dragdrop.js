// Edit-mode reordering (docs/SPEC.md step5 feature 4): Pointer Events only (mouse/touch/pen
// unified, no HTML5 DnD API, no libraries) with a drop placeholder and viewport-edge
// auto-scroll, plus a fully independent keyboard path (Tab to a widget, Space/Enter picks it
// up, arrow keys move it, Space/Enter drops, Escape cancels), announced through an aria-live
// region. Both paths call the same `onReorder(fromIndex, toIndex)` callback operating on
// `layout.widgets` indices -- the grid's DOM order always mirrors that array 1:1.

import { announce } from './dom.js';
import { computeKeyboardMoveTarget } from './logic.js';

const AUTO_SCROLL_MARGIN = 60;
const AUTO_SCROLL_SPEED = 12;

export function attachReordering({ gridEl, liveRegionEl, isEditMode, getItemCount, onReorder }) {
  let pointerState = null;
  let keyboardPickedIndex = null;
  let keyboardStartIndex = null;
  let scrollRaf = null;

  function widgetItems() {
    return Array.from(gridEl.querySelectorAll(':scope > .widget'));
  }

  function indexOf(node) {
    return widgetItems().indexOf(node);
  }

  function columnCount() {
    const value = getComputedStyle(gridEl).gridTemplateColumns;
    return value ? value.trim().split(/\s+/).filter(Boolean).length : 1;
  }

  // ---- Pointer drag -----------------------------------------------------------------

  function onPointerDown(event) {
    if (!isEditMode() || event.button !== 0) {
      return;
    }

    const handle = event.target.closest('.drag-handle');
    if (!handle) {
      return;
    }

    const widget = handle.closest('.widget');
    if (!widget) {
      return;
    }

    event.preventDefault();
    const items = widgetItems();
    const fromIndex = items.indexOf(widget);
    if (fromIndex < 0) {
      return;
    }

    handle.setPointerCapture(event.pointerId);
    widget.classList.add('picked-up');

    const placeholder = document.createElement('div');
    placeholder.className = 'drop-placeholder';
    placeholder.setAttribute('data-size', widget.getAttribute('data-size'));
    widget.after(placeholder);

    pointerState = { pointerId: event.pointerId, widget, placeholder, fromIndex, currentIndex: fromIndex };

    handle.addEventListener('pointermove', onPointerMove);
    handle.addEventListener('pointerup', onPointerUp);
    handle.addEventListener('pointercancel', onPointerCancel);
  }

  function onPointerMove(event) {
    if (!pointerState || event.pointerId !== pointerState.pointerId) {
      return;
    }

    maybeAutoScroll(event.clientY);

    const items = widgetItems().filter((item) => item !== pointerState.widget);
    let target = pointerState.placeholder;
    let bestDistance = Infinity;
    let insertBefore = null;

    for (const item of items) {
      const rect = item.getBoundingClientRect();
      const cx = rect.left + rect.width / 2;
      const cy = rect.top + rect.height / 2;
      const distance = Math.hypot(event.clientX - cx, event.clientY - cy);
      if (distance < bestDistance) {
        bestDistance = distance;
        insertBefore = event.clientY < cy || (event.clientY < rect.bottom && event.clientX < cx) ? item : item.nextElementSibling;
      }
    }

    if (insertBefore) {
      gridEl.insertBefore(pointerState.placeholder, insertBefore);
    } else if (target !== gridEl.lastElementChild) {
      gridEl.appendChild(pointerState.placeholder);
    }
  }

  function maybeAutoScroll(clientY) {
    const viewportHeight = window.innerHeight;
    let direction = 0;
    if (clientY < AUTO_SCROLL_MARGIN) {
      direction = -1;
    } else if (clientY > viewportHeight - AUTO_SCROLL_MARGIN) {
      direction = 1;
    }

    if (direction === 0) {
      if (scrollRaf) {
        cancelAnimationFrame(scrollRaf);
        scrollRaf = null;
      }
      return;
    }

    if (scrollRaf) {
      return;
    }

    const step = () => {
      window.scrollBy(0, direction * AUTO_SCROLL_SPEED);
      scrollRaf = requestAnimationFrame(step);
    };
    scrollRaf = requestAnimationFrame(step);
  }

  function onPointerUp(event) {
    if (!pointerState || event.pointerId !== pointerState.pointerId) {
      return;
    }

    const { widget, placeholder, fromIndex } = pointerState;
    const placeholderItems = Array.from(gridEl.children);
    const toIndex = placeholderItems.indexOf(placeholder);
    placeholder.replaceWith(widget);
    widget.classList.remove('picked-up');
    cleanupPointer();

    // Removing the dragged item shifts every later index down by one; the placeholder's
    // position already accounts for that because it was inserted among the *remaining* items.
    const adjustedTo = toIndex > fromIndex ? toIndex - 1 : toIndex;
    if (adjustedTo !== fromIndex && adjustedTo >= 0) {
      onReorder(fromIndex, adjustedTo);
    }
  }

  function onPointerCancel() {
    if (!pointerState) {
      return;
    }

    pointerState.placeholder.remove();
    pointerState.widget.classList.remove('picked-up');
    cleanupPointer();
  }

  function cleanupPointer() {
    if (scrollRaf) {
      cancelAnimationFrame(scrollRaf);
      scrollRaf = null;
    }
    pointerState = null;
  }

  gridEl.addEventListener('pointerdown', onPointerDown);

  // ---- Keyboard reorder ---------------------------------------------------------------

  function onKeyDown(event) {
    if (!isEditMode()) {
      return;
    }

    const widget = event.target.closest('.widget');
    if (!widget || !gridEl.contains(widget)) {
      return;
    }

    const index = indexOf(widget);

    if (keyboardPickedIndex === null) {
      if (event.key === ' ' || event.key === 'Enter') {
        event.preventDefault();
        keyboardPickedIndex = index;
        keyboardStartIndex = index;
        widget.classList.add('picked-up');
        announce(liveRegionEl, `Picked up widget ${index + 1} of ${getItemCount()}. Use arrow keys to move, Enter to drop, Escape to cancel.`);
      }
      return;
    }

    if (event.key === 'Escape') {
      event.preventDefault();
      if (keyboardPickedIndex !== keyboardStartIndex) {
        onReorder(keyboardPickedIndex, keyboardStartIndex);
      }
      finishKeyboardMove(keyboardStartIndex, 'Reorder canceled.');
      return;
    }

    if (event.key === ' ' || event.key === 'Enter') {
      event.preventDefault();
      finishKeyboardMove(keyboardPickedIndex, `Dropped at position ${keyboardPickedIndex + 1}.`);
      return;
    }

    if (['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) {
      event.preventDefault();
      const target = computeKeyboardMoveTarget(keyboardPickedIndex, event.key, columnCount(), getItemCount());
      if (target !== null) {
        onReorder(keyboardPickedIndex, target);
        keyboardPickedIndex = target;
        announce(liveRegionEl, `Moved to position ${target + 1} of ${getItemCount()}.`);
      }
    }
  }

  function finishKeyboardMove(finalIndex, message) {
    for (const item of widgetItems()) {
      item.classList.remove('picked-up');
    }
    keyboardPickedIndex = null;
    keyboardStartIndex = null;
    announce(liveRegionEl, message);
    const items = widgetItems();
    items[finalIndex]?.focus();
  }

  gridEl.addEventListener('keydown', onKeyDown);

  return {
    /** Re-marks the currently picked-up widget after a re-render (element identity changes). */
    refreshPickedUpMarker() {
      if (keyboardPickedIndex === null) {
        return;
      }
      const items = widgetItems();
      items[keyboardPickedIndex]?.classList.add('picked-up');
    },
    detach() {
      gridEl.removeEventListener('pointerdown', onPointerDown);
      gridEl.removeEventListener('keydown', onKeyDown);
    },
  };
}
